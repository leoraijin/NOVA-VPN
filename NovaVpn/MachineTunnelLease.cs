using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace NovaVpn;

public sealed class TunnelLeaseBusyException : InvalidOperationException
{
    public TunnelLeaseBusyException() : base("Не удалось занять VPN-туннель: он уже используется или Windows отказала в доступе к блокировке.") { }
}

// Object existence, not thread ownership, is the lease. The native child inherits
// a handle atomically at CreateProcess, keeping the lease alive after host/UI death.
// This coordinates cooperating NOVA clients; it is not a privileged TUN/WFP security boundary.
public sealed partial class MachineTunnelLease : IDisposable
{
    public const string DesktopScope = "Desktop";
    public const int UnavailableExitCode = unchecked((int)0xE14E4F56);
    Mutex handle; int startAttempted;
    private MachineTunnelLease(Mutex handle) { this.handle = handle; }
    public static string NameFor(string scope) { WindowsUserInstance.ValidatePurpose(scope); return @"Global\NOVA.VPN.TunnelLease.v1." + scope; }
    public static MachineTunnelLease TryCreate(string scope)
    {
        WindowsUserInstance.ValidatePurpose(scope);
        return TryCreateCore(scope,new SecurityIdentifier(WindowsUserInstance.CurrentSid()));
    }
    private static MachineTunnelLease TryCreateCore(string scope,SecurityIdentifier user)
    {
        string name = NameFor(scope);
        var security = new MutexSecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        if(!user.Equals(system))security.AddAccessRule(new MutexAccessRule(system, MutexRights.FullControl, AccessControlType.Allow));
        try
        {
            bool created; var mutex = new Mutex(false, name, out created, security);
            if (!created) { mutex.Dispose(); return null; } // Never adopt, modify or wait on somebody else's object.
            try { WindowsUserInstance.ValidateSecurity(mutex.GetAccessControl(), user); return new MachineTunnelLease(mutex); }
            catch { mutex.Dispose(); throw; }
        }
        catch (UnauthorizedAccessException) { return null; } // Another SID's private lease is busy too.
        catch (WaitHandleCannotBeOpenedException) { return null; } // A name/type conflict never grants ownership.
    }
    public bool StartChild(Process child)
    {
        if (handle == null) throw new ObjectDisposedException(nameof(MachineTunnelLease));
        if (child == null || child.StartInfo.UseShellExecute || !child.StartInfo.RedirectStandardOutput || !child.StartInfo.RedirectStandardError)
            throw new ArgumentException("Наследование блокировки требует управляемый запуск дочернего ядра.");
        return StartOwnedChild(_ => child.Start());
    }
    internal T StartOwnedChild<T>(Func<IntPtr, T> create)
    {
        var owned = handle;
        if (owned == null) throw new ObjectDisposedException(nameof(MachineTunnelLease));
        if (create == null) throw new ArgumentNullException(nameof(create));
        if (Interlocked.CompareExchange(ref startAttempted, 1, 0) != 0) throw new InvalidOperationException("Блокировка уже использована для запуска ядра.");
        var safe = owned.SafeWaitHandle; bool reference = false;
        try
        {
            safe.DangerousAddRef(ref reference); IntPtr native = safe.DangerousGetHandle();
            if (!SetHandleInformation(native, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            T result = default(T); bool created = false;
            try { result = create(native); created = true; return result; }
            finally
            {
                if (!SetHandleInformation(native, 1, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    // A returned owned process must not escape cleanup if
                    // clearing the parent's temporary inherit bit fails.
                    if (created && result is IDisposable disposable) disposable.Dispose();
                    throw new Win32Exception(error);
                }
            }
        }
        finally { if (reference) safe.DangerousRelease(); }
    }
    public void Dispose() { Interlocked.Exchange(ref handle, null)?.Dispose(); }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}

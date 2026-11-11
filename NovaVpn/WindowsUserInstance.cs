using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace NovaVpn;

public sealed class LegacyInstanceDetectedException : InvalidOperationException
{
    public LegacyInstanceDetectedException() : base("Обнаружена блокировка прежней версии NOVA в этом сеансе Windows. Завершите прежний клиент перед открытием этой версии.") { }
}

// One state-writing UI per Windows identity across sessions; never a machine tunnel lock.
public sealed class WindowsUserInstance : IDisposable
{
    static readonly object Gate = new object();
    static readonly Dictionary<string, WindowsUserInstance> Local = new Dictionary<string, WindowsUserInstance>(StringComparer.Ordinal);
    readonly Thread thread; readonly string name; Mutex mutex;
    private WindowsUserInstance(string name, Mutex mutex) { this.name = name; this.mutex = mutex; thread = Thread.CurrentThread; }
    public static string CurrentSid()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User == null || identity.IsAnonymous || identity.IsSystem ||
                identity.User.IsWellKnown(WellKnownSidType.LocalServiceSid) || identity.User.IsWellKnown(WellKnownSidType.NetworkServiceSid))
                throw new UnauthorizedAccessException("Для личного клиента требуется учётная запись пользователя Windows.");
            return identity.User.Value;
        }
    }
    public static string NameFor(string sid, string purpose)
    {
        string canonical = new SecurityIdentifier(sid).Value;
        ValidatePurpose(purpose);
        using (var sha = SHA256.Create())
            return @"Global\NOVA.VPN.UserInstance.v2." + purpose + "." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "");
    }
    internal static void ValidatePurpose(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) throw new ArgumentException("Некорректная область блокировки.");
        foreach (char c in value) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-')) throw new ArgumentException("Некорректная область блокировки.");
    }
    public static WindowsUserInstance TryAcquire(string purpose)
    {
        if (purpose == "Desktop" && LegacyObjectExists("NOVA-VPN-Desktop-Instance")) throw new LegacyInstanceDetectedException();
        var user = new SecurityIdentifier(CurrentSid()); string name = NameFor(user.Value, purpose);
        lock (Gate)
        {
            if (Local.TryGetValue(name, out var previous))
            {
                if (previous.thread.IsAlive) return null;
                previous.mutex?.Dispose(); previous.mutex = null; Local.Remove(name);
            }
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new MutexSecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(system, MutexRights.FullControl, AccessControlType.Allow));
            Mutex handle = null;
            try
            {
                bool created; handle = new Mutex(false, name, out created, security);
                ValidateSecurity(handle.GetAccessControl(), user);
                bool owned; try { owned = handle.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) { handle.Dispose(); return null; }
                var lease = new WindowsUserInstance(name, handle); Local.Add(name, lease); return lease;
            }
            catch { handle?.Dispose(); throw; }
        }
    }
    internal static bool LegacyObjectExists(string name)
    {
        try { using (var existing = Mutex.OpenExisting(name, MutexRights.Synchronize)) return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }
    internal static void ValidateSecurity(MutexSecurity security, SecurityIdentifier user)
    {
        var allowed = new HashSet<string> { user.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
        if (!security.AreAccessRulesProtected || !security.GetOwner(typeof(SecurityIdentifier)).Equals(user)) throw new UnauthorizedAccessException("Недоверенная блокировка экземпляра NOVA.");
        foreach (MutexAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow || rule.MutexRights != MutexRights.FullControl || !allowed.Remove(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("Недоверенные права блокировки экземпляра NOVA.");
        if (allowed.Count != 0) throw new UnauthorizedAccessException("Неполные права блокировки экземпляра NOVA.");
    }
    public void Dispose()
    {
        lock (Gate)
        {
            if (mutex == null) return;
            if (Thread.CurrentThread != thread) throw new InvalidOperationException("Блокировку окна освобождает только владеющий поток.");
            mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; Local.Remove(name);
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace NovaVpn;

// Native mutation is unavailable to the desktop process, even when it is elevated.
// The SCM-registered LocalSystem service must live in the protected default install.
internal static class TrafficGuardServiceIdentity
{
    // ProgramFiles is process-bitness-dependent under WOW64. All participants
    // must address the native machine installation; never trust process environment.
    internal static string InstallRoot => Path.Combine(ReadNativeProgramFiles(), "NOVA VPN");
    private static string ReadNativeProgramFiles()
    {
        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
        using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion", false))
        {
            if (key == null) throw new InvalidDataException("Native Program Files machine configuration is unavailable.");
            return ValidateNativeProgramFiles(key.GetValue("ProgramFilesDir", null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind("ProgramFilesDir"));
        }
    }
    internal static string ValidateNativeProgramFiles(object value, RegistryValueKind kind)
    {
        var text = value as string;
        if (kind != RegistryValueKind.String || text == null || text.Length < 4 || text != text.Trim() || text.Any(Char.IsControl) ||
            !((text[0] >= 'A' && text[0] <= 'Z') || (text[0] >= 'a' && text[0] <= 'z')) || text[1] != ':' || text[2] != '\\' ||
            text.IndexOf(':', 2) >= 0 || text.IndexOfAny(new[] { '%', '*', '?', '"' }) >= 0)
            throw new InvalidDataException("Invalid literal native Program Files machine path.");
        string canonical;
        try { canonical = Path.GetFullPath(text).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
        { throw new InvalidDataException("Invalid native Program Files machine path.", error); }
        if (canonical.Length < 4 || !String.Equals(canonical, text.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native Program Files machine path must be canonical and local.");
        return canonical;
    }
    internal static string ServiceExe => Path.Combine(InstallRoot, "NOVA Traffic Guard.exe");
    internal static string RecoveryExe => Path.Combine(InstallRoot, "NOVA Guard Recovery.exe");
    internal static string SetupExe => Path.Combine(InstallRoot, "NOVA Guard Setup.exe");
    private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        AdminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static void Demand()
    {
        DemandProcess();
        using (var process = Process.GetCurrentProcess())
            ValidateRegisteredProcess((uint)process.Id, true);
        ValidateProtected(InstallRoot, true); ValidateProtected(ServiceExe, false);
        ValidateProtected(Path.Combine(InstallRoot, "NOVA VPN.exe"), false);
    }

    // Read-only client-side server verification. Does not require the desktop
    // process to run as SYSTEM and never changes service configuration or ACLs.
    internal static void ValidateServerProcess(uint processId)
    {
        ValidateRegisteredProcess(processId, false);
        ValidateProtected(InstallRoot, true); ValidateProtected(ServiceExe, false);
        ValidateProtected(Path.Combine(InstallRoot, "NOVA VPN.exe"), false);
    }
    private static void ValidateRegisteredProcess(uint processId, bool allowStarting)
    {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + TrafficGuardCompiler.ServiceName))
            {
                if (key == null || Convert.ToInt32(key.GetValue("Start", -1)) != 2 || !string.Equals(Convert.ToString(key.GetValue("ObjectName")), "LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                    !new[] { ServiceExe, "\"" + ServiceExe + "\"" }.Contains(Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("ImagePath"))).Trim(), StringComparer.OrdinalIgnoreCase) ||
                    !(key.GetValue("DependOnService") is string[] dependencies) || !dependencies.Contains("BFE", StringComparer.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Служба должна быть зарегистрирована для автозапуска с зависимостью BFE.");
            }
            IntPtr manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) throw new UnauthorizedAccessException("SCM недоступен.");
            try
            {
                IntPtr service = OpenService(manager, TrafficGuardCompiler.ServiceName, 4); if (service == IntPtr.Zero) throw new UnauthorizedAccessException("Служба не зарегистрирована.");
                try
                {
                    if (processId == 0 || !QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _) || status.ProcessId != processId || status.Type != 16 || status.State != 4 && !(allowStarting && status.State == 2))
                        throw new UnauthorizedAccessException("Текущий процесс не является службой NOVA в SCM.");
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
    }

    internal static void DemandProcess()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            if (!SystemSid.Equals(identity.User)) throw new UnauthorizedAccessException("Изменять системную защиту может только служба NOVA от LocalSystem.");
        using (var process = Process.GetCurrentProcess())
            if (!string.Equals(process.MainModule.FileName, ServiceExe, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Неизвестный исполняемый файл службы.");
    }

    internal static string PrepareMachineJournalDirectory()
    {
        Demand();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
        for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Каталог состояния службы перенаправлен.");
        var security = new DirectorySecurity(); security.SetOwner(SystemSid); security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdminSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // An attacker-created existing directory is rejected, never adopted by
        // resetting its owner/ACL and trusting the files already placed inside.
        if (!Directory.Exists(root)) Directory.CreateDirectory(root, security);
        ValidateProtected(root, true);
        string directory = Path.Combine(root, "TrafficGuard");
        if (Directory.Exists(directory)) ValidateProtected(directory, true);
        foreach (string name in new[] { "guard-intent.json", "guard-intent.lock" })
        {
            string file = Path.Combine(directory, name);
            if (File.Exists(file)) ValidateProtected(file, false);
        }
        return directory;
    }

    internal static GuardOwnerPolicy ReadOwnerPolicy(string journalDirectory)
    {
        Demand();
        return ReadOwnerPolicyCore(journalDirectory);
    }
    internal static GuardOwnerPolicy ReadOwnerPolicyForRecovery(string journalDirectory)
    {
        DemandRecovery(); return ReadOwnerPolicyCore(journalDirectory);
    }
    private static GuardOwnerPolicy ReadOwnerPolicyCore(string journalDirectory)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
        if (!string.Equals(Path.GetFullPath(journalDirectory), Path.Combine(root, "TrafficGuard"), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Неизвестный каталог политики владельца.");
        string path = Path.Combine(root, "owner-policy.bin");
        if (!File.Exists(path)) return null; // No implicit owner from an arbitrary first connection.
        ValidateProtected(root, true); ValidateProtected(path, false);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length < 34 || stream.Length > 94) throw new InvalidDataException("Неверный размер политики владельца.");
            byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
            while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return GuardOwnerPolicy.Decode(bytes);
        }
    }

    internal static void DemandRecoveryProcess()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Аварийное восстановление требует повышенных прав администратора.");
        using (var process = Process.GetCurrentProcess())
            if (!string.Equals(process.MainModule.FileName, RecoveryExe, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Запустите установленную утилиту восстановления NOVA.");
        // Independent of a damaged/missing client or main service executable.
        ValidateProtected(InstallRoot, true); ValidateProtected(RecoveryExe, false);
    }
    internal static void DemandRecovery()
    {
        DemandRecoveryProcess();
        IntPtr manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(manager, TrafficGuardCompiler.ServiceName, 4);
            if (service == IntPtr.Zero) { int error = Marshal.GetLastWin32Error(); if (error == 1060) return; throw new Win32Exception(error); }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (status.State != 1) throw new InvalidOperationException("Служба NOVA ещё не остановлена; аварийное изменение правил запрещено.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    internal static void StopMainServiceForRecovery()
    {
        DemandRecoveryProcess();
        IntPtr manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(manager, TrafficGuardCompiler.ServiceName, 4 | 32);
            if (service == IntPtr.Zero) { int error = Marshal.GetLastWin32Error(); if (error == 1060) return; throw new Win32Exception(error); }
            try
            {
                bool requested = false; var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (status.State == 1) return;
                    if (status.State == 4 && !requested)
                    {
                        // Never send STOP to an unrelated program registered under
                        // this name. A stopped broken registration can be rescued.
                        ValidateRegisteredProcess(status.ProcessId, false); ValidateProtected(ServiceExe, false);
                        if (!ControlService(service, 1, out _)) { int error = Marshal.GetLastWin32Error(); if (error != 1062) throw new Win32Exception(error); }
                        requested = true;
                    }
                    else if (status.State != 2 && status.State != 3 && status.State != 4) throw new InvalidOperationException("Неизвестное состояние службы NOVA.");
                    Thread.Sleep(100);
                }
                throw new TimeoutException("Служба NOVA не остановилась за 30 секунд. Правила не изменены.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    internal static string PrepareRecoveryJournalDirectory()
    {
        DemandRecovery();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
        GuardMachineJournalSecurity.PrepareDirectory(root);
        return Path.Combine(root, "TrafficGuard");
    }

    internal static void ValidatePlanFiles(TrafficGuardPlan plan)
    {
        if (!plan.CoreAuthorityCurrent()) throw new UnauthorizedAccessException("Owned core authority is no longer live.");
        string[] catalog = { ServiceExe, Path.Combine(InstallRoot, "core", "sing-box.exe"), Path.Combine(InstallRoot, "core", "reality-client.exe") };
        foreach (string app in plan.Filters.SelectMany(f => f.Conditions).Where(c => c.Field == GuardField.Application).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.Contains(app, StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("В исключениях разрешены только защищённые компоненты NOVA.");
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(app)); directory != null && directory.FullName.Length >= InstallRoot.Length; directory = directory.Parent)
                ValidateProtected(directory.FullName, string.Equals(directory.FullName, InstallRoot, StringComparison.OrdinalIgnoreCase));
            ValidateProtected(app, false);
        }
        if (plan.TunnelLuid != 0)
        {
            ulong luid = plan.TunnelLuid;
            if (ConvertInterfaceLuidToGuid(ref luid, out Guid actual) != 0 || actual != plan.TunnelInterfaceId) throw new UnauthorizedAccessException("TUN-интерфейс изменился.");
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => Guid.TryParse(n.Id, out Guid id) && id == actual);
            if (adapter == null || plan.CoreBinding?.Tunnel == null && adapter.Name != "NOVA" || (int)adapter.NetworkInterfaceType != 53 || adapter.OperationalStatus != OperationalStatus.Up)
                throw new UnauthorizedAccessException("Разрешение выдаётся только активному виртуальному интерфейсу NOVA.");
        }
    }

    internal static void ValidateProtected(string path, bool protectedRoot)
    {
        bool directory = Directory.Exists(path);
        if (!directory && !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Защищённый компонент отсутствует или перенаправлен.");
        FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!owner.Equals(SystemSid) && !owner.Equals(AdminSid) || protectedRoot && !security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Каталог службы должен принадлежать SYSTEM/Administrators и иметь защищённые ACL.");
        var write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 && !rule.IdentityReference.Equals(SystemSid) && !rule.IdentityReference.Equals(AdminSid))
                throw new UnauthorizedAccessException("Обычная учётная запись не должна изменять компоненты службы.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Controls, ExitCode, SpecificExit, CheckPoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicServiceStatus { public uint Type, State, Controls, ExitCode, SpecificExit, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint control, out BasicServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int info, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceLuidToGuid(ref ulong luid, out Guid id);
}

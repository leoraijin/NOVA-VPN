using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

// Kept compatible with the framework compiler used by Make-Installer.ps1.
internal static class InstallerFileSafety
{
    private static readonly Lazy<string> nativeProgramFiles = new Lazy<string>(ReadNativeProgramFiles);
    internal static string NativeProgramFiles { get { return nativeProgramFiles.Value; } }
    internal static string DefaultRoot { get { return Path.Combine(NativeProgramFiles, "NOVA VPN"); } }
    private static string ReadNativeProgramFiles()
    {
        RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
        using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion", false))
        {
            if (key == null) throw new InvalidDataException("Native Program Files configuration is unavailable.");
            string value = key.GetValue("ProgramFilesDir", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (key.GetValueKind("ProgramFilesDir") != RegistryValueKind.String || value == null || value.Length < 4 || value != value.Trim() || value.Any(Char.IsControl) ||
                !((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z')) || value[1] != ':' || value[2] != '\\' ||
                value.IndexOf(':', 2) >= 0 || value.IndexOfAny(new[] { '%', '*', '?', '"' }) >= 0)
                throw new InvalidDataException("Invalid native Program Files configuration.");
            string canonical = Path.GetFullPath(value).TrimEnd('\\');
            if (canonical.Length < 4 || !String.Equals(canonical, value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native Program Files path must be canonical and local.");
            return canonical;
        }
    }
    internal static string ValidateRoot(string value)
    {
        if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Не указана папка установки.");
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (expanded.Length < 3 || expanded[1] != ':' || expanded[2] != '\\' || !Char.IsLetter(expanded[0]) || expanded.IndexOf(':', 2) >= 0 || expanded.Any(Char.IsControl))
            throw new InvalidDataException("Требуется абсолютная папка на локальном диске.");
        foreach (string segment in expanded.Substring(3).Replace('/', '\\').Split('\\'))
            if (segment.Length != 0 && (segment == "." || segment == ".." || segment != segment.TrimEnd(' ', '.')))
                throw new InvalidDataException("Папка установки содержит неоднозначный сегмент.");
        string full = Path.GetFullPath(expanded).TrimEnd('\\', '/');
        if (File.Exists(full)) throw new InvalidDataException("Вместо папки выбран файл.");
        if (String.Equals(full, Path.GetPathRoot(full).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выберите отдельную папку NOVA внутри диска.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (String.Equals(full, windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Папка Windows не подходит для установки.");
        if (String.Equals(full, NativeProgramFiles, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выберите отдельную папку NOVA, а не общую папку Windows или профиля.");
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string shared = Environment.GetFolderPath(special);
            if (!String.IsNullOrEmpty(shared) && String.Equals(full, shared.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выберите отдельную папку NOVA, а не общую папку Windows или профиля.");
        }
        for (var parent = new DirectoryInfo(full); parent != null; parent = parent.Parent)
            CheckReparse(parent.FullName);
        return full;
    }
    private static void CheckReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Путь установки перенаправлен ссылкой или junction: " + path);
    }
    internal static string Resolve(string root, string relative)
    {
        root = ValidateRoot(root);
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.Any(Char.IsControl)) throw new InvalidDataException("Небезопасный путь компонента.");
        string[] parts = relative.Replace('/', '\\').Split('\\');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part == "." || part == ".." || part != part.TrimEnd(' ', '.') || part.IndexOfAny(new[] { '<', '>', '"', '|', '*', '?' }) >= 0) throw new InvalidDataException("Неоднозначный путь компонента.");
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" }.Contains(stem) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".IndexOf(stem[3]) >= 0)
                throw new InvalidDataException("Имя компонента совпадает с устройством Windows.");
        }
        string candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Компонент находится вне папки установки.");
        string cursor = root;
        foreach (string part in parts) { cursor = Path.Combine(cursor, part); CheckReparse(cursor); }
        return candidate;
    }
    // Internal journal paths use Resolve; payloads must never name recovery objects.
    internal static string ResolvePayload(string root, string relative)
    {
        string target = Resolve(root, relative);
        foreach (string part in relative.Replace('/', '\\').Split('\\'))
            if (part.Equals(".nova-install.lock", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".nova-update-", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".nova-remove-", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".nova-removed-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Reserved installer coordination or recovery path.");
        return target;
    }
    internal static string[] ReadUninstallPaths(string root)
    {
        string manifest = Resolve(root, "install-manifest.txt");
        var info = new FileInfo(manifest);
        if (!info.Exists || info.Length == 0 || info.Length > 4 * 1024 * 1024) throw new InvalidDataException("Не найден корректный манифест; файлы и регистрация не изменены.");
        string[] entries = File.ReadAllLines(manifest, new System.Text.UTF8Encoding(false, true));
        return ParseUninstallPaths(root, entries);
    }
    internal static string[] ParseUninstallPaths(string root, string[] entries)
    {
        if (entries.Length > 100000) throw new InvalidDataException("Манифест слишком велик.");
        foreach (string anchor in new[] { "NOVA VPN.exe", "NOVA VPN Uninstaller.exe", "install-manifest.txt" })
            if (!entries.Contains(anchor, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Манифест не подтверждает состав установки NOVA.");
        // Freeze and validate the entire list before deleting even the first file,
        // shortcut, task or registration record. No partial validation in a loop.
        return entries.Select(entry => ResolvePayload(root, entry)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal interface IInstallerGuardProbe
{
    bool ServiceRegistered();
    bool GuardFilesPresent(string root);
    bool TrustedMachineStatePresent();
}
internal static class InstallerGuardSafety
{
    internal static void DemandCanModify(string root) { DemandCanModify(root, new WindowsGuardProbe()); }
    internal static void DemandCanModify(string root, IInstallerGuardProbe probe)
    {
        InstallerFileSafety.ValidateRoot(root);
        if (probe.ServiceRegistered() || probe.GuardFilesPresent(root) || probe.TrustedMachineStatePresent())
            throw new InvalidOperationException("Обнаружены компоненты системной защиты NOVA. Этот установщик не изменяет их: требуется обслуживание с поддержкой Guard. Файлы и регистрация сохранены.");
    }
    private sealed class WindowsGuardProbe : IInstallerGuardProbe
    {
        public bool ServiceRegistered()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NovaVpnTrafficGuard")) return key != null;
        }
        public bool GuardFilesPresent(string root)
        {
            return File.Exists(Path.Combine(root, "NOVA Traffic Guard.exe")) || File.Exists(Path.Combine(root, "NOVA Guard Recovery.exe")) || File.Exists(Path.Combine(root, "NOVA Guard Setup.exe"));
        }
        public bool TrustedMachineStatePresent()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            var security = Directory.GetAccessControl(root); var owner = security.GetOwner(typeof(SecurityIdentifier));
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null); var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (!owner.Equals(system) && !owner.Equals(admin) || !security.AreAccessRulesProtected) return false;
            // Inspect parent entries even when an older SYSTEM-only child cannot
            // be opened by this installer. Unknown protected state is not Off.
            return Directory.EnumerateFileSystemEntries(root).Any(path => String.Equals(Path.GetFileName(path), "TrafficGuard", StringComparison.OrdinalIgnoreCase) || String.Equals(Path.GetFileName(path), "owner-policy.bin", StringComparison.OrdinalIgnoreCase));
        }
    }
}

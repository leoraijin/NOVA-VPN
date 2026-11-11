using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

internal sealed class InstallerShortcutChange
{
    public int Slot;
    public string Path, NewHash, OldHash, OldAccess;
    public bool Existed;
}
internal sealed class InstallerShortcutJournal
{
    public string UserSid;
    public int Applied, Attempting = -1;
    public List<InstallerShortcutChange> Items = new List<InstallerShortcutChange>();
}
internal interface IInstallerShortcutEnvironment
{
    string UserSid { get; }
    string Resolve(int slot);
    void Create(string destination, string installRoot, int slot, bool brandedIcon);
    void Notify(string path);
}
internal sealed class WindowsInstallerShortcutEnvironment : IInstallerShortcutEnvironment
{
    public string UserSid { get { using (var identity = WindowsIdentity.GetCurrent()) return identity.User.Value; } }
    public string Resolve(int slot)
    {
        string folder, name;
        switch (slot)
        {
            case 0: folder = Path.Combine(KnownFolder(Environment.SpecialFolder.CommonPrograms), "NOVA VPN"); name = "NOVA VPN.lnk"; break;
            case 1: folder = Path.Combine(KnownFolder(Environment.SpecialFolder.CommonPrograms), "NOVA VPN"); name = "Удалить NOVA VPN.lnk"; break;
            case 2: folder = KnownFolder(Environment.SpecialFolder.CommonDesktopDirectory); name = "NOVA VPN.lnk"; break;
            case 3: folder = KnownFolder(Environment.SpecialFolder.DesktopDirectory); name = "NOVA VPN.lnk"; break;
            default: throw new InvalidDataException("Unexpected installer shortcut slot.");
        }
        return InstallerShortcutTransaction.ValidatePath(Path.Combine(folder, name));
    }
    private static string KnownFolder(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        if (String.IsNullOrEmpty(path)) throw new InvalidDataException("Windows shortcut folder unavailable.");
        return path;
    }
    public void Create(string destination, string installRoot, int slot, bool brandedIcon)
    {
        if (slot < 0 || slot > 3 || File.Exists(destination) || !destination.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid staged shortcut.");
        string target = Path.Combine(installRoot, slot == 1 ? "NOVA VPN Uninstaller.exe" : "NOVA VPN.exe");
        string icon = brandedIcon ? Path.Combine(installRoot, "Assets", "nova-desktop-v3.ico") : target;
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("В Windows недоступно создание ярлыков.");
        object shell = Activator.CreateInstance(shellType), shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { destination });
            Type type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { installRoot });
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
            type.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { slot == 1 ? "--uninstall" : "" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        // The COM writer has closed its handle; publish its bytes durably before
        // recording their hash in the transaction journal.
        using (var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
    }
    public void Notify(string path) { SHChangeNotify(0x00002000, 0x0005, path, null); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, string item2);
}
internal static class InstallerShortcutTransaction
{
    private const long MaximumShortcutBytes = 1024 * 1024;
    internal static string ValidatePath(string path)
    {
        if (String.IsNullOrEmpty(path) || path.Length < 4 || !Char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0 || path.Any(Char.IsControl) || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid shortcut destination.");
        string full = Path.GetFullPath(path);
        if (!String.Equals(full, path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ambiguous shortcut destination.");
        // Validate a child of the containing folder so shared known-folder roots
        // remain usable, while Windows/reparse/ambiguous ancestors are rejected.
        InstallerFileSafety.ValidateRoot(Path.Combine(Path.GetDirectoryName(full), ".nova-shortcut-path-check"));
        foreach (string segment in path.Substring(3).Split('\\'))
            if (segment.Length == 0 || segment != segment.TrimEnd(' ', '.') || segment.IndexOfAny(new[] { '<', '>', '"', '|', '*', '?' }) >= 0) throw new InvalidDataException("Ambiguous shortcut path segment.");
        for (string cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected shortcut path.");
        if (Directory.Exists(path)) throw new InvalidDataException("Shortcut destination is a directory.");
        return full;
    }
    private static string Item(string stage, string prefix, int index)
    { return Path.Combine(stage, "shortcut-" + prefix + index.ToString("D6") + ".lnk"); }
    private static string Hash(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > MaximumShortcutBytes) throw new InvalidDataException("Invalid shortcut file.");
        return InstallerFileTransaction.Hash(path);
    }
    internal static InstallerShortcutJournal Prepare(string root, string stage, IInstallerShortcutEnvironment environment, bool desktopRequested, bool brandedIcon)
    {
        var journal = new InstallerShortcutJournal { UserSid = environment.UserSid };
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot < 4; slot++)
        {
            string target = ValidatePath(environment.Resolve(slot));
            if (slot == 2 && !desktopRequested && !File.Exists(target) || slot == 3 && !File.Exists(target)) continue;
            // A redirected personal desktop may resolve to the common desktop.
            if (!destinations.Add(target)) continue;
            int index = journal.Items.Count;
            var entry = new InstallerShortcutChange { Slot = slot, Path = target, Existed = File.Exists(target) };
            if (entry.Existed)
            {
                entry.OldHash = Hash(target);
                entry.OldAccess = File.GetAccessControl(target, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                using (var source = File.OpenRead(target)) InstallerFileTransaction.CopyDurable(source, Item(stage, "old-", index));
                if (Hash(Item(stage, "old-", index)) != entry.OldHash) throw new IOException("Shortcut changed during preparation.");
            }
            environment.Create(Item(stage, "new-", index), root, slot, brandedIcon);
            entry.NewHash = Hash(Item(stage, "new-", index));
            journal.Items.Add(entry);
        }
        Validate(journal, environment); return journal;
    }
    internal static void Validate(InstallerShortcutJournal journal, IInstallerShortcutEnvironment environment)
    {
        if (journal == null || String.IsNullOrEmpty(journal.UserSid) || journal.UserSid != environment.UserSid || journal.Items == null || journal.Items.Count < 2 || journal.Items.Count > 4 || journal.Applied < 0 || journal.Applied > journal.Items.Count || journal.Attempting < -1 || journal.Attempting >= journal.Items.Count || journal.Attempting != -1 && journal.Attempting != journal.Applied) throw new InvalidDataException("Invalid shortcut transaction checkpoint or user context.");
        new SecurityIdentifier(journal.UserSid);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int previous = -1;
        for (int i = 0; i < journal.Items.Count; i++)
        {
            var item = journal.Items[i];
            if (item == null || item.Slot <= previous || item.Slot < 0 || item.Slot > 3 || i < 2 && item.Slot != i || !String.Equals(item.Path, environment.Resolve(item.Slot), StringComparison.OrdinalIgnoreCase) || !destinations.Add(ValidatePath(item.Path)) || !InstallerFileTransaction.IsHash(item.NewHash) || item.Existed && (!InstallerFileTransaction.IsHash(item.OldHash) || String.IsNullOrEmpty(item.OldAccess)) || !item.Existed && (item.OldHash != null || item.OldAccess != null)) throw new InvalidDataException("Invalid shortcut transaction record.");
            if (item.Existed) { var access = new FileSecurity(); access.SetSecurityDescriptorSddlForm(item.OldAccess, AccessControlSections.Access); }
            previous = item.Slot;
        }
    }
    internal static void Apply(InstallerShortcutJournal journal, string stage, IInstallerShortcutEnvironment environment, Action save)
    {
        if (journal.Applied != 0 || journal.Attempting != -1) throw new InvalidOperationException("Shortcuts already attempted.");
        for (int i = 0; i < journal.Items.Count; i++)
        {
            var item = journal.Items[i]; string target = ValidatePath(item.Path);
            if (item.Existed ? !File.Exists(target) || Hash(target) != item.OldHash : File.Exists(target)) throw new IOException("Shortcut changed during preparation.");
            if (Hash(Item(stage, "new-", i)) != item.NewHash) throw new InvalidDataException("Staged shortcut hash mismatch.");
            journal.Attempting = i; save();
            Directory.CreateDirectory(Path.GetDirectoryName(target)); ValidatePath(target);
            if (item.Existed) File.Replace(Item(stage, "new-", i), target, null); else File.Move(Item(stage, "new-", i), target);
            if (!item.Existed) InstallerFileTransaction.MakeNewFileReadable(target);
            if (Hash(target) != item.NewHash) throw new IOException("Shortcut write verification failed.");
            journal.Applied = i + 1; journal.Attempting = -1; save(); Notify(environment, target);
        }
    }
    internal static void Rollback(InstallerShortcutJournal journal, string stage, IInstallerShortcutEnvironment environment, Action save)
    {
        for (int i = Math.Max(journal.Applied - 1, journal.Attempting); i >= 0; i--)
        {
            var item = journal.Items[i]; string target = ValidatePath(item.Path);
            bool alreadyRestored = File.Exists(target) && item.Existed && Hash(target) == item.OldHash;
            if (!alreadyRestored && File.Exists(target) && Hash(target) != item.NewHash) throw new IOException("Shortcut rollback preserved an external edit: " + item.Path);
            if (item.Existed)
            {
                if (!alreadyRestored)
                {
                    if (Hash(Item(stage, "old-", i)) != item.OldHash) throw new InvalidDataException("Shortcut backup damaged.");
                    string restore = Item(stage, "restore-", i); if (File.Exists(restore)) File.Delete(restore);
                    using (var backup = File.OpenRead(Item(stage, "old-", i))) InstallerFileTransaction.CopyDurable(backup, restore);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); ValidatePath(target);
                    if (File.Exists(target)) File.Replace(restore, target, null); else File.Move(restore, target);
                }
                InstallerFileTransaction.RestoreAccess(target, item.OldAccess);
                if (Hash(target) != item.OldHash) throw new IOException("Shortcut rollback verification failed.");
            }
            else if (File.Exists(target)) File.Delete(target);
            journal.Applied = i; journal.Attempting = -1; save(); Notify(environment, target);
        }
    }
    private static void Notify(IInstallerShortcutEnvironment environment, string path)
    { try { environment.Notify(path); } catch { /* Advisory shell refresh must not change the durable outcome. */ } }
}

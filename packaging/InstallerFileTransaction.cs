using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class InstallerFileChange
{
    public string Relative, NewHash, OldHash, OldAccess, OldSecurity, OldIdentity, NewIdentity;
    public int OldAttributes;
    public bool Existed;
}
internal sealed class InstallerFileJournal
{
    public int Schema = 1, Applied, Attempting = -1;
    public string Phase = "prepared", Root;
    public List<InstallerFileChange> Files = new List<InstallerFileChange>();
    public InstallerRegistryJournal Registry;
    public InstallerShortcutJournal Shortcuts;
}

// File and uninstall-metadata transaction for the legacy installer.
// SCM compensation is still pending; this never operates on WFP.
internal sealed class InstallerFileTransaction : IDisposable
{
    private readonly string root, directory;
    private readonly InstallerFileJournal journal;
    private bool published, complete;
    internal Action<string> Checkpoint { get; set; }
    void Hit(string label) { if (Checkpoint != null) Checkpoint(label); }
    private FileStream rootLease;
    private readonly IInstallerRegistryStore registryStore;
    private IDisposable registryLease;
    private readonly IInstallerShortcutEnvironment shortcutEnvironment;
    private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), AdminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier Actor()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            return identity.IsSystem ? SystemSid : new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? AdminSid : identity.User;
    }
    private static void PrivateDirectory(string path)
    {
        var acl = new DirectorySecurity(); acl.SetOwner(Actor()); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Actor(), SystemSid }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(path, acl); ValidatePrivate(path);
    }
    private static void ValidatePrivate(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected transaction directory.");
        var acl = Directory.GetAccessControl(path);
        if (!acl.AreAccessRulesProtected || !acl.GetOwner(typeof(SecurityIdentifier)).Equals(Actor())) throw new UnauthorizedAccessException("Untrusted transaction owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(Actor()) && !rule.IdentityReference.Equals(SystemSid)) throw new UnauthorizedAccessException("Untrusted transaction access.");
    }
    private InstallerFileTransaction(string root, string directory, InstallerFileJournal journal, IInstallerRegistryStore registryStore, IInstallerShortcutEnvironment shortcutEnvironment)
    { this.root = root; this.directory = directory; this.journal = journal; this.registryStore = registryStore; this.shortcutEnvironment = shortcutEnvironment; published = true; }
    internal InstallerFileTransaction(string root, IInstallerRegistryStore registryStore = null, IInstallerShortcutEnvironment shortcutEnvironment = null)
        : this(root, AcquireRootLease(root), registryStore ?? new WindowsInstallerRegistryStore(), shortcutEnvironment ?? new WindowsInstallerShortcutEnvironment()) { }
    private InstallerFileTransaction(string root, FileStream lease, IInstallerRegistryStore registryStore, IInstallerShortcutEnvironment shortcutEnvironment)
    {
        rootLease = lease; this.registryStore = registryStore; this.shortcutEnvironment = shortcutEnvironment;
        try
        {
            this.root = InstallerFileSafety.ValidateRoot(root); Directory.CreateDirectory(this.root);
            directory = Path.Combine(this.root, ".nova-update-" + Guid.NewGuid().ToString("N"));
            PrivateDirectory(directory); journal = new InstallerFileJournal { Root = this.root, Schema = 4 };
            using (var marker = new MemoryStream(Encoding.ASCII.GetBytes("NOVA update preparation v4"))) CopyDurable(marker, Path.Combine(directory, "preparing"));
        }
        catch { lease.Dispose(); throw; }
    }
    internal static FileStream AcquireRootLease(string root)
    {
        root = InstallerFileSafety.ValidateRoot(root); Directory.CreateDirectory(root);
        return NovaVpn.WindowsInstallationLease.AcquireMaintenance(root);
    }
    internal static InstallerFileTransaction BeginRecovered(string root, Action quiesce = null)
    {
        var lease = AcquireRootLease(root);
        var store = new WindowsInstallerRegistryStore();
        var shortcuts = new WindowsInstallerShortcutEnvironment();
        try { if (quiesce != null) quiesce(); RecoverCore(root, store, shortcuts); return new InstallerFileTransaction(root, lease, store, shortcuts); }
        catch { lease.Dispose(); throw; }
    }
    private string Item(string prefix, int index) { return Path.Combine(directory, prefix + index.ToString("D6")); }
    internal static string Hash(string path)
    { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    internal static bool IsHash(string value) { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F'); }
    private static string PayloadTarget(string root, string relative)
    {
        return InstallerFileSafety.ResolvePayload(root, relative);
    }
    internal static void CopyDurable(Stream input, string destination)
    {
        var access = new FileSecurity(); access.SetOwner(Actor()); access.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Actor(), SystemSid }) access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        using (var output = new FileStream(destination, FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 65536, FileOptions.WriteThrough, access)) { input.CopyTo(output); output.Flush(true); }
    }
    internal void Add(string relative, Stream input)
    {
        if (published || complete) throw new InvalidOperationException("Transaction already prepared.");
        string target = PayloadTarget(root, relative);
        if (journal.Files.Any(file => String.Equals(file.Relative, relative.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Duplicate payload path.");
        foreach (var file in journal.Files)
            if (target.StartsWith(PayloadTarget(root, file.Relative) + "\\", StringComparison.OrdinalIgnoreCase) || PayloadTarget(root, file.Relative).StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File/directory payload collision.");
        int index = journal.Files.Count;
        CopyDurable(input, Item("new-", index));
        var entry = new InstallerFileChange { Relative = relative.Replace('/', '\\'), NewHash = Hash(Item("new-", index)), NewIdentity = FileIdentity(Item("new-", index)), Existed = File.Exists(target) };
        if (entry.Existed)
        {
            entry.OldAccess = File.GetAccessControl(target, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            entry.OldSecurity = FullSecurity(target); entry.OldIdentity = FileIdentity(target); entry.OldAttributes = (int)File.GetAttributes(target);
            entry.OldHash = Hash(target);
        }
        journal.Files.Add(entry);
    }
    internal void PrepareRegistry(string version, bool desktopShortcut)
    {
        if (published || complete || journal.Registry != null) throw new InvalidOperationException("Registry metadata already prepared.");
        AcquireRegistryLease();
        long bytes = Enumerable.Range(0, journal.Files.Count).Sum(i => new FileInfo(Item("new-", i)).Length);
        journal.Registry = InstallerRegistryTransaction.Prepare(registryStore, root, version, desktopShortcut, bytes);
        if (journal.Schema < 4) journal.Schema = 2;
    }
    internal void PrepareShortcuts(bool desktopRequested)
    {
        if (published || complete || journal.Registry == null || journal.Shortcuts != null) throw new InvalidOperationException("Prepare registration before shortcuts, before applying files.");
        bool brandedIcon = journal.Files.Any(file => String.Equals(file.Relative, @"Assets\nova-desktop-v3.ico", StringComparison.OrdinalIgnoreCase)) || File.Exists(InstallerFileSafety.Resolve(root, @"Assets\nova-desktop-v3.ico"));
        journal.Shortcuts = InstallerShortcutTransaction.Prepare(root, directory, shortcutEnvironment, desktopRequested, brandedIcon);
        if (journal.Schema < 4) journal.Schema = 3;
    }
    internal void ApplyShortcuts()
    {
        if (!published || complete || journal.Applied != journal.Files.Count || journal.Attempting != -1 || journal.Shortcuts == null || journal.Registry.Applied != 0 || journal.Registry.Attempting != -1) throw new InvalidOperationException("Apply files before shortcuts, before registration.");
        InstallerShortcutTransaction.Validate(journal.Shortcuts, shortcutEnvironment);
        InstallerShortcutTransaction.Apply(journal.Shortcuts, directory, shortcutEnvironment, Save);
    }
    private void AcquireRegistryLease()
    {
        var windows = registryStore as WindowsInstallerRegistryStore;
        if (windows != null && registryLease == null) registryLease = windows.AcquireLease();
    }
    private void ReleaseRegistryLease()
    { if (registryLease != null) { registryLease.Dispose(); registryLease = null; } }
    internal void ApplyRegistry()
    {
        if (!published || complete || journal.Applied != journal.Files.Count || journal.Attempting != -1 || journal.Registry == null) throw new InvalidOperationException("Files must be fully applied before registration.");
        if (journal.Shortcuts != null && (journal.Shortcuts.Applied != journal.Shortcuts.Items.Count || journal.Shortcuts.Attempting != -1)) throw new InvalidOperationException("Shortcuts must be fully applied before registration.");
        InstallerRegistryTransaction.Validate(journal.Registry, root);
        InstallerRegistryTransaction.Apply(journal.Registry, registryStore, Save);
    }
    private void Save()
    {
        ValidatePrivate(directory);
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(journal));
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Transaction journal too large.");
        string next = Path.Combine(directory, ".journal.next"), path = Path.Combine(directory, "journal.json");
        // This private directory has one writer under the root lease. A short,
        // reusable staging name avoids consuming another GUID's worth of MAX_PATH
        // and safely replaces an unpublished partial write left by a crash.
        if (File.Exists(next))
        {
            if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected temporary journal.");
            File.Delete(next);
        }
        try { using (var stream = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } if (File.Exists(path)) File.Replace(next, path, null); else File.Move(next, path); }
        finally { if (File.Exists(next)) File.Delete(next); }
    }
    internal void Apply()
    {
        if (published || complete) throw new InvalidOperationException("Transaction already applied.");
        journal.Phase = "applying"; Save(); published = true; Hit("apply.intent");
        using (var active = new MemoryStream(Encoding.ASCII.GetBytes("NOVA update active v4"))) CopyDurable(active, Path.Combine(directory, "active")); Hit("apply.active");
        File.Delete(Path.Combine(directory, "preparing")); Hit("apply.ready");
        for (int i = 0; i < journal.Files.Count; i++)
        {
            var file = journal.Files[i]; string target = PayloadTarget(root, file.Relative);
            if (file.Existed ? !File.Exists(target) || Hash(target) != file.OldHash : File.Exists(target) || Directory.Exists(target)) throw new IOException("Installed file changed during preparation: " + file.Relative);
            if (file.Existed && !OriginalMatches(target, file)) throw new IOException("Original metadata changed during preparation.");
            if (FileIdentity(Item("new-", i)) != file.NewIdentity || Hash(Item("new-", i)) != file.NewHash) throw new InvalidDataException("Staged payload hash mismatch.");
            journal.Attempting = i; Save();
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (file.Existed)
            {
                Hit("retain.before:" + i); File.Move(target, Retained(i)); Hit("retain.after:" + i);
                if (!OriginalMatches(Retained(i), file)) throw new IOException("Retained original verification failed.");
            }
            File.Move(Item("new-", i), target); Hit("install.after:" + i);
            // New files moved from a private stage must become executable/readable
            // by ordinary users. Replacements receive the captured original DACL.
            if (!file.Existed) MakeNewFileReadable(target); else RestoreAccess(target, file.OldAccess); Hit("access.after:" + i);
            if (Hash(target) != file.NewHash) throw new IOException("Installed file verification failed.");
            journal.Applied = i + 1; journal.Attempting = -1; Save(); Hit("apply.recorded:" + i);
        }
    }
    internal void Complete()
    {
        if (!published || journal.Applied != journal.Files.Count) throw new InvalidOperationException("Files not fully applied.");
        if (journal.Registry != null && (journal.Registry.Applied != journal.Registry.Values.Count || journal.Registry.Attempting != -1)) throw new InvalidOperationException("Registry metadata not fully applied.");
        if (journal.Shortcuts != null && (journal.Shortcuts.Applied != journal.Shortcuts.Items.Count || journal.Shortcuts.Attempting != -1)) throw new InvalidOperationException("Shortcuts not fully applied.");
        Hit("commit.before"); journal.Phase = "committed"; Save(); complete = true; Hit("commit.after");
        try { Cleanup(); } finally { ReleaseRegistryLease(); if (rootLease != null) { rootLease.Dispose(); rootLease = null; } }
    }
    private void Rollback()
    {
        if (journal.Registry != null) { AcquireRegistryLease(); InstallerRegistryTransaction.Rollback(journal.Registry, registryStore, Save); }
        if (journal.Shortcuts != null) InstallerShortcutTransaction.Rollback(journal.Shortcuts, directory, shortcutEnvironment, Save);
        for (int i = Math.Max(journal.Applied - 1, journal.Attempting); i >= 0; i--)
        {
            var file = journal.Files[i]; string target = PayloadTarget(root, file.Relative);
            if (journal.Schema == 4) { Hit("restore.before:" + i); RestoreOriginal(i); Hit("restore.after:" + i); continue; }
            if (File.Exists(target))
            {
                string current = Hash(target);
                if (file.Existed && current == file.OldHash) { RestoreAccess(target, file.OldAccess); continue; }
                if (current != file.NewHash) throw new IOException("Rollback preserved an externally changed file: " + file.Relative);
            }
            if (file.Existed)
            {
                if (!File.Exists(Item("old-", i)) || Hash(Item("old-", i)) != file.OldHash) throw new InvalidDataException("Rollback backup missing or damaged.");
                string restore = Item("restore-", i); if (File.Exists(restore)) File.Delete(restore);
                using (var backup = File.OpenRead(Item("old-", i))) CopyDurable(backup, restore);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target)) File.Replace(restore, target, null); else File.Move(restore, target);
                RestoreAccess(target, file.OldAccess);
                if (Hash(target) != file.OldHash) throw new IOException("Rollback verification failed.");
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        journal.Phase = "rolled-back"; Save(); complete = true; Hit("rollback.recorded"); Cleanup();
    }
    string Retained(int index)
    {
        string target = PayloadTarget(root, journal.Files[index].Relative);
        return Path.Combine(Path.GetDirectoryName(target), Path.GetFileName(directory) + "-" + index.ToString("D6") + ".old");
    }
    static string FullSecurity(string path)
    {
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        return File.GetAccessControl(path, sections).GetSecurityDescriptorSddlForm(sections);
    }
    static bool ExistsOrdinary(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new IOException("Unexpected original/recovery object type.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    static bool OriginalMatches(string path, InstallerFileChange file)
    {
        return ExistsOrdinary(path) &&
            FileIdentity(path) == file.OldIdentity && Hash(path) == file.OldHash &&
            (int)File.GetAttributes(path) == file.OldAttributes && FullSecurity(path) == file.OldSecurity;
    }
    void RestoreOriginal(int index)
    {
        var file = journal.Files[index]; string target = PayloadTarget(root, file.Relative), retained = Retained(index);
        ExistsOrdinary(retained);
        if (ExistsOrdinary(target))
        {
            if (file.Existed && OriginalMatches(target, file))
            {
                if (ExistsOrdinary(retained)) throw new IOException("Ambiguous retained original.");
                return;
            }
            if (FileIdentity(target) != file.NewIdentity || Hash(target) != file.NewHash) throw new IOException("Rollback preserved an externally changed file: " + file.Relative);
        }
        if (file.Existed && (!ExistsOrdinary(retained) || !OriginalMatches(retained, file))) throw new IOException("Original object missing or changed; retained intent requires review.");
        if (ExistsOrdinary(target)) { Hit("restore.unlink.before:" + index); File.Delete(target); Hit("restore.unlink.after:" + index); }
        if (file.Existed)
        {
            Hit("restore.move.before:" + index); File.Move(retained, target); Hit("restore.move.after:" + index);
            if (!OriginalMatches(target, file)) throw new IOException("Original object restoration failed.");
        }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct FileInformation { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation info);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int informationClass, ref uint flags, uint size);
    internal static string FileIdentity(string path)
    {
        using (var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x200000, IntPtr.Zero))
        {
            FileInformation info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw new IOException("Cannot identify installation file.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0) throw new IOException("Expected an ordinary installation file.");
            return info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
        }
    }
    static bool ValidIdentity(string value)
    {
        if (value == null || value.Length > 64) return false;
        string[] parts = value.Split(':'); uint parsed;
        return parts.Length == 3 && parts.All(p => UInt32.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed));
    }
    static void UnlinkOriginal(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0) { File.Delete(path); return; }
        // Preserve readonly metadata shared by any independent hardlink.
        using (var handle = CreateFile(path, 0x10000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            uint flags = 0x17;
            if (handle.IsInvalid || !SetFileInformationByHandle(handle, 21, ref flags, 4)) throw new IOException("Cannot unlink retained readonly object.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        }
        if (ExistsOrdinary(path)) throw new IOException("Retained original unlink not verified.");
    }
    internal static void MakeNewFileReadable(string path)
    {
        var access = new FileSecurity(); access.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Actor(), SystemSid }) access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        File.SetAccessControl(path, access);
    }
    internal static void RestoreAccess(string path, string sddl)
    {
        if (File.GetAccessControl(path, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == sddl) return;
        var access = new FileSecurity(); access.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access); File.SetAccessControl(path, access);
    }
    private void Cleanup()
    {
        ValidatePrivate(directory);
        if (!String.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase) || Directory.GetDirectories(directory).Length != 0) throw new IOException("Unexpected transaction layout.");
        if (journal.Schema == 4)
            for (int i = 0; i < journal.Files.Count; i++)
            {
                string retained = Retained(i);
                if (!ExistsOrdinary(retained)) continue;
                if (journal.Phase != "committed" || !OriginalMatches(retained, journal.Files[i])) throw new IOException("Retained original requires recovery.");
                Hit("cleanup.original.before:" + i); UnlinkOriginal(retained); Hit("cleanup.original.after:" + i);
            }
        foreach (string path in Directory.GetFiles(directory).OrderBy(p => Path.GetFileName(p) == "journal.json" ? 1 : 0))
        { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected transaction file."); bool intent = Path.GetFileName(path) == "journal.json"; if (intent) Hit("cleanup.intent.before"); File.Delete(path); if (intent) Hit("cleanup.intent.after"); }
        Hit("cleanup.directory.before"); Directory.Delete(directory, false);
    }
    internal static void RecoverPending(string root, IInstallerRegistryStore registryStore = null, IInstallerShortcutEnvironment shortcutEnvironment = null, Action<string> checkpoint = null)
    { using (AcquireRootLease(root)) RecoverCore(root, registryStore ?? new WindowsInstallerRegistryStore(), shortcutEnvironment ?? new WindowsInstallerShortcutEnvironment(), checkpoint); }
    internal static void RecoverUnderLease(string root, IInstallerRegistryStore registryStore, IInstallerShortcutEnvironment shortcutEnvironment)
    { RecoverCore(root, registryStore, shortcutEnvironment); }
    private static void RecoverCore(string root, IInstallerRegistryStore registryStore, IInstallerShortcutEnvironment shortcutEnvironment, Action<string> checkpoint = null)
    {
        root = InstallerFileSafety.ValidateRoot(root);
        foreach (string directory in Directory.GetDirectories(root, ".nova-update-*", SearchOption.TopDirectoryOnly))
        {
            Guid id; if (!Guid.TryParseExact(Path.GetFileName(directory).Substring(13), "N", out id)) throw new InvalidDataException("Unknown transaction directory.");
            ValidatePrivate(directory); string path = Path.Combine(directory, "journal.json");
            if (!File.Exists(path))
            {
                string marker = Path.Combine(directory, "preparing");
                string[] staged = Directory.GetFiles(directory);
                if (File.Exists(Path.Combine(directory, "active")) || staged.Length != 0 && (!File.Exists(marker) || File.ReadAllText(marker, Encoding.ASCII) != "NOVA update preparation v4")) throw new InvalidDataException("Update intent missing; original objects require recovery review.");
                new InstallerFileTransaction(root, directory, new InstallerFileJournal(), registryStore, shortcutEnvironment).Cleanup(); continue;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid transaction journal.");
            var journal = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 16 }.Deserialize<InstallerFileJournal>(File.ReadAllText(path, new UTF8Encoding(false, true)));
            if (journal == null || journal.Schema < 1 || journal.Schema > 4 || journal.Schema == 1 && journal.Registry != null || journal.Schema >= 2 && journal.Schema <= 3 && journal.Registry == null || journal.Schema < 3 && journal.Shortcuts != null || journal.Schema == 3 && journal.Shortcuts == null || journal.Schema == 4 && journal.Shortcuts != null && journal.Registry == null || !String.Equals(journal.Root, root, StringComparison.OrdinalIgnoreCase) || journal.Files == null || journal.Files.Count > 100000 || journal.Applied < 0 || journal.Applied > journal.Files.Count || journal.Attempting < -1 || journal.Attempting >= journal.Files.Count || !new[] { "applying", "committed", "rolled-back" }.Contains(journal.Phase)) throw new InvalidDataException("Invalid transaction state.");
            if (journal.Attempting != -1 && journal.Attempting != journal.Applied || journal.Phase == "committed" && (journal.Applied != journal.Files.Count || journal.Attempting != -1)) throw new InvalidDataException("Inconsistent transaction checkpoint.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in journal.Files)
                if (file == null || !paths.Add(PayloadTarget(root, file.Relative)) || !IsHash(file.NewHash) || file.Existed && (!IsHash(file.OldHash) || String.IsNullOrEmpty(file.OldAccess)) || journal.Schema == 4 && (!ValidIdentity(file.NewIdentity) || file.Existed && (!ValidIdentity(file.OldIdentity) || String.IsNullOrEmpty(file.OldSecurity) || ((FileAttributes)file.OldAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0))) throw new InvalidDataException("Invalid transaction file record.");
            if (journal.Registry != null)
            {
                InstallerRegistryTransaction.Validate(journal.Registry, root);
                bool registryStarted = journal.Registry.Applied > 0 || journal.Registry.Attempting != -1;
                if (journal.Phase != "rolled-back" && registryStarted && (journal.Applied != journal.Files.Count || journal.Attempting != -1) || journal.Phase == "committed" && (journal.Registry.Applied != journal.Registry.Values.Count || journal.Registry.Attempting != -1)) throw new InvalidDataException("Inconsistent file/registry checkpoint.");
            }
            if (journal.Shortcuts != null)
            {
                InstallerShortcutTransaction.Validate(journal.Shortcuts, shortcutEnvironment);
                bool shortcutsStarted = journal.Shortcuts.Applied > 0 || journal.Shortcuts.Attempting != -1;
                bool shortcutsDone = journal.Shortcuts.Applied == journal.Shortcuts.Items.Count && journal.Shortcuts.Attempting == -1;
                bool registryStarted = journal.Registry.Applied > 0 || journal.Registry.Attempting != -1;
                if (journal.Phase != "rolled-back" && (shortcutsStarted && (journal.Applied != journal.Files.Count || journal.Attempting != -1) || registryStarted && !shortcutsDone) || journal.Phase == "committed" && !shortcutsDone) throw new InvalidDataException("Inconsistent shortcut transaction checkpoint.");
            }
            var transaction = new InstallerFileTransaction(root, directory, journal, registryStore, shortcutEnvironment) { Checkpoint = checkpoint };
            try { if (journal.Phase == "committed" || journal.Phase == "rolled-back") transaction.Cleanup(); else transaction.Rollback(); }
            finally { transaction.ReleaseRegistryLease(); }
        }
    }
    public void Dispose()
    {
        try { if (complete) return; if (published) Rollback(); else { complete = true; Cleanup(); } }
        finally { ReleaseRegistryLease(); if (rootLease != null) { rootLease.Dispose(); rootLease = null; } }
    }
}

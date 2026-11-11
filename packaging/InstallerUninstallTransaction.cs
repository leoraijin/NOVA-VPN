using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using NovaVpn;

internal sealed class InstallerRemovalTask
{
    public string Name, Xml, Sddl, Sid;
    public StartupTaskSnapshot Snapshot() { return new StartupTaskSnapshot(Name, Xml, Sddl); }
}
internal sealed class InstallerRemovalStep
{
    public string Kind, Relative, Hash, Security, Name, Identity, ShortcutPath;
    public int Slot = -1, Attributes;
    public InstallerRegistryValue Value;
    public InstallerRemovalTask Task;
}
internal sealed class InstallerRemovalJournal
{
    public int Schema = 1, Applied, Attempting = -1;
    public string Root, Id, UserSid, Phase = "applying", DeferredSelf, DeferredHash, DeferredIdentity;
    public List<InstallerRemovalStep> Steps = new List<InstallerRemovalStep>();
}

// All mutation entry points require the installation and registration leases.
// No SCM, WFP, profile/AppData access, arbitrary registry addresses or task runs.
internal sealed class InstallerUninstallTransaction : IDisposable
{
    const int Limit = 8 * 1024 * 1024;
    const string Preparation = "NOVA removal preparation v1";
    readonly string root, directory;
    readonly IInstallerRegistryStore registry;
    readonly IInstallerShortcutEnvironment shortcuts;
    readonly IInstallerStartupTaskStore tasks;
    readonly Action<string> checkpoint, deferDelete;
    InstallerRemovalJournal journal; bool published, complete;
    static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), AdminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    static SecurityIdentifier Actor()
    {
        using (var id = WindowsIdentity.GetCurrent()) return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) ? AdminSid : id.User;
    }
    static FileSecurity FileSecurity()
    {
        var acl = new FileSecurity(); acl.SetOwner(Actor()); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Actor(), SystemSid }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow)); return acl;
    }
    static void PrivateDirectory(string path)
    {
        var acl = new DirectorySecurity(); acl.SetOwner(Actor()); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Actor(), SystemSid }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(path, acl); CheckPrivate(path);
    }
    static void CheckPrivate(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected removal journal.");
        var acl = Directory.GetAccessControl(path); var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!acl.AreAccessRulesProtected || !owner.Equals(Actor()) && !(Actor().Equals(AdminSid) && owner.Equals(SystemSid))) throw new UnauthorizedAccessException("Untrusted removal owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl || !rule.IdentityReference.Equals(Actor()) && !rule.IdentityReference.Equals(SystemSid)) throw new UnauthorizedAccessException("Untrusted removal permissions.");
    }
    static bool Exists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("Expected a file, found a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Access denied and every other IO error are unknown state, never absence.
    }
    static void Copy(string source, string target)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 65536, FileOptions.WriteThrough, FileSecurity())) { input.CopyTo(output); output.Flush(true); }
    }
    internal InstallerUninstallTransaction(string root, IInstallerRegistryStore registry, IInstallerShortcutEnvironment shortcuts, IInstallerStartupTaskStore tasks, Action<string> checkpoint = null, Action<string> deferDelete = null)
    {
        this.deferDelete = deferDelete; this.root = InstallerFileSafety.ValidateRoot(root); this.registry = registry; this.shortcuts = shortcuts; this.tasks = tasks; this.checkpoint = checkpoint;
        journal = new InstallerRemovalJournal { Root = this.root, Id = Guid.NewGuid().ToString("N"), UserSid = shortcuts.UserSid };
        if (HasPending(this.root)) throw new IOException("Recover the previous uninstall before preparing another one.");
        directory = Path.Combine(this.root, ".nova-remove-" + journal.Id); PrivateDirectory(directory);
        using (var marker = new FileStream(Path.Combine(directory, "preparing"), FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, FileSecurity()))
        { byte[] bytes = Encoding.ASCII.GetBytes(Preparation); marker.Write(bytes, 0, bytes.Length); marker.Flush(true); }
    }
    InstallerUninstallTransaction(string root, string directory, InstallerRemovalJournal journal, IInstallerRegistryStore registry, IInstallerShortcutEnvironment shortcuts, IInstallerStartupTaskStore tasks, Action<string> checkpoint, Action<string> deferDelete = null)
    { this.deferDelete = deferDelete; this.root = root; this.directory = directory; this.journal = journal; this.registry = registry; this.shortcuts = shortcuts; this.tasks = tasks; this.checkpoint = checkpoint; published = true; }
    string Backup(int index) { return Path.Combine(directory, "old-" + index.ToString("D6")); }
    string Retired(int index) { return Path.Combine(Path.GetDirectoryName(Target(journal.Steps[index])), ".nova-removed-" + journal.Id + "-" + index.ToString("D6") + ".old"); }
    static string Relative(string root, string full)
    { if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File outside installation."); return full.Substring(root.Length + 1); }
    static string FileTarget(string root, string relative)
    {
        return InstallerFileSafety.ResolvePayload(root, relative);
    }
    string Target(InstallerRemovalStep step)
    {
        if (step.Kind == "file") return FileTarget(root, step.Relative);
        string path = InstallerShortcutTransaction.ValidatePath(shortcuts.Resolve(step.Slot));
        if (!String.Equals(path, step.ShortcutPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Windows shortcut folders changed; original context is required for recovery.");
        return path;
    }
    static string Security(string path) { return File.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group); }
    void AddFile(string path, string relative, int slot)
    {
        if (Directory.Exists(path)) throw new IOException("Removal target is a directory."); if (!Exists(path)) return;
        int i = journal.Steps.Count;
        var step = new InstallerRemovalStep { Kind = relative != null ? "file" : "shortcut", Relative = relative, ShortcutPath = relative == null ? path : null, Slot = slot, Security = Security(path), Identity = FileIdentity(path), Attributes = (int)File.GetAttributes(path) };
        step.Hash = InstallerFileTransaction.Hash(path);
        // Retain the actual same-volume file object on removal. Byte-copy restore
        // alone would discard alternate streams, hard-link identity and NTFS state.
        if (relative != null && String.Equals(relative, "install-manifest.txt", StringComparison.OrdinalIgnoreCase)) Copy(path, Backup(i));
        if (!Matches(path, step, false)) throw new IOException("File changed while backing up."); journal.Steps.Add(step);
    }
    internal static string SecurityKey(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        descriptor.SetFlags(descriptor.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired));
        return descriptor.GetSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    }
    static bool Matches(string path, InstallerRemovalStep step, bool attempted)
    {
        if (!Exists(path) || Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var attributes = (int)File.GetAttributes(path);
        string security = SecurityKey(Security(path));
        return FileIdentity(path) == step.Identity && InstallerFileTransaction.Hash(path) == step.Hash && security == SecurityKey(step.Security) && attributes == step.Attributes;
    }
    internal void Prepare(string executingUninstaller = null, Func<string, string, int, bool> ownsShortcut = null)
    {
        if (published || journal.Steps.Count != 0) throw new InvalidOperationException("Removal already prepared.");
        var paths = InstallerFileSafety.ReadUninstallPaths(root); foreach (var path in paths) FileTarget(root, Relative(root, path));
        var snapshots = tasks.List();
        if (snapshots == null || snapshots.Length > 1024 || snapshots.Any(t => t == null) || snapshots.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) throw new InvalidDataException("Invalid task list.");
        foreach (var snapshot in snapshots.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var owned = InstallerStartupTasks.Identify(snapshot, Path.Combine(root, "NOVA VPN.exe"), tasks.ResolveSid); if (owned == null) continue;
            journal.Steps.Add(new InstallerRemovalStep { Kind = "task", Task = new InstallerRemovalTask { Name = snapshot.Name, Xml = snapshot.Xml, Sddl = snapshot.Sddl, Sid = owned.Sid } });
        }
        foreach (string path in paths)
        {
            if (executingUninstaller != null && String.Equals(path, Path.GetFullPath(executingUninstaller), StringComparison.OrdinalIgnoreCase))
            {
                if (!String.Equals(Relative(root, path), "NOVA VPN Uninstaller.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected executing uninstaller.");
                journal.DeferredSelf = "NOVA VPN Uninstaller.exe"; journal.DeferredHash = InstallerFileTransaction.Hash(path); journal.DeferredIdentity = FileIdentity(path); continue;
            }
            AddFile(path, Relative(root, path), -1);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot <= 3; slot++)
        {
            string path = InstallerShortcutTransaction.ValidatePath(shortcuts.Resolve(slot));
            if (!seen.Add(path) || !Exists(path)) continue;
            if ((ownsShortcut ?? Belongs)(path, root, slot)) AddFile(path, null, slot);
        }
        if (registry.KeyExists())
        {
            var location = registry.Read("InstallLocation");
            if (!location.Exists || location.Kind != (int)Microsoft.Win32.RegistryValueKind.String) throw new InvalidDataException("Cannot identify uninstall registration owner.");
            if (String.Equals(location.Text, root, StringComparison.OrdinalIgnoreCase))
                foreach (string name in InstallerRegistryTransaction.OwnedNames()) journal.Steps.Add(new InstallerRemovalStep { Kind = "registry", Name = name, Value = registry.Read(name) });
        }
        Validate(); // Complete plan and every backup must be valid before first mutation.
    }
    internal static bool Belongs(string path, string root, int slot)
    {
        object shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)); link = ((dynamic)shell).CreateShortcut(path);
            string target = (string)((dynamic)link).TargetPath, work = (string)((dynamic)link).WorkingDirectory, args = (string)((dynamic)link).Arguments;
            return String.Equals(target, Path.Combine(root, slot == 1 ? "NOVA VPN Uninstaller.exe" : "NOVA VPN.exe"), StringComparison.OrdinalIgnoreCase) && String.Equals(work, root, StringComparison.OrdinalIgnoreCase) && args == (slot == 1 ? "--uninstall" : "");
        }
        finally { if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    void Hit(string value) { if (checkpoint != null) checkpoint(value); }
    void Save()
    {
        CheckPrivate(directory); Hit("save.before");
        byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = Limit }.Serialize(journal)); if (bytes.Length > Limit) throw new InvalidDataException("Removal journal too large.");
        string next = Path.Combine(directory, ".journal.next"), path = Path.Combine(directory, "journal.json");
        if (Exists(next)) { if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected removal stage."); File.Delete(next); }
        using (var stream = new FileStream(next, FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, FileSecurity())) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        if (Exists(path)) File.Replace(next, path, null); else File.Move(next, path); Hit("save.after");
    }
    void Validate()
    {
        Guid id;
        if (journal == null || journal.Schema != 1 || !String.Equals(journal.Root, root, StringComparison.OrdinalIgnoreCase) || journal.UserSid != shortcuts.UserSid || !Guid.TryParseExact(journal.Id, "N", out id) || id == Guid.Empty || Path.GetFileName(directory) != ".nova-remove-" + journal.Id || journal.Steps == null || journal.Steps.Count > 102000 || journal.Applied < 0 || journal.Applied > journal.Steps.Count || journal.Attempting < -1 || journal.Attempting >= journal.Steps.Count || journal.Attempting != -1 && journal.Attempting != journal.Applied || !new[] { "applying", "committed", "rolled-back" }.Contains(journal.Phase) || (journal.DeferredSelf == null ? journal.DeferredHash != null || journal.DeferredIdentity != null : journal.DeferredSelf != "NOVA VPN Uninstaller.exe" || !InstallerFileTransaction.IsHash(journal.DeferredHash) || !ValidIdentity(journal.DeferredIdentity))) throw new InvalidDataException("Invalid removal journal.");
        if (journal.Phase == "committed" && (journal.Applied != journal.Steps.Count || journal.Attempting != -1)) throw new InvalidDataException("Incomplete removal commit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int manifest = -1, previousKind = 0;
        for (int i = 0; i < journal.Steps.Count; i++)
        {
            var step = journal.Steps[i]; if (step == null) throw new InvalidDataException("Missing removal step.");
            int kind = step.Kind == "task" ? 0 : step.Kind == "file" ? 1 : step.Kind == "shortcut" ? 2 : step.Kind == "registry" ? 3 : -1;
            if (kind < previousKind) throw new InvalidDataException("Invalid removal resource ordering."); previousKind = kind;
            if (step.Kind == "task")
            {
                if (step.Task == null || !names.Add("task:" + step.Task.Name)) throw new InvalidDataException("Invalid removal task.");
                var owned = InstallerStartupTasks.Identify(step.Task.Snapshot(), Path.Combine(root, "NOVA VPN.exe"), tasks.ResolveSid);
                if (owned == null || owned.Sid != step.Task.Sid) throw new UnauthorizedAccessException("Untrusted task in removal journal.");
            }
            else if (step.Kind == "registry") { InstallerRegistryTransaction.DemandName(step.Name); if (step.Value == null || !names.Add("registry:" + step.Name)) throw new InvalidDataException("Invalid registry removal step."); step.Value.Validate(); }
            else if (step.Kind == "file" || step.Kind == "shortcut")
            {
                if (step.Kind == "file" ? step.Slot != -1 || step.ShortcutPath != null : step.Slot < 0 || step.Slot > 3 || step.Relative != null) throw new InvalidDataException("Invalid removal target slot.");
                string target = Target(step); if (!names.Add("file:" + target) || !InstallerFileTransaction.IsHash(step.Hash) || !ValidIdentity(step.Identity) || String.IsNullOrEmpty(step.Security) || ((FileAttributes)step.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0) throw new InvalidDataException("Invalid removal file.");
                var acl = new FileSecurity(); acl.SetSecurityDescriptorSddlForm(step.Security, AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
                if (step.Kind == "file" && String.Equals(step.Relative, "install-manifest.txt", StringComparison.OrdinalIgnoreCase)) manifest = i;
                if (journal.Phase == "applying" && Exists(Retired(i)) && !Matches(Retired(i), step, false)) throw new InvalidDataException("Retained original object changed.");
            }
            else throw new InvalidDataException("Unknown removal resource.");
        }
        if (manifest < 0) throw new InvalidDataException("No manifest snapshot for removal.");
        if (journal.Phase == "applying")
        {
            if (!Exists(Backup(manifest)) || (File.GetAttributes(Backup(manifest)) & FileAttributes.ReparsePoint) != 0 || InstallerFileTransaction.Hash(Backup(manifest)) != journal.Steps[manifest].Hash) throw new InvalidDataException("Original manifest snapshot is damaged.");
            var allowed = new HashSet<string>(InstallerFileSafety.ParseUninstallPaths(root, File.ReadAllLines(Backup(manifest), new UTF8Encoding(false, true))), StringComparer.OrdinalIgnoreCase);
            foreach (string path in allowed) FileTarget(root, Relative(root, path));
            foreach (var step in journal.Steps.Where(s => s.Kind == "file")) if (!allowed.Contains(Target(step))) throw new InvalidDataException("Removal target is outside original manifest.");
            var registration = journal.Steps.Where(s => s.Kind == "registry").ToArray();
            if (registration.Length != 0 && (registration.Length != InstallerRegistryTransaction.OwnedNames().Length || !registration.Any(s => s.Name == "InstallLocation" && s.Value.Exists && s.Value.Kind == (int)Microsoft.Win32.RegistryValueKind.String && String.Equals(s.Value.Text, root, StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Registration snapshot is not installation bound.");
        }
    }
    internal void Apply()
    {
        if (published || complete) throw new InvalidOperationException("Removal already applied."); Validate();
        // Set before Save: a lost publication acknowledgement must not cause cleanup
        // of a possibly published journal in Dispose.
        published = true; Save();
        Unlink(Path.Combine(directory, "preparing")); // Removed before any resource may change.
        for (int i = 0; i < journal.Steps.Count; i++)
        {
            var step = journal.Steps[i]; VerifyBefore(step); journal.Attempting = i; Save(); Hit("mutation.before:" + i); VerifyBefore(step);
            if (step.Kind == "task") { tasks.Delete(step.Task.Snapshot()); if (tasks.Read(step.Task.Name) != null) throw new IOException("Task removal not verified."); }
            else if (step.Kind == "registry") { registry.Write(step.Name, InstallerRegistryValue.Missing()); if (registry.Read(step.Name).Exists) throw new IOException("Registry removal not verified."); }
            else { string target = Target(step); File.Move(target, Retired(i)); if (Exists(target) || Directory.Exists(target) || !Matches(Retired(i), step, false)) throw new IOException("File quarantine not verified."); Notify(step); }
            Hit("mutation.after:" + i); journal.Applied = i + 1; journal.Attempting = -1; Save();
        }
    }
    void VerifyBefore(InstallerRemovalStep step)
    {
        if (step.Kind == "task") InstallerStartupTasks.Same(tasks.Read(step.Task.Name), step.Task.Snapshot());
        else if (step.Kind == "registry") { if (!InstallerRegistryValue.Same(registry.Read(step.Name), step.Value)) throw new IOException("Registration changed during uninstall."); }
        else if (!Matches(Target(step), step, false)) throw new IOException("File changed during uninstall.");
    }
    void Notify(InstallerRemovalStep step) { if (step.Kind == "shortcut") try { shortcuts.Notify(Target(step)); } catch { } }
    internal void Complete()
    {
        if (!published || journal.Applied != journal.Steps.Count || journal.Attempting != -1) throw new InvalidOperationException("Incomplete uninstall.");
        journal.Phase = "committed";
        try { Save(); }
        catch
        {
            var observed = ReadJournal(directory);
            if (observed == null || observed.Id != journal.Id || observed.Phase != "committed") throw;
            journal = observed; Validate();
        }
        complete = true;
        try { Cleanup(); if (journal.Steps.Any(s => s.Kind == "registry")) registry.DeleteKeyIfEmpty(); }
        catch (IOException) { } catch (UnauthorizedAccessException) { } // durable commit is retained for cleanup retry

    }
    void Rollback()
    {
        Validate();
        for (int i = Math.Max(journal.Applied - 1, journal.Attempting); i >= 0; i--)
        {
            var step = journal.Steps[i]; Hit("restore.before:" + i);
            if (step.Kind == "task")
            {
                var current = tasks.Read(step.Task.Name); var original = step.Task.Snapshot();
                if (current != null) { if (!tasks.Equivalent(current, original, step.Task.Sid)) throw new IOException("Rollback preserves externally edited task."); }
                else { tasks.Restore(original, step.Task.Sid); if (!tasks.Equivalent(tasks.Read(original.Name), original, step.Task.Sid)) throw new IOException("Restored task not verified."); }
            }
            else if (step.Kind == "registry")
            {
                if (!registry.KeyExists()) throw new IOException("Rollback preserves independently removed registration key.");
                var current = registry.Read(step.Name);
                if (!InstallerRegistryValue.Same(current, step.Value)) { if (current.Exists) throw new IOException("Rollback preserves external registry edit."); registry.Write(step.Name, step.Value); if (!InstallerRegistryValue.Same(registry.Read(step.Name), step.Value)) throw new IOException("Registry restore not verified."); }
            }
            else
            {
                string target = Target(step);
                if (Directory.Exists(target) || Exists(target) && !Matches(target, step, true)) throw new IOException("Rollback preserves externally changed file.");
                if (!Exists(target))
                {
                    if (!Exists(Retired(i)) || !Matches(Retired(i), step, false)) throw new IOException("Original file object is missing or changed; journal retained.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(Retired(i), target);
                }
                if (!Matches(target, step, false)) throw new IOException("File restore not verified."); Notify(step);
            }
            Hit("restore.after:" + i); journal.Applied = i; journal.Attempting = -1; Save();
        }
        journal.Phase = "rolled-back"; Save(); complete = true; Cleanup();
    }
    void Cleanup()
    {
        CheckPrivate(directory);
        if (journal != null && journal.Phase == "committed" && journal.DeferredSelf != null) FinishSelf();
        if (Directory.GetDirectories(directory).Length != 0) throw new IOException("Unexpected removal directory layout.");
        if (journal != null)
        {
            for (int i = 0; i < journal.Steps.Count; i++)
            {
                var step = journal.Steps[i]; if (step.Kind != "file" && step.Kind != "shortcut") continue;
                string retained = Retired(i); if (!Exists(retained)) continue;
                if (journal.Phase != "committed" || !Matches(retained, step, false)) throw new IOException("Retained original object still needs recovery.");
                Unlink(retained);
            }
        }
        foreach (string path in Directory.GetFiles(directory).Where(p => !String.Equals(Path.GetFileName(p), "journal.json", StringComparison.OrdinalIgnoreCase))) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected backup."); Unlink(path); }
        string intent = Path.Combine(directory, "journal.json"); if (Exists(intent)) { if ((File.GetAttributes(intent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected intent."); Unlink(intent); }
        Directory.Delete(directory, false);
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, ref uint flags, uint size);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Information { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out Information info);
    static string FileIdentity(string path)
    {
        using (var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x200000, IntPtr.Zero))
        {
            Information info; if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw new IOException("Cannot identify original file object.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            return info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
        }
    }
    static bool ValidIdentity(string value)
    {
        if (value == null || value.Length > 64) return false; string[] parts = value.Split(':'); uint parsed;
        return parts.Length == 3 && parts.All(p => UInt32.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed));
    }
    internal static void Unlink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0) { File.Delete(path); return; }
        // Do not clear the readonly attribute: another hard link may share it.
        // DELETE + POSIX + FORCE_IMAGE_SECTION_CHECK + IGNORE_READONLY_ATTRIBUTE.
        using (var handle = CreateFile(path, 0x10000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (handle.IsInvalid) throw new IOException("Cannot open retained object for deletion.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            uint flags = 0x17;
            if (!SetFileInformationByHandle(handle, 21, ref flags, 4)) throw new IOException("Cannot safely unlink readonly object.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        }
        if (Exists(path)) throw new IOException("Retained object removal not verified.");
    }
    void FinishSelf()
    {
        string original = InstallerFileSafety.Resolve(root, journal.DeferredSelf);
        string retired = InstallerFileSafety.Resolve(root, ".nova-removed-" + journal.Id + ".exe");
        if (!Exists(retired))
        {
            if (!Exists(original)) return; // Earlier committed cleanup already removed it.
            if (FileIdentity(original) != journal.DeferredIdentity || InstallerFileTransaction.Hash(original) != journal.DeferredHash) throw new IOException("A newer uninstaller is preserved; retired-file cleanup needs review.");
            Hit("self.before-move"); File.Move(original, retired); Hit("self.after-move");
        }
        if ((File.GetAttributes(retired) & FileAttributes.ReparsePoint) != 0 || FileIdentity(retired) != journal.DeferredIdentity || InstallerFileTransaction.Hash(retired) != journal.DeferredHash) throw new IOException("Retired uninstaller was replaced.");
        try { Unlink(retired); }
        catch (IOException) { if (deferDelete == null) throw; deferDelete(retired); }
        catch (UnauthorizedAccessException) { if (deferDelete == null) throw; deferDelete(retired); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string destination, int flags);
    internal static void QueueRetiredFile(string path)
    {
        // Only FinishSelf may supply this exact, hashed, uniquely retired file.
        if (!MoveFileEx(path, null, 4)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }
    internal static bool HasPending(string root)
    { try { return Directory.GetDirectories(InstallerFileSafety.ValidateRoot(root), ".nova-remove-*", SearchOption.TopDirectoryOnly).Length != 0; } catch (DirectoryNotFoundException) { return false; } }
    internal static bool RecoverUnderLease(string root, IInstallerRegistryStore registry, IInstallerShortcutEnvironment shortcuts, IInstallerStartupTaskStore tasks, Action<string> checkpoint = null, Action<string> deferDelete = null)
    {
        root = InstallerFileSafety.ValidateRoot(root); bool committed = false;
        foreach (string directory in Directory.GetDirectories(root, ".nova-remove-*", SearchOption.TopDirectoryOnly))
        {
            Guid id; if (!Guid.TryParseExact(Path.GetFileName(directory).Substring(13), "N", out id)) throw new InvalidDataException("Unknown uninstall directory.");
            CheckPrivate(directory); string path = Path.Combine(directory, "journal.json");
            if (!Exists(path)) { DemandUnpublished(directory); new InstallerUninstallTransaction(root, directory, null, registry, shortcuts, tasks, checkpoint, deferDelete).Cleanup(); continue; }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > Limit) throw new InvalidDataException("Invalid uninstall journal.");
            var saved = new JavaScriptSerializer { MaxJsonLength = Limit, RecursionLimit = 20 }.Deserialize<InstallerRemovalJournal>(File.ReadAllText(path, new UTF8Encoding(false, true)));
            var transaction = new InstallerUninstallTransaction(root, directory, saved, registry, shortcuts, tasks, checkpoint, deferDelete); transaction.Validate();
            if (saved.Phase == "committed" || saved.Phase == "rolled-back") { if (saved.Phase == "committed") committed = true; transaction.Cleanup(); } else transaction.Rollback();
        }
        return committed;
    }
    internal static bool RecoverInstalledUnderLease(string root)
    {
        if (!HasPending(root)) return false;
        if (Directory.GetDirectories(root, ".nova-update-*").Length != 0) throw new IOException("Conflicting update and uninstall recovery journals require review.");
        var registry = new WindowsInstallerRegistryStore();
        using (registry.AcquireLease())
        using (var tasks = new WindowsInstallerStartupTaskStore(Path.Combine(root, "NOVA VPN.exe")))
            return RecoverUnderLease(root, registry, new WindowsInstallerShortcutEnvironment(), tasks, null, QueueRetiredFile);
    }
    static void DemandUnpublished(string directory)
    {
        CheckPrivate(directory);
        if (Directory.GetDirectories(directory).Length != 0) throw new IOException("Unexpected unpublished layout.");
        string[] files = Directory.GetFiles(directory); if (files.Length == 0) return; // construction/terminal cleanup boundary
        string marker = Path.Combine(directory, "preparing");
        if (Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0 && new FileInfo(marker).Length <= Preparation.Length)
        {
            string text = File.ReadAllText(marker, Encoding.ASCII);
            if (text == Preparation || files.Length == 1 && Preparation.StartsWith(text, StringComparison.Ordinal)) return;
        }
        throw new InvalidDataException("Uninstall intent disappeared after preparation; retained originals require recovery review.");
    }
    static InstallerRemovalJournal ReadJournal(string directory)
    {
        CheckPrivate(directory); string path = Path.Combine(directory, "journal.json");
        if (!Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > Limit) throw new InvalidDataException("Invalid uninstall journal.");
        return new JavaScriptSerializer { MaxJsonLength = Limit, RecursionLimit = 20 }.Deserialize<InstallerRemovalJournal>(File.ReadAllText(path, new UTF8Encoding(false, true)));
    }
    internal void Execute(string executingUninstaller = null, Func<string, string, int, bool> ownsShortcut = null)
    {
        try { Prepare(executingUninstaller, ownsShortcut); Apply(); Complete(); }
        catch (Exception primary)
        {
            try { Dispose(); } catch (Exception rollback) { throw new AggregateException("Uninstall failed and recovery is incomplete; journal and backups retained.", primary, rollback); }
            throw;
        }
    }
    public void Dispose()
    {
        if (complete) return;
        if (!published) { Cleanup(); complete = true; return; }
        // Once commit publication may have succeeded, reread it before deciding.
        string path = Path.Combine(directory, "journal.json");
        if (!Exists(path))
        {
            if (journal.Applied != 0 || journal.Attempting != -1 || journal.Phase != "applying") throw new InvalidDataException("Uninstall intent missing after attempted changes; originals preserved.");
            DemandUnpublished(directory); complete = true; Cleanup(); return;
        }
        var saved = ReadJournal(directory);
        journal = saved; Validate();
        if (saved.Phase == "committed" || saved.Phase == "rolled-back") { complete = true; Cleanup(); return; }
        Rollback();
    }
}

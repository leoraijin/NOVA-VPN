using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;

// Only the ten values actually written by the installer can be restored.
// The journal cannot choose a hive, key, registry view or arbitrary value name.
internal sealed class InstallerRegistryValue
{
    public bool Exists;
    public int Kind;
    public string Text;
    public long Number;
    public string[] Strings;

    internal static InstallerRegistryValue Missing() { return new InstallerRegistryValue(); }
    internal static InstallerRegistryValue String(string value) { return new InstallerRegistryValue { Exists = true, Kind = (int)RegistryValueKind.String, Text = value }; }
    internal static InstallerRegistryValue DWord(int value) { return new InstallerRegistryValue { Exists = true, Kind = (int)RegistryValueKind.DWord, Number = value }; }
    internal static InstallerRegistryValue Capture(object value, RegistryValueKind kind)
    {
        var result = new InstallerRegistryValue { Exists = true, Kind = (int)kind };
        switch (kind)
        {
            case RegistryValueKind.String: case RegistryValueKind.ExpandString: result.Text = (string)value; break;
            case RegistryValueKind.DWord: result.Number = (int)value; break;
            case RegistryValueKind.QWord: result.Number = (long)value; break;
            case RegistryValueKind.MultiString: result.Strings = (string[])value; break;
            case RegistryValueKind.Binary: case RegistryValueKind.None: result.Text = Convert.ToBase64String((byte[])value); break;
            default: throw new InvalidDataException("Unsupported previous registry value type.");
        }
        result.Validate(); return result;
    }
    internal void Validate()
    {
        if (!Exists)
        {
            if (Kind != 0 || Text != null || Number != 0 || Strings != null) throw new InvalidDataException("Invalid absent registry value.");
            return;
        }
        if (Text != null && Text.Length > 1024 * 1024 || Strings != null && (Strings.Length > 16384 || Strings.Any(s => s == null || s.Length > 65536) || Strings.Sum(s => (long)s.Length) > 1024 * 1024)) throw new InvalidDataException("Registry snapshot too large.");
        switch ((RegistryValueKind)Kind)
        {
            case RegistryValueKind.String: case RegistryValueKind.ExpandString:
                if (Text == null || Strings != null || Number != 0) throw new InvalidDataException("Invalid string registry snapshot."); break;
            case RegistryValueKind.Binary: case RegistryValueKind.None:
                if (Text == null || Strings != null || Number != 0) throw new InvalidDataException("Invalid binary registry snapshot.");
                try { Convert.FromBase64String(Text); } catch (FormatException) { throw new InvalidDataException("Invalid binary registry snapshot."); } break;
            case RegistryValueKind.DWord: case RegistryValueKind.QWord:
                if (Text != null || Strings != null || Kind == (int)RegistryValueKind.DWord && (Number < Int32.MinValue || Number > Int32.MaxValue)) throw new InvalidDataException("Invalid integer registry snapshot."); break;
            case RegistryValueKind.MultiString:
                if (Strings == null || Text != null || Number != 0) throw new InvalidDataException("Invalid multi-string registry snapshot."); break;
            default: throw new InvalidDataException("Invalid registry snapshot type.");
        }
    }
    internal object Decode()
    {
        Validate();
        switch ((RegistryValueKind)Kind)
        {
            case RegistryValueKind.String: case RegistryValueKind.ExpandString: return Text;
            case RegistryValueKind.DWord: return (int)Number;
            case RegistryValueKind.QWord: return Number;
            case RegistryValueKind.MultiString: return Strings;
            case RegistryValueKind.Binary: case RegistryValueKind.None: return Convert.FromBase64String(Text);
            default: throw new InvalidDataException("Cannot decode absent registry value.");
        }
    }
    internal static bool Same(InstallerRegistryValue left, InstallerRegistryValue right)
    {
        return left != null && right != null && left.Exists == right.Exists && left.Kind == right.Kind && left.Text == right.Text && left.Number == right.Number &&
            (left.Strings == null ? right.Strings == null : right.Strings != null && left.Strings.SequenceEqual(right.Strings, StringComparer.Ordinal));
    }
}
internal sealed class InstallerRegistryChange
{
    public string Name;
    public InstallerRegistryValue Before, After;
}
internal sealed class InstallerRegistryJournal
{
    public bool KeyExisted;
    public int Applied, Attempting = -1;
    public List<InstallerRegistryChange> Values = new List<InstallerRegistryChange>();
}
internal interface IInstallerRegistryStore
{
    bool KeyExists();
    InstallerRegistryValue Read(string name);
    void Write(string name, InstallerRegistryValue value);
    void DeleteKeyIfEmpty();
}
internal sealed class WindowsInstallerRegistryStore : IInstallerRegistryStore
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NOVA VPN";
    private readonly RegistryHive hive;
    private readonly RegistryView view;
    private readonly string keyPath;
    internal WindowsInstallerRegistryStore() : this(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32, KeyPath) { }
    // Native fixture tests invoke this private constructor with an owned HKCU key.
    // Neither the installer UI nor a recovery journal can supply these addresses.
    private WindowsInstallerRegistryStore(RegistryHive hive, RegistryView view, string keyPath)
    { this.hive = hive; this.view = view; this.keyPath = keyPath; }
    internal IDisposable AcquireLease(int timeoutMilliseconds = 20000)
    {
        bool production = hive == RegistryHive.LocalMachine && keyPath == KeyPath;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier actor;
        using (var identity = WindowsIdentity.GetCurrent()) actor = production ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) : identity.User;
        string name = @"Global\NOVA.VPN.Installer.Registry.v1";
        if (!production)
        {
            using (var sha = SHA256.Create()) name = @"Local\NOVA.VPN.Installer.Registry.Fixture." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(hive + ":" + view + ":" + keyPath))).Replace("-", "");
        }
        var security = new MutexSecurity(); security.SetOwner(actor); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { actor, system }) security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
        bool created; Mutex mutex = new Mutex(false, name, out created, security);
        try
        {
            var actual = mutex.GetAccessControl(); var owner = actual.GetOwner(typeof(SecurityIdentifier));
            if (!actual.AreAccessRulesProtected || !owner.Equals(actor) && !owner.Equals(system)) throw new UnauthorizedAccessException("Untrusted installation metadata lock owner.");
            foreach (MutexAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType != AccessControlType.Allow || !rule.IdentityReference.Equals(actor) && !rule.IdentityReference.Equals(system)) throw new UnauthorizedAccessException("Untrusted installation metadata lock access.");
            bool acquired;
            try { acquired = mutex.WaitOne(timeoutMilliseconds); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another NOVA installation is modifying Windows registration.");
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class Lease : IDisposable
    {
        private Mutex mutex;
        internal Lease(Mutex mutex) { this.mutex = mutex; }
        public void Dispose() { if (mutex != null) { try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); mutex = null; } } }
    }
    public bool KeyExists() { using (var root = RegistryKey.OpenBaseKey(hive, view)) using (var key = root.OpenSubKey(keyPath)) return key != null; }
    public InstallerRegistryValue Read(string name)
    {
        InstallerRegistryTransaction.DemandName(name);
        using (var root = RegistryKey.OpenBaseKey(hive, view))
        using (var key = root.OpenSubKey(keyPath))
        {
            if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return InstallerRegistryValue.Missing();
            return InstallerRegistryValue.Capture(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(name));
        }
    }
    public void Write(string name, InstallerRegistryValue value)
    {
        InstallerRegistryTransaction.DemandName(name); value.Validate();
        using (var root = RegistryKey.OpenBaseKey(hive, view))
        {
            if (value.Exists)
            {
                using (var key = root.CreateSubKey(keyPath)) { key.SetValue(name, value.Decode(), (RegistryValueKind)value.Kind); key.Flush(); }
            }
            else using (var key = root.OpenSubKey(keyPath, true)) { if (key != null) { key.DeleteValue(name, false); key.Flush(); } }
        }
    }
    public void DeleteKeyIfEmpty()
    {
        using (var root = RegistryKey.OpenBaseKey(hive, view))
        {
            using (var key = root.OpenSubKey(keyPath))
                if (key == null || key.ValueCount != 0 || key.SubKeyCount != 0) return;
            // Never recursively delete a key: unrelated values/subkeys must survive.
            root.DeleteSubKey(keyPath, false); root.Flush();
        }
    }
}
internal static class InstallerRegistryTransaction
{
    private static readonly string[] Names = { "DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "DisplayIcon", "UninstallString", "NoModify", "NoRepair", "DesktopShortcut", "EstimatedSize" };
    internal static string[] OwnedNames() { return (string[])Names.Clone(); }
    internal static void DemandName(string name)
    { if (!Names.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Unexpected installer registry value."); }
    internal static InstallerRegistryJournal Prepare(IInstallerRegistryStore store, string root, string version, bool desktopShortcut, long bytes)
    {
        root = InstallerFileSafety.ValidateRoot(root);
        if (String.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(Char.IsControl) || bytes < 0) throw new ArgumentException("Invalid installation metadata.");
        var desired = new[] {
            InstallerRegistryValue.String("NOVA VPN"), InstallerRegistryValue.String(version), InstallerRegistryValue.String("NOVA Project"),
            InstallerRegistryValue.String(root), InstallerRegistryValue.String(Path.Combine(root, "NOVA VPN.exe")),
            InstallerRegistryValue.String("\"" + Path.Combine(root, "NOVA VPN Uninstaller.exe") + "\" --uninstall"),
            InstallerRegistryValue.DWord(1), InstallerRegistryValue.DWord(1), InstallerRegistryValue.DWord(desktopShortcut ? 1 : 0),
            InstallerRegistryValue.DWord((int)Math.Min(Int32.MaxValue, bytes / 1024)) };
        var journal = new InstallerRegistryJournal { KeyExisted = store.KeyExists() };
        for (int i = 0; i < Names.Length; i++) journal.Values.Add(new InstallerRegistryChange { Name = Names[i], Before = store.Read(Names[i]), After = desired[i] });
        Validate(journal, root); return journal;
    }
    internal static void Validate(InstallerRegistryJournal journal, string root)
    {
        if (journal == null || journal.Values == null || journal.Values.Count != Names.Length || journal.Applied < 0 || journal.Applied > Names.Length || journal.Attempting < -1 || journal.Attempting >= Names.Length || journal.Attempting != -1 && journal.Attempting != journal.Applied) throw new InvalidDataException("Invalid registry transaction checkpoint.");
        for (int i = 0; i < Names.Length; i++)
        {
            var value = journal.Values[i];
            if (value == null || value.Name != Names[i] || value.Before == null || value.After == null) throw new InvalidDataException("Invalid registry transaction record.");
            value.Before.Validate(); value.After.Validate();
            if (!journal.KeyExisted && value.Before.Exists || !value.After.Exists || value.After.Kind != (int)(i < 6 ? RegistryValueKind.String : RegistryValueKind.DWord)) throw new InvalidDataException("Invalid registry transaction value.");
        }
        var values = journal.Values;
        if (values[0].After.Text != "NOVA VPN" || String.IsNullOrWhiteSpace(values[1].After.Text) || values[1].After.Text.Length > 128 || values[1].After.Text.Any(Char.IsControl) || values[2].After.Text != "NOVA Project" || values[3].After.Text != root ||
            values[4].After.Text != Path.Combine(root, "NOVA VPN.exe") || values[5].After.Text != "\"" + Path.Combine(root, "NOVA VPN Uninstaller.exe") + "\" --uninstall" ||
            values[6].After.Number != 1 || values[7].After.Number != 1 || values[8].After.Number < 0 || values[8].After.Number > 1 || values[9].After.Number < 0) throw new InvalidDataException("Registry metadata does not match this installation.");
    }
    internal static void Apply(InstallerRegistryJournal journal, IInstallerRegistryStore store, Action save)
    {
        if (journal.Applied != 0 || journal.Attempting != -1) throw new InvalidOperationException("Registry transaction already attempted.");
        for (int i = 0; i < journal.Values.Count; i++)
        {
            var value = journal.Values[i];
            if (!InstallerRegistryValue.Same(store.Read(value.Name), value.Before)) throw new IOException("Installation metadata changed during preparation: " + value.Name);
            journal.Attempting = i; save();
            store.Write(value.Name, value.After);
            if (!InstallerRegistryValue.Same(store.Read(value.Name), value.After)) throw new IOException("Registry write verification failed: " + value.Name);
            journal.Applied = i + 1; journal.Attempting = -1; save();
        }
    }
    internal static void Rollback(InstallerRegistryJournal journal, IInstallerRegistryStore store, Action save)
    {
        for (int i = Math.Max(journal.Applied - 1, journal.Attempting); i >= 0; i--)
        {
            var value = journal.Values[i]; var current = store.Read(value.Name);
            if (!InstallerRegistryValue.Same(current, value.Before))
            {
                if (!InstallerRegistryValue.Same(current, value.After)) throw new IOException("Registry rollback preserved an external edit: " + value.Name);
                store.Write(value.Name, value.Before);
                if (!InstallerRegistryValue.Same(store.Read(value.Name), value.Before)) throw new IOException("Registry rollback verification failed.");
            }
            journal.Applied = i; journal.Attempting = -1; save();
        }
        if (!journal.KeyExisted) store.DeleteKeyIfEmpty();
    }
}

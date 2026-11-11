using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NovaVpn;

internal sealed class GuardServiceConfiguration
{
    internal uint Type, Start, Error;
    internal string Binary, Account, Group;
    internal string[] Dependencies;
    internal bool SafeSecurity, RecoverableSecurity;
    internal void Validate(bool allowPrepared = false)
    {
        if (Type != 16 || Start != 2 && !(allowPrepared && Start == 4) || Error != 1 ||
            !string.Equals(Binary, "\"" + TrafficGuardServiceIdentity.ServiceExe + "\"", StringComparison.OrdinalIgnoreCase) || !string.Equals(Account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(Group) ||
            Dependencies == null || Dependencies.Length != 1 || !string.Equals(Dependencies[0], "BFE", StringComparison.OrdinalIgnoreCase) || !(SafeSecurity || allowPrepared && Start == 4 && RecoverableSecurity))
            throw new InvalidDataException("Existing service is not the protected NOVA guard configuration.");
    }
}

internal interface IGuardRegistrationPlatform
{
    void ValidatePayload();
    GuardServiceConfiguration ReadService();
    GuardOwnerPolicy ReadOwner();
    IDisposable AcquireJournalLease();
    void RequireDisabledState();
    void WriteNewOwner(GuardOwnerPolicy owner);
    bool HasCreationReceipt(GuardOwnerPolicy owner);
    void PrepareCreationReceipt(GuardOwnerPolicy owner);
    void CompleteCreationReceipt(GuardOwnerPolicy owner);
    void RegisterOrComplete();
}

internal static class GuardRegistrationCoordinator
{
    internal static GuardOwnerPolicy Register(IGuardRegistrationPlatform platform, string authenticatedCallerSid)
    {
        platform.ValidatePayload();
        var existing = platform.ReadService();
        if (existing != null)
        {
            existing.Validate(true);
            if (existing.Start == 2) return platform.ReadOwner() ?? throw new InvalidDataException("Registered guard has no valid installed owner policy.");
        }
        using (platform.AcquireJournalLease())
        {
            // Another installer may have completed or prepared SCM registration
            // while this invocation waited for the machine journal lease. Never
            // mint an owner/receipt using the stale pre-lease absence snapshot.
            existing = platform.ReadService();
            if (existing != null)
            {
                existing.Validate(true);
                if (existing.Start == 2) return platform.ReadOwner() ?? throw new InvalidDataException("Registered guard has no valid installed owner policy.");
            }
            platform.RequireDisabledState();
            var owner = platform.ReadOwner();
            if (existing != null && (owner == null || !platform.HasCreationReceipt(owner))) throw new InvalidDataException("Prepared service has no matching durable creation receipt.");
            if (owner == null)
            {
                var sid = new SecurityIdentifier(authenticatedCallerSid);
                if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) throw new InvalidOperationException("SYSTEM installation requires an already authenticated installed-owner policy.");
                owner = new GuardOwnerPolicy(sid.Value, Guid.NewGuid());
                platform.WriteNewOwner(owner);
            }
            platform.PrepareCreationReceipt(owner);
            platform.RegisterOrComplete();
            var actual = platform.ReadService() ?? throw new InvalidDataException("SCM did not return the registered guard."); actual.Validate();
            var saved = platform.ReadOwner();
            if (saved == null || saved.InstallationId != owner.InstallationId || saved.OwnerSid != owner.OwnerSid) throw new InvalidDataException("Installed owner policy changed during registration.");
            platform.CompleteCreationReceipt(owner);
            return owner;
        }
    }
}

// No StartService/DeleteService or firewall writer here. Creation starts DISABLED,
// receives a protected service ACL, is read back, then becomes AUTO_START. Existing
// AUTO_START services are verified without disabling, rewriting or taking them over.
internal sealed class WindowsGuardRegistration : IGuardRegistrationPlatform
{
    private GuardFileRecoveryJournal journal;
    private string root;
    internal static string DemandEntry()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                if (!string.Equals(process.MainModule.FileName, TrafficGuardServiceIdentity.SetupExe, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
            TrafficGuardServiceIdentity.ValidateProtected(TrafficGuardServiceIdentity.InstallRoot, true);
            TrafficGuardServiceIdentity.ValidateProtected(TrafficGuardServiceIdentity.SetupExe, false);
            return identity.User.Value;
        }
    }
    public void ValidatePayload()
    {
        DemandEntry();
        foreach (string relative in new[] { "NOVA VPN.exe", "NOVA Traffic Guard.exe", "NOVA Guard Recovery.exe", "NOVA Core Host.exe", "core\\sing-box.exe" })
        {
            string file = Path.Combine(TrafficGuardServiceIdentity.InstallRoot, relative);
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(file)); parent != null && parent.FullName.Length >= TrafficGuardServiceIdentity.InstallRoot.Length; parent = parent.Parent)
                TrafficGuardServiceIdentity.ValidateProtected(parent.FullName, parent.FullName.Equals(TrafficGuardServiceIdentity.InstallRoot, StringComparison.OrdinalIgnoreCase));
            TrafficGuardServiceIdentity.ValidateProtected(file, false);
        }
        // Managed dependencies are executable code under LocalSystem as well.
        var pending = new System.Collections.Generic.Stack<string>(); pending.Push(TrafficGuardServiceIdentity.InstallRoot);
        while (pending.Count != 0)
        {
            string directory = pending.Pop(); TrafficGuardServiceIdentity.ValidateProtected(directory, directory == TrafficGuardServiceIdentity.InstallRoot);
            foreach (string file in Directory.GetFiles(directory)) TrafficGuardServiceIdentity.ValidateProtected(file, false);
            foreach (string child in Directory.GetDirectories(directory)) { TrafficGuardServiceIdentity.ValidateProtected(child, false); pending.Push(child); }
        }
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
    }
    public IDisposable AcquireJournalLease()
    {
        DemandEntry(); GuardMachineJournalSecurity.PrepareDirectory(root);
        journal = new GuardFileRecoveryJournal(Path.Combine(root, "TrafficGuard"), true); return journal;
    }
    public void RequireDisabledState()
    {
        DemandEntry();
        if (journal == null) throw new InvalidOperationException("Journal lease required.");
        var intent = journal.Read();
        if (intent != null && intent.Phase != GuardRecoveryPhase.Disabled) throw new InvalidOperationException("Preserve existing guard intent; use guarded servicing or explicit administrative recovery first.");
        using (var store = TrafficGuardNativeStore.OpenReadOnly())
            if (store.Snapshot().FilterCount != 0) throw new InvalidOperationException("Existing NOVA filters require administrative recovery before a new registration.");
    }
    public GuardOwnerPolicy ReadOwner()
    {
        DemandEntry();
        string path = Path.Combine(root, "owner-policy.bin");
        if (!File.Exists(path)) return null;
        GuardMachineJournalSecurity.Validate(root, true); GuardMachineJournalSecurity.Validate(path, false);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length < 34 || stream.Length > 94) throw new InvalidDataException("Invalid installed owner policy size.");
            byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
            while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return GuardOwnerPolicy.Decode(bytes);
        }
    }
    public void WriteNewOwner(GuardOwnerPolicy owner)
    {
        DemandEntry(); if (journal == null) throw new InvalidOperationException("Journal lease required.");
        string path = Path.Combine(root, "owner-policy.bin");
        if (File.Exists(path)) throw new IOException("Owner policy already exists; never overwrite it.");
        string temporary = Path.Combine(root, "owner-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = owner.Encode();
            using (var stream = GuardMachineJournalSecurity.Open(temporary, FileMode.CreateNew, FileShare.None, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public bool HasCreationReceipt(GuardOwnerPolicy owner)
    {
        DemandEntry(); if (journal == null) throw new InvalidOperationException("Journal lease required.");
        string path = Path.Combine(root, "registration-pending.bin");
        if (!File.Exists(path)) return false;
        GuardMachineJournalSecurity.Validate(path, false);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != 24) throw new InvalidDataException("Invalid registration receipt size.");
            using (var reader = new BinaryReader(stream))
                return System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) == "NOVAREG1" && new Guid(reader.ReadBytes(16)) == owner.InstallationId;
        }
    }
    public void PrepareCreationReceipt(GuardOwnerPolicy owner)
    {
        DemandEntry(); if (journal == null) throw new InvalidOperationException("Journal lease required.");
        string path = Path.Combine(root, "registration-pending.bin");
        if (File.Exists(path)) { if (!HasCreationReceipt(owner)) throw new InvalidDataException("Registration receipt belongs to a different installation."); return; }
        string pending = Path.Combine(root, "registration-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes("NOVAREG1").Concat(owner.InstallationId.ToByteArray()).ToArray();
            using (var stream = GuardMachineJournalSecurity.Open(pending, FileMode.CreateNew, FileShare.None, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            File.Move(pending, path);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public void CompleteCreationReceipt(GuardOwnerPolicy owner)
    {
        if (!HasCreationReceipt(owner)) throw new InvalidDataException("Registration receipt changed before confirmation.");
        File.Delete(Path.Combine(root, "registration-pending.bin"));
    }

    [StructLayout(LayoutKind.Sequential)] private struct Config { public uint Type, Start, Error; public IntPtr Binary, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    private static IntPtr Manager(uint access) { IntPtr value = OpenSCManager(null, null, access); if (value == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); return value; }
    public GuardServiceConfiguration ReadService()
    {
        DemandEntry(); IntPtr manager = Manager(1);
        try
        {
            IntPtr service = OpenService(manager, TrafficGuardCompiler.ServiceName, 1 | 0x20000);
            if (service == IntPtr.Zero) { int error = Marshal.GetLastWin32Error(); if (error == 1060) return null; throw new Win32Exception(error); }
            try { return Read(service); } finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static GuardServiceConfiguration Read(IntPtr service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out uint length);
        int error = Marshal.GetLastWin32Error(); if (error != 122 || length < Marshal.SizeOf<Config>() || length > 8192) throw new Win32Exception(error);
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!QueryServiceConfig(service, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<Config>(buffer); var dependencies = new System.Collections.Generic.List<string>();
            if (config.Dependencies != IntPtr.Zero)
            {
                int offset = checked((int)(config.Dependencies.ToInt64() - buffer.ToInt64()));
                if (offset < 0 || offset >= length) throw new InvalidDataException("Invalid SCM dependency pointer.");
                string current = "";
                for (; offset + 1 < length; offset += 2)
                {
                    char ch = (char)Marshal.ReadInt16(buffer, offset);
                    if (ch == '\0') { if (current.Length == 0) break; dependencies.Add(current); current = ""; }
                    else current += ch;
                }
                if (offset + 1 >= length) throw new InvalidDataException("Unterminated SCM dependencies.");
            }
            var security = ReadSecurity(service);
            return new GuardServiceConfiguration { Type = config.Type, Start = config.Start, Error = config.Error, Binary = ReadString(buffer, length, config.Binary), Account = ReadString(buffer, length, config.Account), Group = ReadString(buffer, length, config.Group), Dependencies = dependencies.ToArray(), SafeSecurity = SafeDescriptor(security), RecoverableSecurity = RecoverableDescriptor(security) };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string ReadString(IntPtr buffer, uint size, IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;
        long offset = pointer.ToInt64() - buffer.ToInt64();
        if (offset < 0 || offset >= size || (offset & 1) != 0) throw new InvalidDataException("Invalid SCM string pointer.");
        var text = new System.Text.StringBuilder();
        for (; offset + 1 < size; offset += 2) { char ch = (char)Marshal.ReadInt16(buffer, (int)offset); if (ch == '\0') return text.ToString(); text.Append(ch); }
        throw new InvalidDataException("Unterminated SCM string.");
    }
    private static RawSecurityDescriptor ReadSecurity(IntPtr service)
    {
        QueryServiceObjectSecurity(service, 5, null, 0, out uint size);
        int error = Marshal.GetLastWin32Error(); if (error != 122 || size < 20 || size > 65536) throw new Win32Exception(error);
        byte[] bytes = new byte[size]; if (!QueryServiceObjectSecurity(service, 5, bytes, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new RawSecurityDescriptor(bytes, 0);
    }
    internal static bool SafeDescriptor(RawSecurityDescriptor descriptor)
        => CheckDescriptor(descriptor, true);
    internal static bool RecoverableDescriptor(RawSecurityDescriptor descriptor)
        => CheckDescriptor(descriptor, false);
    private static bool CheckDescriptor(RawSecurityDescriptor descriptor, bool strict)
    {
        if (descriptor.Owner == null || descriptor.Owner.Value != "S-1-5-18" && descriptor.Owner.Value != "S-1-5-32-544" || descriptor.DiscretionaryAcl == null || strict && (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0) return false;
        bool system = false, admin = false;
        foreach (GenericAce raw in descriptor.DiscretionaryAcl)
        {
            var ace = raw as CommonAce;
            if (ace == null || ace.IsCallback || ace.AceFlags != AceFlags.None || ace.AceQualifier != AceQualifier.AccessAllowed) return false;
            if (ace.SecurityIdentifier.Value == "S-1-5-18") system |= strict ? (ace.AccessMask & 0xF01FF) == 0xF01FF : (ace.AccessMask & 0x20005) == 0x20005 || (unchecked((uint)ace.AccessMask) & 0x90000000u) != 0;
            else if (ace.SecurityIdentifier.Value == "S-1-5-32-544") admin |= (ace.AccessMask & 0xF01FF) == 0xF01FF || !strict && (ace.AccessMask & 0x10000000) != 0;
            // A prepared disabled service may expose control/status rights, but
            // never configuration, deletion, DACL or owner writes to an outsider.
            else if ((unchecked((uint)ace.AccessMask) & ~(strict ? 0x20005u : 0xA00201FDu)) != 0) return false;
        }
        return system && admin;
    }
    public void RegisterOrComplete()
    {
        DemandEntry(); if (journal == null) throw new InvalidOperationException("Journal lease required.");
        IntPtr manager = Manager(1 | 2);
        try
        {
            IntPtr service = CreateService(manager, TrafficGuardCompiler.ServiceName, "NOVA VPN Traffic Guard", 1 | 2 | 0xE0000, 16, 4, 1, "\"" + TrafficGuardServiceIdentity.ServiceExe + "\"", null, IntPtr.Zero, "BFE\0\0", null, null);
            bool created = service != IntPtr.Zero;
            if (!created)
            {
                int error = Marshal.GetLastWin32Error(); if (error != 1073) throw new Win32Exception(error);
                service = OpenService(manager, TrafficGuardCompiler.ServiceName, 1 | 2 | 0xE0000); if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var initial = Read(service);
                // The newly returned creation handle identifies our own object;
                // do not require its OS-default ACL to already be our final ACL.
                // Existing objects need the full prepared identity/ACL proof.
                if (!created) initial.Validate(true);
                if (!HasCreationReceipt(ReadOwner() ?? throw new InvalidDataException("Missing installed owner."))) throw new InvalidDataException("Missing creation receipt.");
                if (initial.Start == 4 && (created || !initial.SafeSecurity))
                {
                    var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20005;;;AU)");
                    byte[] owner = new byte[descriptor.Owner.BinaryLength], acl = new byte[descriptor.DiscretionaryAcl.BinaryLength];
                    descriptor.Owner.GetBinaryForm(owner, 0); descriptor.DiscretionaryAcl.GetBinaryForm(acl, 0);
                    var ownerPin = GCHandle.Alloc(owner, GCHandleType.Pinned); var aclPin = GCHandle.Alloc(acl, GCHandleType.Pinned);
                    try { uint error = SetSecurityInfo(service, ResourceType.Service, 0x80000005, ownerPin.AddrOfPinnedObject(), IntPtr.Zero, aclPin.AddrOfPinnedObject(), IntPtr.Zero); if (error != 0) throw new Win32Exception((int)error); }
                    finally { aclPin.Free(); ownerPin.Free(); }
                }
                var current = Read(service); current.Validate(true);
                if (current.Start == 4 && !ChangeServiceConfig(service, uint.MaxValue, 2, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Read(service).Validate();
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint length, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password, string display);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceObjectSecurity(IntPtr service, uint information, byte[] bytes, uint length, out uint required);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(IntPtr service, ResourceType objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}

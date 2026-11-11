using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace NovaVpn;

// ABI follows Microsoft's fwptypes.h/fwpmtypes.h. Persistent protection and
// lifetime-bound dynamic core permits use separate engine sessions.
internal static class GuardWfp
{
    [StructLayout(LayoutKind.Sequential)] internal struct Display { public IntPtr Name, Description; }
    [StructLayout(LayoutKind.Sequential)] internal struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct Union
    {
        [FieldOffset(0)] public IntPtr Pointer;
        [FieldOffset(0)] public uint U32;
        [FieldOffset(0)] public ushort U16;
        [FieldOffset(0)] public byte U8;
        [FieldOffset(0)] public float Float;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Value { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct Condition { public Guid Field; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] internal struct Action { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] internal struct Context { [FieldOffset(0)] public ulong Raw; [FieldOffset(0)] public Guid Key; }
    [StructLayout(LayoutKind.Sequential)] internal struct Filter
    {
        public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob ProviderData;
        public Guid Layer, Sublayer; public Value Weight; public uint Count; public IntPtr Conditions; public Action Action;
        public Context Context; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Session { public Guid Key; public Display Display; public uint Flags, Timeout, ProcessId; public IntPtr Sid, Username; public int KernelMode; }
    [StructLayout(LayoutKind.Sequential)] internal struct Provider { public Guid Key; public Display Display; public uint Flags; public Blob Data; public IntPtr ServiceName; }
    [StructLayout(LayoutKind.Sequential)] internal struct Sublayer { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public ushort Weight; }
    [StructLayout(LayoutKind.Sequential)] internal struct Field { public IntPtr Key; public uint Type, DataType; }
    [StructLayout(LayoutKind.Sequential)] internal struct Layer { public Guid Key; public Display Display; public uint Flags, Count; public IntPtr Fields; public Guid DefaultSublayer; public ushort Id; }
    internal static readonly Guid App = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971"), Address = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045"),
        Port = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b"), Protocol = new Guid("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7"),
        LocalInterface = new Guid("4cd62a49-59c3-4969-b7f3-bda5d32890a4"), Flags = new Guid("632ce23b-5167-435c-86d7-e903684aa80c"),
        User = new Guid("af043a0a-b34d-4f86-979c-c90371af6e66");
    internal static Guid LayerKey(GuardLayer layer) => new Guid(new[] {
        "c38d57d1-05a7-4c33-904f-7fbceee60e82", "4a72393b-319f-44bc-84c3-ba54dcb3b6b4",
        "e1cd9fe7-f4b5-4273-96c0-592e487b8650", "a3b42c97-9f04-4672-b87e-cee9c483257f",
        "09e61aea-d214-46e2-9b21-b26b0b2f28c8", "e1735bde-013f-4655-b351-a49e15762df0",
        "a82acc24-4ee1-4ee1-b465-fd1d25cb10a4", "7b964818-19c7-493a-b71f-832c3684d28c",
        "a82acc24-4ee1-4ee1-b465-fd1d25cb10a4", "7b964818-19c7-493a-b71f-832c3684d28c" }[(int)layer]);
    internal static void Check(uint error) { if (error != 0) throw new InvalidOperationException("WFP: 0x" + error.ToString("X8", CultureInfo.InvariantCulture)); }
    internal static bool ConditionTypeSupported(Guid field, uint classifyType, uint conditionType)
    {
        // FWPM_FIELD0 describes the value passed to classify. ALE user fields
        // may expose a token (15/16), matched against an SD condition (14).
        // Do not relax type validation for another field or a raw SID condition.
        if (field == User && conditionType == 14) return classifyType == 14 || classifyType == 15 || classifyType == 16;
        return classifyType == conditionType;
    }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] internal static extern uint FwpmEngineOpen0(string server, uint auth, IntPtr identity, ref Session session, out IntPtr handle);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmEngineClose0(IntPtr handle);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionBegin0(IntPtr handle, uint flags);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionCommit0(IntPtr handle);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionAbort0(IntPtr handle);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmProviderAdd0(IntPtr handle, ref Provider provider, IntPtr security);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmProviderGetByKey0(IntPtr handle, ref Guid key, out IntPtr provider);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmSubLayerAdd0(IntPtr handle, ref Sublayer sublayer, IntPtr security);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmSubLayerGetByKey0(IntPtr handle, ref Guid key, out IntPtr sublayer);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterAdd0(IntPtr handle, ref Filter filter, IntPtr security, out ulong id);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterDeleteByKey0(IntPtr handle, ref Guid key);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterCreateEnumHandle0(IntPtr handle, IntPtr template, out IntPtr enumeration);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterEnum0(IntPtr handle, IntPtr enumeration, uint count, out IntPtr entries, out uint returned);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterDestroyEnumHandle0(IntPtr handle, IntPtr enumeration);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmLayerGetByKey0(IntPtr handle, ref Guid key, out IntPtr layer);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] internal static extern uint FwpmGetAppIdFromFileName0(string file, out IntPtr blob);
    [DllImport("fwpuclnt.dll")] internal static extern void FwpmFreeMemory0(ref IntPtr memory);
}

internal sealed class GuardNativeArena : IDisposable
{
    private readonly List<IntPtr> allocations = new List<IntPtr>(), wfpAllocations = new List<IntPtr>();
    private readonly Dictionary<string, IntPtr> applications = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
    public IntPtr Bytes(byte[] bytes) { IntPtr p = Marshal.AllocHGlobal(bytes.Length); allocations.Add(p); Marshal.Copy(bytes, 0, p, bytes.Length); return p; }
    public IntPtr Text(string text) { IntPtr p = Marshal.StringToHGlobalUni(text); allocations.Add(p); return p; }
    public IntPtr Struct<T>(T value) where T : struct { IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); allocations.Add(p); Marshal.StructureToPtr(value, p, false); return p; }
    public GuardWfp.Value UInt64(ulong value) => new GuardWfp.Value { Type = 4, Data = new GuardWfp.Union { Pointer = Struct(value) } };
    public IntPtr AppId(string path)
    {
        path = TrafficGuardCompiler.LocalExecutable(path);
        if (!applications.TryGetValue(path, out var p)) { GuardWfp.Check(GuardWfp.FwpmGetAppIdFromFileName0(path, out p)); wfpAllocations.Add(p); applications.Add(path, p); }
        return p;
    }
    public GuardWfp.Condition Condition(GuardCondition condition)
    {
        var result = new GuardWfp.Condition();
        switch (condition.Field)
        {
            case GuardField.Application: result.Field = GuardWfp.App; result.Value = new GuardWfp.Value { Type = 12, Data = new GuardWfp.Union { Pointer = AppId(condition.Value) } }; break;
            case GuardField.OwnerUser:
                // ALE_USER_ID is an access check, not a raw SID comparison.
                // FWP_ACTRL_MATCH_FILTER=1; one non-inherited allow ACE only.
                // https://learn.microsoft.com/en-us/windows/win32/fwp/permitting-and-blocking-applications-and-users
                // The future service must derive this SID from the authenticated
                // owner token, never accept an arbitrary group SID from IPC.
                var sid = new SecurityIdentifier(condition.Value);
                var acl = new RawAcl(2, 1);
                acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 1, sid, false, null));
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative, system, system, null, acl);
                byte[] sd = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(sd, 0);
                result.Field = GuardWfp.User; result.Value = new GuardWfp.Value { Type = 14, Data = new GuardWfp.Union { Pointer = Struct(new GuardWfp.Blob { Size = (uint)sd.Length, Data = Bytes(sd) }) } }; break;
            case GuardField.RemoteAddress:
                result.Field = GuardWfp.Address; byte[] address = System.Net.IPAddress.Parse(condition.Value).GetAddressBytes();
                result.Value = address.Length == 4 ? new GuardWfp.Value { Type = 3, Data = new GuardWfp.Union { U32 = ((uint)address[0] << 24) | ((uint)address[1] << 16) | ((uint)address[2] << 8) | address[3] } } :
                    new GuardWfp.Value { Type = 11, Data = new GuardWfp.Union { Pointer = Bytes(address) } }; break;
            case GuardField.RemotePort: result.Field = GuardWfp.Port; result.Value = new GuardWfp.Value { Type = 2, Data = new GuardWfp.Union { U16 = ushort.Parse(condition.Value, CultureInfo.InvariantCulture) } }; break;
            case GuardField.Protocol: result.Field = GuardWfp.Protocol; result.Value = new GuardWfp.Value { Type = 1, Data = new GuardWfp.Union { U8 = byte.Parse(condition.Value, CultureInfo.InvariantCulture) } }; break;
            case GuardField.LocalInterface: result.Field = GuardWfp.LocalInterface; result.Value = UInt64(ulong.Parse(condition.Value, CultureInfo.InvariantCulture)); break;
            case GuardField.Loopback: result.Field = GuardWfp.Flags; result.Match = 6; result.Value = new GuardWfp.Value { Type = 3, Data = new GuardWfp.Union { U32 = 1 } }; break;
            default: throw new NotSupportedException();
        }
        return result;
    }
    public GuardWfp.Filter Filter(GuardFilter source, string fingerprint)
    {
        var conditions = source.Conditions.Select(Condition).ToArray(); int size = Marshal.SizeOf<GuardWfp.Condition>();
        IntPtr list = IntPtr.Zero;
        if (conditions.Length > 0) { list = Marshal.AllocHGlobal(size * conditions.Length); allocations.Add(list); for (int i = 0; i < conditions.Length; i++) Marshal.StructureToPtr(conditions[i], IntPtr.Add(list, i * size), false); }
        byte[] annotation = Encoding.UTF8.GetBytes(fingerprint);
        return new GuardWfp.Filter { Key = source.Key, Display = new GuardWfp.Display { Name = Text("NOVA Traffic Guard / " + source.Role) },
            Flags = source.Flags, Provider = Struct(TrafficGuardCompiler.ProviderKey), ProviderData = new GuardWfp.Blob { Size = (uint)annotation.Length, Data = Bytes(annotation) },
            Layer = GuardWfp.LayerKey(source.Layer), Sublayer = TrafficGuardCompiler.SublayerKey, Weight = UInt64(source.Weight), Count = (uint)conditions.Length, Conditions = list,
            Action = new GuardWfp.Action { Type = source.Permit ? 0x1002u : 0x1001u } };
    }
    public IntPtr Security()
    {
        var sd = new RawSecurityDescriptor("O:SYG:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)"); var bytes = new byte[sd.BinaryLength]; sd.GetBinaryForm(bytes, 0); return Bytes(bytes);
    }
    public void Dispose() { foreach (var p in wfpAllocations) { IntPtr pointer = p; GuardWfp.FwpmFreeMemory0(ref pointer); } foreach (var p in allocations) Marshal.FreeHGlobal(p); }
}

public sealed class TrafficGuardNativeSnapshot
{
    public int FilterCount { get; internal set; }
    public int PersistentCount { get; internal set; }
    public int BootCount { get; internal set; }
    public int DisabledCount { get; internal set; }
}

public sealed partial class TrafficGuardNativeStore : IDisposable, IGuardRecoveryBackend, IGuardClosedRecoveryBackend
{
    private IntPtr engine;
    private IntPtr coreEngine;
    private GuardCoreEndpointBinding coreEngineBinding;
    private readonly bool writable;
    private readonly bool recoveryOnly;
    private CloseAuthority closeAuthority;
    private sealed class CloseAuthority : IDisposable
    {
        readonly Process process;
        readonly FileStream ownerLease;
        readonly string owner;
        readonly long creation;
        private CloseAuthority(Process process, FileStream lease, string owner)
        { this.process = process; ownerLease = lease; this.owner = owner; creation = process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
        internal static CloseAuthority Capture(GuardOwnerPolicy expected)
        {
            TrafficGuardServiceIdentity.Demand();
            Process process = Process.GetCurrentProcess(); FileStream lease = null;
            try
            {
                // Unlike a STOP_PENDING PID, RUNNING registration is a valid
                // authority source. Keep this exact process and owner file alive.
                TrafficGuardServiceIdentity.ValidateServerProcess((uint)process.Id);
                if (process.Handle == IntPtr.Zero) throw new InvalidOperationException("Missing retained process handle.");
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
                string path = Path.Combine(root, "owner-policy.bin");
                TrafficGuardServiceIdentity.ValidateProtected(root, true); TrafficGuardServiceIdentity.ValidateProtected(path, false);
                lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (expected == null || lease.Length < 34 || lease.Length > 94) throw new UnauthorizedAccessException("Installed owner required for cleanup authority.");
                byte[] bytes = new byte[(int)lease.Length]; int offset = 0;
                while (offset < bytes.Length) { int count = lease.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
                var actual = GuardOwnerPolicy.Decode(bytes);
                if (actual.OwnerSid != expected.OwnerSid || actual.InstallationId != expected.InstallationId) throw new UnauthorizedAccessException("Installed owner changed.");
                return new CloseAuthority(process, lease, actual.OwnerSid);
            }
            catch { lease?.Dispose(); process.Dispose(); throw; }
        }
        internal void Demand(TrafficGuardPlan plan)
        {
            GuardClosedPolicy.Demand(plan, owner);
            TrafficGuardServiceIdentity.DemandProcess();
            using (var current = Process.GetCurrentProcess())
                if (process.HasExited || process.Id != current.Id || creation != current.StartTime.ToUniversalTime().ToFileTimeUtc() || !ownerLease.CanRead) throw new UnauthorizedAccessException("Cleanup authority lifetime ended.");
            TrafficGuardServiceIdentity.ValidateProtected(TrafficGuardServiceIdentity.InstallRoot, true);
            TrafficGuardServiceIdentity.ValidateProtected(TrafficGuardServiceIdentity.ServiceExe, false);
            TrafficGuardServiceIdentity.ValidateProtected(Path.Combine(TrafficGuardServiceIdentity.InstallRoot, "NOVA VPN.exe"), false);
        }
        public void Dispose() { ownerLease.Dispose(); process.Dispose(); }
    }
    internal void BindCloseAuthority(GuardOwnerPolicy owner)
    {
        if (!writable || recoveryOnly || engine == IntPtr.Zero || closeAuthority != null) throw new InvalidOperationException("Cleanup authority requires a fresh service writer.");
        closeAuthority = CloseAuthority.Capture(owner);
    }
    void IGuardClosedRecoveryBackend.ReplaceClosed(TrafficGuardPlan plan)
    {
        if (!writable || recoveryOnly || engine == IntPtr.Zero || closeAuthority == null) throw new UnauthorizedAccessException("No lifetime cleanup authority.");
        closeAuthority.Demand(plan); ReplaceCore(plan);
    }
    private TrafficGuardNativeStore(bool write, bool recoveryOnly = false)
    {
        if (write) { if (recoveryOnly) TrafficGuardServiceIdentity.DemandRecovery(); else TrafficGuardServiceIdentity.Demand(); }
        writable = write; this.recoveryOnly = recoveryOnly;
        var session = new GuardWfp.Session { Key = Guid.NewGuid(), Timeout = 10000 }; // flags=0; never DYNAMIC.
        GuardWfp.Check(GuardWfp.FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out engine));
    }
    public static TrafficGuardNativeStore OpenReadOnly() => new TrafficGuardNativeStore(false);
    internal static TrafficGuardNativeStore OpenForService() => new TrafficGuardNativeStore(true);
    internal static TrafficGuardNativeStore OpenForRecovery() => new TrafficGuardNativeStore(true, true);
    private void DemandWrite(bool replacing = false)
    {
        if (!writable) throw new InvalidOperationException("Сеанс WFP открыт только для чтения.");
        if (recoveryOnly) { if (replacing) throw new InvalidOperationException("Administrative recovery cannot install/replace filters."); TrafficGuardServiceIdentity.DemandRecovery(); }
        else TrafficGuardServiceIdentity.Demand();
    }

    private List<GuardWfp.Filter> OwnedFilters()
    {
        var result = new List<GuardWfp.Filter>();
        VisitOwnedFilters(filter =>
        {
            // Enumeration memory owns pointers. Only scalar fields/keys are retained.
            filter.Provider = IntPtr.Zero; filter.Conditions = IntPtr.Zero; filter.ProviderData = default(GuardWfp.Blob);
            filter.Display = default(GuardWfp.Display); filter.Weight = filter.EffectiveWeight = default(GuardWfp.Value); filter.Reserved = IntPtr.Zero;
            result.Add(filter);
        });
        return result;
    }
    private void VisitOwnedFilters(Action<GuardWfp.Filter> visitor)
    {
        VisitFilters(filter =>
        {
            if (filter.Provider != IntPtr.Zero && Marshal.PtrToStructure<Guid>(filter.Provider) == TrafficGuardCompiler.ProviderKey) visitor(filter);
        });
    }
    private void VisitFilters(Action<GuardWfp.Filter> visitor, IntPtr handle = default(IntPtr))
    {
        // Bound each allocation while avoiding a separate RPC for every 256
        // rows in large BFE tables. Still visit every row, including foreign,
        // disabled and boot-time filters; never truncate to the requested size.
        const uint pageSize = 1024;
        if (handle == IntPtr.Zero) handle = engine;
        GuardWfp.Check(GuardWfp.FwpmFilterCreateEnumHandle0(handle, IntPtr.Zero, out var enumeration));
        int total = 0;
        try
        {
            while (true)
            {
                IntPtr entries = IntPtr.Zero;
                try
                {
                    GuardWfp.Check(GuardWfp.FwpmFilterEnum0(handle, enumeration, pageSize, out entries, out uint count));
                    if (count > pageSize || count > 0 && entries == IntPtr.Zero) throw new InvalidDataException("Некорректная страница перечисления WFP.");
                    if (count == 0) break;
                    total = checked(total + (int)count);
                    if (total > 1000000) throw new InvalidDataException("Превышен предел перечисления WFP.");
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr pointer = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        if (pointer == IntPtr.Zero) throw new InvalidDataException("Пустой указатель фильтра WFP.");
                        var filter = Marshal.PtrToStructure<GuardWfp.Filter>(pointer);
                        visitor(filter); // Consume all native pointers before page memory is freed.
                    }
                }
                finally { if (entries != IntPtr.Zero) GuardWfp.FwpmFreeMemory0(ref entries); }
            }
        }
        finally { GuardWfp.FwpmFilterDestroyEnumHandle0(handle, enumeration); }
    }
    bool IGuardRecoveryBackend.HasOwnedFilters() => Snapshot().FilterCount != 0;
    void IGuardRecoveryBackend.RemoveOwnedFilters() => RemoveOwnedProtection();
    public TrafficGuardNativeSnapshot Snapshot()
    {
        GuardWfp.Check(GuardWfp.FwpmTransactionBegin0(engine, 1));
        try { var filters = OwnedFilters(); return new TrafficGuardNativeSnapshot { FilterCount = filters.Count, PersistentCount = filters.Count(f => (f.Flags & 1) != 0), BootCount = filters.Count(f => (f.Flags & 2) != 0), DisabledCount = filters.Count(f => (f.Flags & 0x20) != 0) }; }
        finally { GuardWfp.FwpmTransactionAbort0(engine); }
    }
    public void ValidateLayers(TrafficGuardPlan plan)
    {
        using (var arena = new GuardNativeArena())
        foreach (var group in plan.Filters.GroupBy(f => f.Layer))
        {
            Guid key = GuardWfp.LayerKey(group.Key); IntPtr pointer = IntPtr.Zero;
            try
            {
                GuardWfp.Check(GuardWfp.FwpmLayerGetByKey0(engine, ref key, out pointer)); var layer = Marshal.PtrToStructure<GuardWfp.Layer>(pointer);
                if (layer.Count > 1024 || layer.Count > 0 && layer.Fields == IntPtr.Zero) throw new InvalidDataException("Некорректная схема уровня WFP.");
                var fields = new Dictionary<Guid, uint>(); for (int i = 0; i < layer.Count; i++) { var field = Marshal.PtrToStructure<GuardWfp.Field>(IntPtr.Add(layer.Fields, i * Marshal.SizeOf<GuardWfp.Field>())); fields.Add(Marshal.PtrToStructure<Guid>(field.Key), field.DataType); }
                foreach (var condition in group.SelectMany(f => f.Conditions)) { var native = arena.Condition(condition); if (!fields.TryGetValue(native.Field, out uint type) || !GuardWfp.ConditionTypeSupported(native.Field, type, native.Value.Type)) throw new NotSupportedException("Условие или его тип не поддерживается выбранным уровнем WFP."); }
            }
            finally { if (pointer != IntPtr.Zero) GuardWfp.FwpmFreeMemory0(ref pointer); }
        }
    }
    public void Replace(TrafficGuardPlan plan)
    {
        // Commit only acknowledges a native transaction. The service must verify
        // full rule readback before reporting protection to a desktop client.
        DemandWrite(true); if (plan == null) throw new ArgumentNullException(nameof(plan));
        ReplaceCore(plan);
    }
    private void ReplaceCore(TrafficGuardPlan plan)
    {
        TrafficGuardServiceIdentity.ValidatePlanFiles(plan); ValidateLayers(plan);
        // Revoke the previous lifetime first. A crash between the two commits
        // leaves only closed protection, never a persisted core exception.
        CloseCoreEngine();
        GuardWfp.Check(GuardWfp.FwpmTransactionBegin0(engine, 0)); bool committed = false;
        try
        {
            using (var arena = new GuardNativeArena())
            {
                if (HasPotentialForeignOverride(plan)) throw new InvalidOperationException("Potential conflicting WFP policy; Traffic Guard activation refused.");
                EnsureProvider(arena);
                foreach (var old in OwnedFilters()) { Guid key = old.Key; GuardWfp.Check(GuardWfp.FwpmFilterDeleteByKey0(engine, ref key)); }
                foreach (var rule in plan.Filters.Where(rule => !IsDynamicCoreRule(plan, rule))) { var filter = arena.Filter(rule, plan.Fingerprint); GuardWfp.Check(GuardWfp.FwpmFilterAdd0(engine, ref filter, arena.Security(), out _)); }
                GuardWfp.Check(GuardWfp.FwpmTransactionCommit0(engine)); committed = true;
            }
        }
        finally { if (!committed) GuardWfp.FwpmTransactionAbort0(engine); }
        if (plan.CoreBinding != null) InstallCoreGrants(plan);
    }
    internal static bool IsDynamicCoreRule(TrafficGuardPlan plan, GuardFilter rule)
        => plan.CoreBinding != null && (rule.Role == "core-endpoint" || rule.Role == "verified-tunnel");
    private void InstallCoreGrants(TrafficGuardPlan plan)
    {
        if (!plan.CoreAuthorityCurrent()) throw new UnauthorizedAccessException("Core lifetime ended before dynamic grants.");
        var rules = plan.Filters.Where(rule => IsDynamicCoreRule(plan, rule)).ToArray();
        if (rules.Length == 0) return;
        if (rules.Any(rule => !rule.Permit || rule.Flags != 0)) throw new InvalidOperationException("Dynamic core grants cannot be persistent or boot-time.");
        var session = new GuardWfp.Session { Key = Guid.NewGuid(), Flags = 1, Timeout = 10000 }; // FWPM_SESSION_FLAG_DYNAMIC
        GuardWfp.Check(GuardWfp.FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out coreEngine));
        coreEngineBinding = plan.CoreBinding;
        bool committed = false, transaction = false;
        try
        {
            GuardWfp.Check(GuardWfp.FwpmTransactionBegin0(coreEngine, 0)); transaction = true;
            // Read arbitration through this same transaction, not another
            // session which could block behind our write lock.
            if (!plan.CoreAuthorityCurrent() || HasPotentialForeignOverride(plan, coreEngine)) throw new InvalidOperationException("Core authority or WFP arbitration changed.");
            using (var arena = new GuardNativeArena())
                foreach (var rule in rules) { var filter = arena.Filter(rule, plan.Fingerprint); GuardWfp.Check(GuardWfp.FwpmFilterAdd0(coreEngine, ref filter, arena.Security(), out _)); }
            GuardWfp.Check(GuardWfp.FwpmTransactionCommit0(coreEngine)); committed = true;
        }
        finally
        {
            if (!committed)
            {
                try { if (transaction) GuardWfp.FwpmTransactionAbort0(coreEngine); }
                finally { CloseCoreEngine(); }
            }
        }
    }
    private void CloseCoreEngine()
    {
        if (coreEngine == IntPtr.Zero) return;
        // Keep the handle on failure, so cleanup can be retried and a failed
        // revoke cannot be acknowledged as a successful replacement.
        GuardWfp.Check(GuardWfp.FwpmEngineClose0(coreEngine)); coreEngine = IntPtr.Zero; coreEngineBinding = null;
    }
    internal void RevokeCoreGrants(GuardCoreEndpointBinding binding)
    {
        // A journal/closed-policy failure must not retain endpoint permits.
        // Revocation accepts the retained capability by reference even after
        // its process exits; it never creates or broadens any permission.
        TrafficGuardServiceIdentity.DemandProcess();
        if (coreEngine == IntPtr.Zero) return;
        if (binding == null || !ReferenceEquals(binding, coreEngineBinding)) throw new UnauthorizedAccessException("Different core grant lifetime.");
        CloseCoreEngine();
    }
    public void RemoveOwnedProtection()
    {
        DemandWrite(); CloseCoreEngine(); GuardWfp.Check(GuardWfp.FwpmTransactionBegin0(engine, 0)); bool committed = false;
        try { foreach (var old in OwnedFilters()) { Guid key = old.Key; GuardWfp.Check(GuardWfp.FwpmFilterDeleteByKey0(engine, ref key)); } GuardWfp.Check(GuardWfp.FwpmTransactionCommit0(engine)); committed = true; }
        finally { if (!committed) GuardWfp.FwpmTransactionAbort0(engine); }
    }
    private void EnsureProvider(GuardNativeArena arena)
    {
        Guid key = TrafficGuardCompiler.ProviderKey; IntPtr pointer = IntPtr.Zero;
        uint found = GuardWfp.FwpmProviderGetByKey0(engine, ref key, out pointer);
        try
        {
            if (found == 0) { var existing = Marshal.PtrToStructure<GuardWfp.Provider>(pointer); if ((existing.Flags & 1) == 0 || (existing.Flags & 0x10) != 0 || Marshal.PtrToStringUni(existing.ServiceName) != TrafficGuardCompiler.ServiceName) throw new InvalidOperationException("Поставщик WFP отключён или имеет другую регистрацию."); }
            else if (found == 0x80320005) { var provider = new GuardWfp.Provider { Key = key, Flags = 1, Display = new GuardWfp.Display { Name = arena.Text("NOVA VPN Traffic Guard") }, ServiceName = arena.Text(TrafficGuardCompiler.ServiceName) }; GuardWfp.Check(GuardWfp.FwpmProviderAdd0(engine, ref provider, arena.Security())); }
            else GuardWfp.Check(found);
        }
        finally { if (pointer != IntPtr.Zero) GuardWfp.FwpmFreeMemory0(ref pointer); }
        key = TrafficGuardCompiler.SublayerKey; found = GuardWfp.FwpmSubLayerGetByKey0(engine, ref key, out pointer);
        try
        {
            if (found == 0) { var existing = Marshal.PtrToStructure<GuardWfp.Sublayer>(pointer); if (existing.Provider == IntPtr.Zero || Marshal.PtrToStructure<Guid>(existing.Provider) != TrafficGuardCompiler.ProviderKey || (existing.Flags & 1) == 0 || existing.Weight != TrafficGuardCompiler.SublayerWeight) throw new InvalidOperationException("Подслой WFP имеет другую регистрацию или приоритет."); }
            else if (found == 0x80320007) { var sublayer = new GuardWfp.Sublayer { Key = key, Flags = 1, Display = new GuardWfp.Display { Name = arena.Text("NOVA VPN Traffic Guard") }, Provider = arena.Struct(TrafficGuardCompiler.ProviderKey), Weight = TrafficGuardCompiler.SublayerWeight }; GuardWfp.Check(GuardWfp.FwpmSubLayerAdd0(engine, ref sublayer, arena.Security())); }
            else GuardWfp.Check(found);
        }
        finally { if (pointer != IntPtr.Zero) GuardWfp.FwpmFreeMemory0(ref pointer); }
    }
    ~TrafficGuardNativeStore() { Close(); }
    private void Close()
    {
        IntPtr coreHandle = Interlocked.Exchange(ref coreEngine, IntPtr.Zero);
        IntPtr handle = Interlocked.Exchange(ref engine, IntPtr.Zero);
        try { if (coreHandle != IntPtr.Zero) GuardWfp.FwpmEngineClose0(coreHandle); if (handle != IntPtr.Zero) GuardWfp.FwpmEngineClose0(handle); }
        finally { Interlocked.Exchange(ref closeAuthority, null)?.Dispose(); }
    }
    public void Dispose() { Close(); GC.SuppressFinalize(this); }
}

public static class TrafficGuardNativeDiagnostics
{
    public static Dictionary<string, int> Layout() => new Dictionary<string, int> { ["Value"] = Marshal.SizeOf<GuardWfp.Value>(), ["Condition"] = Marshal.SizeOf<GuardWfp.Condition>(), ["Filter"] = Marshal.SizeOf<GuardWfp.Filter>(), ["Session"] = Marshal.SizeOf<GuardWfp.Session>(), ["Layer"] = Marshal.SizeOf<GuardWfp.Layer>(), ["Filter.Conditions"] = (int)Marshal.OffsetOf<GuardWfp.Filter>("Conditions"), ["Filter.Action"] = (int)Marshal.OffsetOf<GuardWfp.Filter>("Action"), ["Filter.Context"] = (int)Marshal.OffsetOf<GuardWfp.Filter>("Context"), ["Filter.Id"] = (int)Marshal.OffsetOf<GuardWfp.Filter>("Id") };
    public static string ApplicationId(string localExe)
    {
        using (var arena = new GuardNativeArena()) { var blob = Marshal.PtrToStructure<GuardWfp.Blob>(arena.AppId(localExe)); if (blob.Size < 2 || blob.Size > 65536) throw new InvalidDataException("Некорректный идентификатор приложения WFP."); return Marshal.PtrToStringUni(blob.Data, checked((int)blob.Size / 2)).TrimEnd('\0'); }
    }
}

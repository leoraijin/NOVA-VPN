using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NovaVpn;

internal static class GuardNativeReadback
{
    internal static bool BlobEqual(GuardWfp.Blob left, GuardWfp.Blob right)
    {
        if (left.Size != right.Size || left.Size > 65536) return false;
        if (left.Size == 0) return true;
        if (left.Data == IntPtr.Zero || right.Data == IntPtr.Zero) return false;
        for (int i = 0; i < left.Size; i++) if (Marshal.ReadByte(left.Data, i) != Marshal.ReadByte(right.Data, i)) return false;
        return true;
    }
    internal static bool ValueEqual(GuardWfp.Value left, GuardWfp.Value right)
    {
        if (left.Type != right.Type) return false;
        switch (left.Type)
        {
            case 1: return left.Data.U8 == right.Data.U8;
            case 2: return left.Data.U16 == right.Data.U16;
            case 3: return left.Data.U32 == right.Data.U32;
            case 4: return left.Data.Pointer != IntPtr.Zero && right.Data.Pointer != IntPtr.Zero && Marshal.ReadInt64(left.Data.Pointer) == Marshal.ReadInt64(right.Data.Pointer);
            case 11:
                return BlobEqual(new GuardWfp.Blob { Size = 16, Data = left.Data.Pointer }, new GuardWfp.Blob { Size = 16, Data = right.Data.Pointer });
            case 12:
            case 14:
                return left.Data.Pointer != IntPtr.Zero && right.Data.Pointer != IntPtr.Zero && BlobEqual(Marshal.PtrToStructure<GuardWfp.Blob>(left.Data.Pointer), Marshal.PtrToStructure<GuardWfp.Blob>(right.Data.Pointer));
            default: return false; // Do not claim verification for an unsupported native type.
        }
    }
    // BFE may add FWPM_FILTER_FLAG_INDEXED (0x40) for lookup optimization.
    // Keep every requested bit and every other returned bit exact.
    internal static bool FlagsEqual(uint expected, uint actual) => actual == expected || actual == (expected | 0x40u);
    internal static bool FilterEqual(GuardWfp.Filter expected, GuardWfp.Filter actual)
    {
        if (actual.Key != expected.Key || !FlagsEqual(expected.Flags, actual.Flags) || actual.Layer != expected.Layer || actual.Sublayer != expected.Sublayer ||
            actual.Provider == IntPtr.Zero || expected.Provider == IntPtr.Zero || Marshal.PtrToStructure<Guid>(actual.Provider) != Marshal.PtrToStructure<Guid>(expected.Provider) ||
            actual.Action.Type != expected.Action.Type || actual.Action.Key != expected.Action.Key || actual.Context.Raw != expected.Context.Raw ||
            actual.Id == 0 || !ValueEqual(expected.Weight, actual.Weight) || !ValueEqual(expected.Weight, actual.EffectiveWeight) ||
            !BlobEqual(expected.ProviderData, actual.ProviderData) || actual.Count != expected.Count || actual.Count > 64) return false;
        if (actual.Count == 0) return true;
        if (actual.Conditions == IntPtr.Zero || expected.Conditions == IntPtr.Zero) return false;
        int size = Marshal.SizeOf<GuardWfp.Condition>();
        var used = new bool[actual.Count];
        // WFP may return conditions in another order. Compare a multiset, so a
        // duplicated condition cannot stand in for a missing IP/app/port grant.
        for (int i = 0; i < expected.Count; i++)
        {
            var wanted = Marshal.PtrToStructure<GuardWfp.Condition>(IntPtr.Add(expected.Conditions, i * size));
            bool found = false;
            for (int j = 0; j < actual.Count; j++)
            {
                if (used[j]) continue;
                var item = Marshal.PtrToStructure<GuardWfp.Condition>(IntPtr.Add(actual.Conditions, j * size));
                if (wanted.Field == item.Field && wanted.Match == item.Match && ValueEqual(wanted.Value, item.Value)) { used[j] = true; found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }
}

internal sealed class GuardNativeReadbackSet
{
    private readonly Dictionary<Guid, GuardWfp.Filter> expected;
    private readonly HashSet<Guid> seen = new HashSet<Guid>();
    private bool match = true;
    internal GuardNativeReadbackSet(IEnumerable<GuardWfp.Filter> rules) { expected = rules.ToDictionary(f => f.Key); }
    internal void Accept(GuardWfp.Filter actual)
    {
        if (!expected.TryGetValue(actual.Key, out var wanted) || !seen.Add(actual.Key) || !GuardNativeReadback.FilterEqual(wanted, actual)) match = false;
    }
    internal bool Complete => match && seen.Count == expected.Count && expected.Count != 0;
}

// Conservative admission check, not a solver for condition intersections.
// Equal sublayer priorities have no ordering guarantee. Callout behavior is
// opaque here, so a relevant callout is refused even if it could be benign.
internal static class GuardForeignArbitration
{
    internal static bool PotentialOverride(uint flags, uint action, bool sameSublayer, ushort priority, bool boot)
    {
        if (((flags & 2u) != 0) != boot) return false;
        if (!boot && (flags & 0x20u) != 0) return false;
        if (!sameSublayer && priority < TrafficGuardCompiler.SublayerWeight) return false;
        if ((action & 0x4000u) != 0) return true;
        return action == 0x1002u && (sameSublayer || (flags & 8u) != 0);
    }
}

// Consumes one coherent native enumeration; sublayer lookups stay inside its
// caller's transaction and are cached only for that one scan.
internal sealed class GuardForeignScan
{
    readonly HashSet<string> denied;
    readonly Dictionary<Guid,ushort> priorities=new Dictionary<Guid,ushort>();
    readonly Func<Guid,ushort> readPriority;
    internal bool Conflict { get; private set; }
    internal GuardForeignScan(TrafficGuardPlan plan,Func<Guid,ushort> readPriority)
    {
        if(plan==null)throw new ArgumentNullException(nameof(plan));
        this.readPriority=readPriority??throw new ArgumentNullException(nameof(readPriority));
        denied=new HashSet<string>(plan.Filters.Where(f=>!f.Permit&&f.Conditions.Count==0)
            .Select(f=>GuardWfp.LayerKey(f.Layer).ToString("D")+":"+((f.Flags&2u)!=0)));
    }
    internal void Accept(Guid layer,Guid sublayer,uint flags,uint action,bool owned)
    {
        if(Conflict||owned||!denied.Contains(layer.ToString("D")+":"+((flags&2u)!=0)))return;
        bool same=sublayer==TrafficGuardCompiler.SublayerKey;
        ushort priority=TrafficGuardCompiler.SublayerWeight;
        if(!same&&!priorities.TryGetValue(sublayer,out priority)){priority=readPriority(sublayer);priorities.Add(sublayer,priority);}
        if(GuardForeignArbitration.PotentialOverride(flags,action,same,priority,(flags&2u)!=0))Conflict=true;
    }
}

public sealed partial class TrafficGuardNativeStore
{
    // Caller holds a native read/write transaction. Pointers never escape the
    // enumeration visitor; failure to read a relevant sublayer fails closed.
    // Foreign rules are never deleted. Conditions and intra-sublayer weights
    // are deliberately not used to dismiss a possible overlap.
    private GuardForeignScan ForeignScan(TrafficGuardPlan plan,IntPtr handle)
    {
        return new GuardForeignScan(plan,key=>
        {
            IntPtr pointer=IntPtr.Zero;
            try
            {
                GuardWfp.Check(GuardWfp.FwpmSubLayerGetByKey0(handle,ref key,out pointer));
                if(pointer==IntPtr.Zero)throw new InvalidOperationException("Missing WFP sublayer readback.");
                return Marshal.PtrToStructure<GuardWfp.Sublayer>(pointer).Weight;
            }
            finally{if(pointer!=IntPtr.Zero)GuardWfp.FwpmFreeMemory0(ref pointer);}
        });
    }
    private static bool IsOwned(GuardWfp.Filter filter)=>filter.Provider!=IntPtr.Zero&&Marshal.PtrToStructure<Guid>(filter.Provider)==TrafficGuardCompiler.ProviderKey;
    private bool HasPotentialForeignOverride(TrafficGuardPlan plan, IntPtr handle = default(IntPtr))
    {
        if(handle==IntPtr.Zero)handle=engine;
        var scan=ForeignScan(plan,handle);
        VisitFilters(filter=>scan.Accept(filter.Layer,filter.Sublayer,filter.Flags,filter.Action.Type,IsOwned(filter)),handle);
        return scan.Conflict;
    }
    // Exact native readback is stronger than an annotation hash or filter count,
    // but still is not proof of packet behavior or arbitration with other vendors.
    // The transaction gives registration and filter reads one coherent view.
    public bool VerifyExact(TrafficGuardPlan plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (!plan.CoreAuthorityCurrent()) return false;
        GuardWfp.Check(GuardWfp.FwpmTransactionBegin0(engine, 1));
        try
        {
            if (!VerifyRegistration()) return false;
            using (var arena = new GuardNativeArena())
            {
                var readback = new GuardNativeReadbackSet(plan.Filters.Select(f => arena.Filter(f, plan.Fingerprint)));
                var scan=ForeignScan(plan,engine);
                VisitFilters(filter=>
                {
                    bool owned=IsOwned(filter);
                    if(owned)readback.Accept(filter);
                    scan.Accept(filter.Layer,filter.Sublayer,filter.Flags,filter.Action.Type,owned);
                });
                return readback.Complete && !scan.Conflict;
            }
        }
        finally { GuardWfp.FwpmTransactionAbort0(engine); }
    }
    private bool VerifyRegistration()
    {
        Guid key = TrafficGuardCompiler.ProviderKey; IntPtr pointer = IntPtr.Zero;
        uint error = GuardWfp.FwpmProviderGetByKey0(engine, ref key, out pointer);
        try
        {
            if (error == 0x80320005) return false;
            GuardWfp.Check(error);
            if (pointer == IntPtr.Zero) return false;
            var provider = Marshal.PtrToStructure<GuardWfp.Provider>(pointer);
            if (provider.Key != key || provider.Flags != 1 || Marshal.PtrToStringUni(provider.ServiceName) != TrafficGuardCompiler.ServiceName) return false;
        }
        finally { if (pointer != IntPtr.Zero) GuardWfp.FwpmFreeMemory0(ref pointer); }
        key = TrafficGuardCompiler.SublayerKey;
        error = GuardWfp.FwpmSubLayerGetByKey0(engine, ref key, out pointer);
        try
        {
            if (error == 0x80320007) return false;
            GuardWfp.Check(error);
            if (pointer == IntPtr.Zero) return false;
            var sublayer = Marshal.PtrToStructure<GuardWfp.Sublayer>(pointer);
            return sublayer.Key == key && sublayer.Flags == 1 && sublayer.Weight == TrafficGuardCompiler.SublayerWeight &&
                sublayer.Provider != IntPtr.Zero && Marshal.PtrToStructure<Guid>(sublayer.Provider) == TrafficGuardCompiler.ProviderKey;
        }
        finally { if (pointer != IntPtr.Zero) GuardWfp.FwpmFreeMemory0(ref pointer); }
    }
}

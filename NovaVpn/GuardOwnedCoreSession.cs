using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed partial class MachineTunnelLease
{
    internal static MachineTunnelLease TryCreateForService()
    {
        TrafficGuardServiceIdentity.Demand();
        using(var current=System.Diagnostics.Process.GetCurrentProcess())TrafficGuardServiceIdentity.ValidateServerProcess((uint)current.Id);
        return TryCreateCore(DesktopScope,new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid,null));
    }
}

// Consumes only an already service-authored, private machine configuration.
// This is not an IPC JSON/file/path import API. A trusted configuration producer
// and attested dynamic TUN grants are still required before enabling the feature.
internal sealed class GuardOwnedCoreSession : IDisposable
{
    readonly object sync = new object();
    GuardOwnedProcess process;
    FileStream imageLease, configurationLease;
    readonly string image, configurationHash;
    readonly Guid session;
    readonly GuardCoreKind kind;
    bool disposed;
    GuardOwnedCoreSession(Guid session, GuardCoreKind kind, string image, string hash)
    { this.session = session; this.kind = kind; this.image = image; configurationHash = hash; }
    internal Task<uint> Completion => process.Completion;
    internal Task OutputCompletion => process.OutputCompletion;
    internal string OutputTail => process.OutputTail;
    internal Guid Session => session;
    internal GuardCoreKind Kind => kind;
    internal string ConfigurationHash => configurationHash;
    internal static GuardOwnedCoreSession PrepareGenerated(GuardServiceProxyConfiguration configuration, GuardOwnerPolicy owner)
    {
        TrafficGuardServiceIdentity.Demand();
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        configuration.DemandCurrent(owner);
        configuration.UpstreamRoute?.DemandBeforeLaunch();
        configuration.Tunnel?.DemandBeforeLaunch();
        var result = PrepareCore(configuration.Kind, configuration.Session, configuration.TunnelLease);
        try { if (result.ConfigurationHash != configuration.Hash) throw new UnauthorizedAccessException("Generated core configuration mismatch."); return result; }
        catch { result.Dispose(); throw; }
    }
    private static GuardOwnedCoreSession Prepare(GuardCoreKind kind, Guid session)
        => PrepareCore(kind,session,null);
    private static GuardOwnedCoreSession PrepareCore(GuardCoreKind kind, Guid session, MachineTunnelLease tunnelLease)
    {
        // Authorization precedes every filesystem operation and process launch.
        TrafficGuardServiceIdentity.Demand();
        if (!Enum.IsDefined(typeof(GuardCoreKind), kind) || session == Guid.Empty) throw new ArgumentException("Invalid owned core session.");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN");
        string journal = Path.Combine(root, "TrafficGuard"), sessions = Path.Combine(journal, "CoreSessions"), scope = Path.Combine(sessions, session.ToString("N"));
        foreach (string path in new[] { root, journal, sessions, scope }) GuardMachineJournalSecurity.Validate(path, true);
        string configuration = Path.Combine(scope, "config.json"); GuardMachineJournalSecurity.Validate(configuration, false);
        string coreDirectory = Path.Combine(TrafficGuardServiceIdentity.InstallRoot, "core");
        string image = Path.Combine(coreDirectory, kind == GuardCoreKind.SingBox ? "sing-box.exe" : "reality-client.exe");
        TrafficGuardServiceIdentity.ValidateProtected(coreDirectory, false); TrafficGuardServiceIdentity.ValidateProtected(image, false);
        FileStream configLease = null, executableLease = null; GuardOwnedCoreSession result = null;
        try
        {
            configLease = new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (configLease.Length < 2 || configLease.Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid service core configuration size.");
            string hash; using (var sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(configLease)); configLease.Position = 0;
            executableLease = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            result = new GuardOwnedCoreSession(session, kind, image, hash) { configurationLease = configLease, imageLease = executableLease };
            configLease = executableLease = null;
            var engine = kind == GuardCoreKind.SingBox ? VpnEngine.SingBox : VpnEngine.Reality;
            result.process = tunnelLease==null ? GuardOwnedProcess.CreateSuspended(image, VpnEngines.RunArguments(engine, configuration), coreDirectory) : GuardOwnedProcess.CreateSuspendedWithLease(image,VpnEngines.RunArguments(engine,configuration),coreDirectory,tunnelLease);
            result.Inspect(); // Image/job/creation/token verified before user-mode core code runs.
            return result;
        }
        catch { result?.Dispose(); throw; }
        finally { configLease?.Dispose(); executableLease?.Dispose(); }
    }
    internal GuardProcessIdentity Inspect()
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GuardOwnedCoreSession));
            var identity = process.Inspect();
            if (!string.Equals(identity.Image, image, StringComparison.OrdinalIgnoreCase) || identity.UserSid != "S-1-5-18") throw new UnauthorizedAccessException("Owned core image or SYSTEM token changed.");
            return identity;
        }
    }
    internal void Resume()
    {
        TrafficGuardServiceIdentity.Demand();
        lock (sync) { Inspect(); process.Resume(); }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            try { process?.Dispose(); }
            finally { configurationLease?.Dispose(); imageLease?.Dispose(); }
        }
    }
}

// The capability's delegate always returns fresh data from the retained core
// handle. No PID, SID or image supplied by an IPC client is accepted here.
internal sealed partial class GuardCoreEndpointBinding
{
    internal void AttachTunnel(GuardOwnedCoreSession core,GuardServiceProxyConfiguration configuration,GuardOwnerPolicy owner)
    {
        TrafficGuardServiceIdentity.Demand();
        if(core==null||configuration==null||configuration.Tunnel==null)throw new ArgumentException("Generated owned tunnel required.");
        if(Tunnel!=null)throw new InvalidOperationException("Tunnel authority is already attached.");
        Read();configuration.DemandCurrent(owner);
        Action demandCore=()=>
        {
            configuration.DemandCurrent(owner);var actual=core.Inspect();
            if(!actual.Resumed||core.Session!=expected.Session||configuration.Session!=expected.Session||configuration.Tunnel.Session!=expected.Session||core.ConfigurationHash!=expected.ConfigurationHash||configuration.Hash!=expected.ConfigurationHash||actual.ProcessId!=expected.ProcessId||actual.CreationFileTime!=expected.CreationFileTime||actual.Image!=expected.Image||actual.UserSid!=expected.UserSid)throw new UnauthorizedAccessException("Tunnel does not belong to the retained core.");
        };
        demandCore();ulong luid=configuration.Tunnel.ReadLuid();Guid id=configuration.Tunnel.ExpectedInterfaceId;
        var attached=new TunnelIdentity(id,luid,()=>{demandCore();if(configuration.Tunnel.ReadLuid()!=luid)throw new UnauthorizedAccessException("Tunnel LUID changed.");});
        attached.DemandCurrent();
        // Narrow this existing capability in place. Keeping its reference lets
        // journal/admission failure cleanup revoke the pre-attachment grants.
        if(System.Threading.Interlocked.CompareExchange(ref tunnel,attached,null)!=null)throw new InvalidOperationException("Tunnel authority is already attached.");
    }
    internal static GuardCoreEndpointBinding FromOwnedCore(GuardOwnedCoreSession core)
    {
        TrafficGuardServiceIdentity.Demand();
        if (core == null) throw new ArgumentNullException(nameof(core));
        Func<Identity> inspect = () =>
        {
            var actual = core.Inspect();
            return new Identity(core.Session, (byte)core.Kind, actual.Image, actual.UserSid, actual.ProcessId, actual.CreationFileTime, core.ConfigurationHash);
        };
        var initial = inspect();
        return new GuardCoreEndpointBinding(initial, inspect);
    }
}

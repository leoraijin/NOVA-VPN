using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class TrafficGuardEndpoint
{
    public string ApplicationPath;
    public string Address;
    public int Port;
    public string Network = "tcp";
}

// A live capability issued only from the actual service-owned core. The issuer
// lives with the core session; standalone rescue/setup do not need that code.
internal sealed partial class GuardCoreEndpointBinding
{
    internal sealed class Identity
    {
        internal readonly Guid Session;
        internal readonly byte Kind;
        internal readonly string Image, UserSid, ConfigurationHash, Digest;
        internal readonly uint ProcessId;
        internal readonly long CreationFileTime;
        internal Identity(Guid session, byte kind, string image, string sid, uint pid, long created, string configurationHash)
        {
            if (session == Guid.Empty || kind < 1 || kind > 2 || sid != "S-1-5-18" || pid == 0 || created <= 0) throw new FormatException("Invalid owned core identity.");
            byte[] configurationDigest = Convert.FromBase64String(configurationHash);
            if (configurationDigest.Length != 32) throw new FormatException("Invalid owned core configuration digest.");
            Session = session; Kind = kind; Image = TrafficGuardCompiler.LocalExecutable(image); UserSid = sid;
            ProcessId = pid; CreationFileTime = created; ConfigurationHash = Convert.ToBase64String(configurationDigest);
            using (var data = new MemoryStream())
            {
                using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
                { writer.Write(1); writer.Write(Session.ToByteArray()); writer.Write(Kind); writer.Write(Image.ToUpperInvariant()); writer.Write(UserSid); writer.Write(ProcessId); writer.Write(CreationFileTime); writer.Write(configurationDigest); }
                using (var sha = SHA256.Create()) Digest = Convert.ToBase64String(sha.ComputeHash(data.ToArray()));
            }
        }
    }
    readonly Identity expected;
    readonly Func<Identity> inspect;
    internal sealed class TunnelIdentity
    {
        internal readonly Guid InterfaceId;
        internal readonly ulong Luid;
        readonly Action inspect;
        internal TunnelIdentity(Guid id,ulong luid,Action inspect)
        {if(id==Guid.Empty||luid==0||inspect==null)throw new ArgumentException("Invalid attested tunnel.");InterfaceId=id;Luid=luid;this.inspect=inspect;}
        internal void DemandCurrent()=>inspect();
    }
    TunnelIdentity tunnel;
    internal TunnelIdentity Tunnel=>Volatile.Read(ref tunnel);
    private GuardCoreEndpointBinding(Identity expected, Func<Identity> inspect) { this.expected = expected; this.inspect = inspect; }
    internal Identity Read()
    {
        TrafficGuardServiceIdentity.Demand();
        var actual = inspect();
        if (actual == null || actual.Digest != expected.Digest) throw new UnauthorizedAccessException("Owned core session identity changed.");
        Tunnel?.DemandCurrent();
        return expected;
    }
}

public sealed class TrafficGuardPolicy
{
    public string OwnerSid;
    public ulong TunnelLuid;
    public Guid TunnelInterfaceId;
    public List<TrafficGuardEndpoint> Endpoints = new List<TrafficGuardEndpoint>();
    internal GuardCoreEndpointBinding CoreBinding { get; set; }
}

public enum GuardLayer { ConnectV4, ConnectV6, AcceptV4, AcceptV6, BootOutboundV4, BootOutboundV6, ForwardV4, ForwardV6, BootForwardV4, BootForwardV6 }
public enum GuardField { Loopback, Application, RemoteAddress, RemotePort, Protocol, LocalInterface, OwnerUser }

public sealed class GuardCondition
{
    public GuardField Field { get; }
    public string Value { get; }
    internal GuardCondition(GuardField field, string value) { Field = field; Value = value; }
}

public sealed class GuardFilter
{
    public Guid Key { get; }
    public string Role { get; }
    public GuardLayer Layer { get; }
    public bool Permit { get; }
    public uint Flags { get; }
    public ulong Weight { get; }
    public ReadOnlyCollection<GuardCondition> Conditions { get; }
    internal GuardFilter(string role, GuardLayer layer, bool permit, uint flags, ulong weight, params GuardCondition[] conditions)
    { Key = Guid.NewGuid(); Role = role; Layer = layer; Permit = permit; Flags = flags; Weight = weight; Conditions = Array.AsReadOnly(conditions); }
}

public sealed class TrafficGuardPlan
{
    public string OwnerSid { get; }
    public string Fingerprint { get; }
    public ulong TunnelLuid { get; }
    public Guid TunnelInterfaceId { get; }
    public ReadOnlyCollection<GuardFilter> Filters { get; }
    internal GuardCoreEndpointBinding CoreBinding { get; }
    readonly string coreDigest;
    internal bool CoreAuthorityCurrent()
    {
        if (coreDigest == null) return CoreBinding == null;
        try { return CoreBinding != null && CoreBinding.Read().Digest == coreDigest; }
        catch (Exception error) when (error is UnauthorizedAccessException || error is InvalidOperationException || error is ArgumentException || error is System.ComponentModel.Win32Exception) { return false; }
    }
    internal TrafficGuardPlan(string owner, ulong luid, Guid adapter, List<GuardFilter> filters) : this(owner, luid, adapter, filters, null, null) { }
    internal TrafficGuardPlan(string owner, ulong luid, Guid adapter, List<GuardFilter> filters, GuardCoreEndpointBinding binding, string digest)
    {
        OwnerSid = owner; TunnelLuid = luid; TunnelInterfaceId = adapter; Filters = filters.AsReadOnly(); CoreBinding = binding; coreDigest = digest;
        var json = new JavaScriptSerializer().Serialize(new { Schema = 1, TrafficGuardCompiler.ProviderKey, TrafficGuardCompiler.SublayerKey, TrafficGuardCompiler.SublayerWeight, OwnerSid, TunnelLuid, TunnelInterfaceId, Rules = filters.Select(f => new { f.Role, f.Layer, f.Permit, f.Flags, f.Weight, f.Conditions }).ToArray() });
        if (digest == null) { using (var sha = SHA256.Create()) Fingerprint = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(json))); }
        else
        {
            // Fixed binary field order; reflection/JSON property order cannot
            // change the live capability or owned-policy identity.
            using (var data = new MemoryStream())
            {
                using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
                {
                    writer.Write(2); writer.Write(TrafficGuardCompiler.ProviderKey.ToByteArray()); writer.Write(TrafficGuardCompiler.SublayerKey.ToByteArray()); writer.Write(TrafficGuardCompiler.SublayerWeight);
                    writer.Write(OwnerSid); writer.Write(TunnelLuid); writer.Write(TunnelInterfaceId.ToByteArray()); writer.Write(Convert.FromBase64String(digest)); writer.Write(filters.Count);
                    foreach (var filter in filters)
                    {
                        writer.Write(filter.Role); writer.Write((byte)filter.Layer); writer.Write(filter.Permit); writer.Write(filter.Flags); writer.Write(filter.Weight); writer.Write(filter.Conditions.Count);
                        foreach (var condition in filter.Conditions) { writer.Write((byte)condition.Field); writer.Write(condition.Value); }
                    }
                }
                using (var sha = SHA256.Create()) Fingerprint = Convert.ToBase64String(sha.ComputeHash(data.ToArray()));
            }
        }
    }
}

public static class TrafficGuardCompiler
{
    public const string ServiceName = "NovaVpnTrafficGuard";
    public const ushort SublayerWeight = ushort.MaxValue;
    public static readonly Guid ProviderKey = new Guid("c56db942-a5d1-49b7-b8d4-7d9e8b4ed0e1");
    public static readonly Guid SublayerKey = new Guid("7a49cb91-7ac3-4e82-a29f-349034b21966");

    public static string LocalExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\' || !char.IsLetter(path[0]) || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl))
            throw new FormatException("Допускается только абсолютный путь к локальному EXE.");
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Требуется EXE-файл.");
        return full;
    }

    public static TrafficGuardPlan Compile(TrafficGuardPolicy policy)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        return CompileCore(policy, policy.CoreBinding?.Read(), policy.CoreBinding);
    }
    private static TrafficGuardPlan CompileCore(TrafficGuardPolicy policy, GuardCoreEndpointBinding.Identity core, GuardCoreEndpointBinding binding)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        if (core != null && (policy.TunnelLuid != 0 || policy.TunnelInterfaceId != Guid.Empty)) throw new FormatException("Owned TUN requires a separate attested dynamic grant.");
        var tunnel=binding?.Tunnel;
        if(tunnel!=null&&core==null)throw new UnauthorizedAccessException("Live owned core required for TUN.");
        ulong tunnelLuid=tunnel?.Luid??policy.TunnelLuid;Guid tunnelId=tunnel?.InterfaceId??policy.TunnelInterfaceId;
        string owner;
        try { owner = new SecurityIdentifier(policy.OwnerSid).Value; } catch (Exception ex) when (ex is ArgumentException || ex is FormatException) { throw new FormatException("Некорректный SID владельца."); }
        if (policy.TunnelLuid != 0 && policy.TunnelInterfaceId == Guid.Empty || policy.TunnelLuid == 0 && policy.TunnelInterfaceId != Guid.Empty)
            throw new FormatException("LUID и GUID туннеля должны передаваться вместе.");
        if (policy.Endpoints == null || policy.Endpoints.Count > 256) throw new FormatException("Допускается до 256 точных адресов VPN-серверов.");
        var endpoints = new List<TrafficGuardEndpoint>();
        foreach (var endpoint in policy.Endpoints)
        {
            if (endpoint == null || !IPAddress.TryParse(endpoint.Address, out var ip)) throw new FormatException("Нужен точный IP-адрес сервера, без доменного имени или подсети.");
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) throw new FormatException("Для scoped IPv6 требуется отдельное ограничение интерфейса.");
            byte[] bytes = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) || ip.IsIPv6Multicast || bytes.Length == 4 && bytes[0] >= 224)
                throw new FormatException("Не допускается неопределённый, широковещательный или multicast-адрес сервера.");
            if (endpoint.Port < 1 || endpoint.Port > 65535 || endpoint.Network != "tcp" && endpoint.Network != "udp") throw new FormatException("Укажите TCP/UDP и один порт от 1 до 65535.");
            if (core != null && !string.Equals(LocalExecutable(endpoint.ApplicationPath), core.Image, StringComparison.OrdinalIgnoreCase)) throw new FormatException("Endpoint does not belong to the attested core image.");
            endpoints.Add(new TrafficGuardEndpoint { ApplicationPath = core == null ? LocalExecutable(endpoint.ApplicationPath) : core.Image, Address = ip.ToString(), Port = endpoint.Port, Network = endpoint.Network });
        }
        endpoints = endpoints.GroupBy(e => e.ApplicationPath.ToLowerInvariant() + "|" + e.Address + "|" + e.Port + "|" + e.Network)
            .Select(g => g.First()).OrderBy(e => e.ApplicationPath, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Address, StringComparer.Ordinal).ThenBy(e => e.Port).ThenBy(e => e.Network).ToList();
        var filters = new List<GuardFilter>();
        foreach (GuardLayer layer in Enum.GetValues(typeof(GuardLayer)))
        {
            bool forward = layer == GuardLayer.ForwardV4 || layer == GuardLayer.ForwardV6 || layer == GuardLayer.BootForwardV4 || layer == GuardLayer.BootForwardV6;
            bool boot = layer == GuardLayer.BootOutboundV4 || layer == GuardLayer.BootOutboundV6 || layer == GuardLayer.BootForwardV4 || layer == GuardLayer.BootForwardV6;
            uint flags = boot ? 2u : 1u; // BOOTTIME and PERSISTENT are mutually exclusive.
            filters.Add(new GuardFilter("default-deny", layer, false, flags, 1));
            if (!forward) filters.Add(new GuardFilter("loopback", layer, true, flags, 10000, new GuardCondition(GuardField.Loopback, "1")));
            // Forwarding stays denied until managed gateway/virtual-network rules
            // have been explicitly compiled and verified; it must not bypass the guard.
            if (boot || forward) continue;
            if (tunnelLuid != 0)
                filters.Add(new GuardFilter("verified-tunnel", layer, true, 0, 10000,
                    new GuardCondition(GuardField.LocalInterface, tunnelLuid.ToString(CultureInfo.InvariantCulture))));
            bool ipv6 = layer == GuardLayer.ConnectV6 || layer == GuardLayer.AcceptV6;
            foreach (var endpoint in endpoints.Where(e => IPAddress.Parse(e.Address).AddressFamily == (ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)))
                filters.Add(new GuardFilter("core-endpoint", layer, true, core == null ? 1u : 0u, 10000,
                    new GuardCondition(GuardField.Application, endpoint.ApplicationPath), new GuardCondition(GuardField.OwnerUser, core == null ? owner : core.UserSid), new GuardCondition(GuardField.RemoteAddress, endpoint.Address),
                    new GuardCondition(GuardField.RemotePort, endpoint.Port.ToString(CultureInfo.InvariantCulture)), new GuardCondition(GuardField.Protocol, endpoint.Network == "tcp" ? "6" : "17")));
        }
        return new TrafficGuardPlan(owner, tunnelLuid, tunnelId, filters, binding, core?.Digest);
    }
}

// A lifetime cleanup capability may install only the exact compiler-produced
// owner-bound deny/loopback policy. It cannot add application/interface permits.
internal static class GuardClosedPolicy
{
    internal static void Demand(TrafficGuardPlan plan, string owner)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (plan.OwnerSid != owner || plan.CoreBinding != null || !plan.CoreAuthorityCurrent() || plan.TunnelLuid != 0 || plan.TunnelInterfaceId != Guid.Empty)
            throw new UnauthorizedAccessException("Cleanup requires the original owner's closed policy.");
        var expected = TrafficGuardCompiler.Compile(new TrafficGuardPolicy { OwnerSid = owner });
        if (plan.Filters.Count != expected.Filters.Count) throw new UnauthorizedAccessException("Closed policy rule count mismatch.");
        for (int i = 0; i < expected.Filters.Count; i++)
        {
            var actual = plan.Filters[i]; var rule = expected.Filters[i];
            if (actual.Role != rule.Role || actual.Layer != rule.Layer || actual.Permit != rule.Permit || actual.Flags != rule.Flags || actual.Weight != rule.Weight || actual.Conditions.Count != rule.Conditions.Count)
                throw new UnauthorizedAccessException("Closed policy rule mismatch.");
            for (int j = 0; j < rule.Conditions.Count; j++)
                if (actual.Conditions[j].Field != rule.Conditions[j].Field || actual.Conditions[j].Value != rule.Conditions[j].Value) throw new UnauthorizedAccessException("Closed policy condition mismatch.");
        }
    }
}

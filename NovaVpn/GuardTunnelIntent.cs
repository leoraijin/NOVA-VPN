using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

namespace NovaVpn;

// Service-selected staging identity, not an attested interface grant. The core
// must create it before GUID/LUID/live-core attestation can authorize routing.
internal sealed class GuardTunnelIntent
{
    readonly string[] addresses;
    internal Guid Session { get; }
    internal string InterfaceName { get; }
    internal Guid ExpectedInterfaceId { get; }
    private GuardTunnelIntent(Guid session, string[] addresses)
    { Session=session; InterfaceName=NameFor(session); ExpectedInterfaceId=IdFor(session); this.addresses=(string[])addresses.Clone(); }
    internal static string NameFor(Guid session)
    { if(session==Guid.Empty)throw new ArgumentException("Empty tunnel session.");return "NOVA-"+session.ToString("N"); }
    internal static Guid IdFor(Guid session)
    {
        // Compatibility mapping used by pinned sing-tun0.8.11, not an
        // authentication hash. Authority comes from the service/core lifetime.
        using(var hash=MD5.Create())return new Guid(hash.ComputeHash(Encoding.UTF8.GetBytes("wintun"+NameFor(session))));
    }
    internal static GuardTunnelIntent Capture(Guid session,string[] addresses)
    {
        TrafficGuardServiceIdentity.Demand();
        if(addresses==null||addresses.Length!=2||!IPAddress.TryParse(addresses[0],out var v4)||v4.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||!IPAddress.TryParse(addresses[1],out var v6)||v6.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetworkV6)throw new ArgumentException("Managed dual-stack tunnel addresses required.");
        var result=new GuardTunnelIntent(session,addresses);result.DemandBeforeLaunch();return result;
    }
    internal void DemandBeforeLaunch()
    {
        TrafficGuardServiceIdentity.Demand();
        if(NetworkInterface.GetAllNetworkInterfaces().Any(n=>string.Equals(n.Name,InterfaceName,StringComparison.OrdinalIgnoreCase)||Guid.TryParse(n.Id,out var id)&&id==ExpectedInterfaceId))throw new InvalidOperationException("Refuse to adopt an existing tunnel interface.");
        var config=new {inbounds=new[]{new {type="tun",address=new[]{addresses[0]+"/30",addresses[1]+"/126"}}}};
        TunnelNetworkPreflight.Validate(new JavaScriptSerializer().Serialize(config),TunnelNetworkPreflight.Capture());
    }
    internal ulong ReadLuid()
    {
        TrafficGuardServiceIdentity.Demand();
        Guid id=ExpectedInterfaceId;
        if(ConvertInterfaceGuidToLuid(ref id,out ulong luid)!=0||luid==0||ConvertInterfaceLuidToGuid(ref luid,out Guid back)!=0||back!=id)throw new UnauthorizedAccessException("Tunnel GUID/LUID no longer matches.");
        var matches=NetworkInterface.GetAllNetworkInterfaces().Where(n=>Guid.TryParse(n.Id,out var actual)&&actual==id).ToArray();
        if(matches.Length!=1||matches[0].Name!=InterfaceName||(int)matches[0].NetworkInterfaceType!=53||matches[0].OperationalStatus!=OperationalStatus.Up)throw new UnauthorizedAccessException("Expected live tunnel interface missing.");
        var actualAddresses=matches[0].GetIPProperties().UnicastAddresses.ToArray();
        foreach(string address in addresses)
        {
            var expected=IPAddress.Parse(address);int bits=expected.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork?30:126;
            if(actualAddresses.Count(a=>a.Address.Equals(expected)&&a.PrefixLength==bits)!=1)throw new UnauthorizedAccessException("Managed tunnel address changed.");
        }
        if(actualAddresses.Any(a=>!a.Address.IsIPv6LinkLocal&&!addresses.Any(expected=>a.Address.Equals(IPAddress.Parse(expected)))))throw new UnauthorizedAccessException("Unexpected tunnel address.");
        if(ConvertInterfaceGuidToLuid(ref id,out ulong finalLuid)!=0||finalLuid!=luid)throw new UnauthorizedAccessException("Tunnel changed during inspection.");
        return luid;
    }
    [DllImport("iphlpapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint ConvertInterfaceGuidToLuid(ref Guid id,out ulong luid);
    [DllImport("iphlpapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint ConvertInterfaceLuidToGuid(ref ulong luid,out Guid id);
}

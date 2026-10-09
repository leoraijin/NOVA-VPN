using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class RoutingSettings
{
	[DataMember]
	public string Mode = "Smart";

	[DataMember]
	public string DefaultRoute = "Proxy";

	[DataMember]
	public bool BypassLan = true;

	[DataMember]
	public bool BypassPrivateDns = true;

	[DataMember]
	public bool BlockAds;

	[DataMember]
	public bool EnableIpv6;

	[DataMember]
	public string DnsMode = "Secure";

	[DataMember]
	public string DnsProvider = "Cloudflare";

	[DataMember]
	public string CustomDnsEndpoint = "";

	[DataMember]
	public string DnsStrategy = "PreferIPv4";

	[DataMember]
	public bool StrictRoute = true;

	[DataMember]
	public bool EnableSniffing = true;

	[DataMember]
	public List<string> DirectDomains = new List<string>();

	// Narrow, temporary direct exceptions for domains that must reach the local
	// Zapret filter even when their application is otherwise routed through VPN.
	[DataMember]
	public List<string> ZapretDirectDomains = new List<string>();

	[DataMember]
	public List<string> ProxyDomains = new List<string>();

	[DataMember]
	public List<string> BlockDomains = new List<string>();

	[DataMember]
	public List<string> DirectProcesses = new List<string>();

	[DataMember]
	public List<string> ProxyProcesses = new List<string>();

	[DataMember]
	public List<string> DirectProcessPaths = new List<string>();

	[DataMember]
	public List<string> ProxyProcessPaths = new List<string>();

	[DataMember]
	public List<string> AdvancedRules = new List<string>();

}

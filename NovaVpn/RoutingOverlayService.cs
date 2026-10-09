using System;
using System.Collections.Generic;

namespace NovaVpn;

/// <summary>
/// Creates a routing snapshot for the core. Zapret changes are already
/// reflected in persisted route lists by ZapretRoutingTransitionService.
/// </summary>
public static class RoutingOverlayService
{
	public static RoutingSettings BuildEffective(AppState state, bool zapretRunning)
	{
		return Clone(state == null ? null : state.Routing);
	}

	public static bool IsZapretMode(string mode)
	{
		return string.Equals(mode, "VPN+Zapret", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(mode, "Zapret", StringComparison.OrdinalIgnoreCase);
	}

	public static RoutingSettings Clone(RoutingSettings source)
	{
		if (source == null) return new RoutingSettings();
		return new RoutingSettings
		{
			Mode = source.Mode,
			DefaultRoute = source.DefaultRoute,
			BypassLan = source.BypassLan,
			BypassPrivateDns = source.BypassPrivateDns,
			BlockAds = source.BlockAds,
			EnableIpv6 = source.EnableIpv6,
			DnsMode = source.DnsMode,
			DnsProvider = source.DnsProvider,
			CustomDnsEndpoint = source.CustomDnsEndpoint,
			DnsStrategy = source.DnsStrategy,
			StrictRoute = source.StrictRoute,
			EnableSniffing = source.EnableSniffing,
			DirectDomains = Copy(source.DirectDomains),
			ZapretDirectDomains = Copy(source.ZapretDirectDomains),
			ProxyDomains = Copy(source.ProxyDomains),
			BlockDomains = Copy(source.BlockDomains),
			DirectProcesses = Copy(source.DirectProcesses),
			ProxyProcesses = Copy(source.ProxyProcesses),
			DirectProcessPaths = Copy(source.DirectProcessPaths),
			ProxyProcessPaths = Copy(source.ProxyProcessPaths),
			AdvancedRules = Copy(source.AdvancedRules)
		};
	}

	private static List<string> Copy(List<string> source)
	{
		return source == null ? new List<string>() : new List<string>(source);
	}
}

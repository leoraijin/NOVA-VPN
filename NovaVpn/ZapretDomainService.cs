using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaVpn;

public static class ZapretDomainService
{
	public static List<string> ReadStandardDomains(string root)
	{
		HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(root)) return result.ToList();
		foreach (string file in Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories))
		{
			string name = Path.GetFileName(file).ToLowerInvariant();
			if ((!name.Contains("list") && !name.Contains("hostlist") && !name.Contains("domains")) || name.Contains("exclude") || name.Contains("user") || name.Contains("ipset")) continue;
			try
			{
				foreach (string domain in RoutingListImporter.Parse(File.ReadAllLines(file))) result.Add(domain);
			}
			catch (IOException) { }
		}
		return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public static int AddToVpnExceptions(AppState state)
	{
		Ensure(state);
		return AddToVpnRoutesFromSource(state, state.Zapret.StandardDomains);
	}

	public static int AddToVpnExceptionsFromSource(AppState state, IEnumerable<string> zapretDomains)
	{
		return AddToVpnRoutesFromSource(state, zapretDomains);
	}

	public static int AddToVpnRoutesFromSource(AppState state, IEnumerable<string> zapretDomains)
	{
		Ensure(state);
		HashSet<string> existing = new HashSet<string>(state.Routing.ProxyDomains, StringComparer.OrdinalIgnoreCase);
		int added = 0;
		foreach (string domain in NormalizeSource(zapretDomains))
		{
			if (existing.Add(domain)) { state.Routing.ProxyDomains.Add(domain); added++; }
			// Direct rules have priority in the routing inspector, so remove a
			// conflicting direct entry to guarantee that Zapret domains use VPN.
			state.Routing.DirectDomains.RemoveAll(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase));
		}
		return added;
	}

	public static int RemoveFromVpnExceptions(AppState state)
	{
		Ensure(state);
		return RemoveFromVpnRoutesFromSource(state, state.Zapret.StandardDomains);
	}

	public static int RemoveFromVpnExceptionsFromSource(AppState state, IEnumerable<string> zapretDomains)
	{
		return RemoveFromVpnRoutesFromSource(state, zapretDomains);
	}

	public static int RemoveFromVpnRoutesFromSource(AppState state, IEnumerable<string> zapretDomains)
	{
		Ensure(state);
		HashSet<string> zapret = new HashSet<string>(NormalizeSource(zapretDomains), StringComparer.OrdinalIgnoreCase);
		int before = state.Routing.ProxyDomains.Count;
		state.Routing.ProxyDomains.RemoveAll(item => zapret.Contains(item));
		return before - state.Routing.ProxyDomains.Count;
	}

	private static IEnumerable<string> NormalizeSource(IEnumerable<string> domains)
	{
		return (domains ?? Enumerable.Empty<string>())
			.Select(RoutingInspector.NormalizeDomainRule)
			.Where(domain => !string.IsNullOrWhiteSpace(domain))
			.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static void Ensure(AppState state)
	{
		if (state.Routing == null) state.Routing = new RoutingSettings();
		if (state.Routing.DirectDomains == null) state.Routing.DirectDomains = new List<string>();
		if (state.Routing.ProxyDomains == null) state.Routing.ProxyDomains = new List<string>();
		if (state.Zapret == null) state.Zapret = new ZapretSettings();
		if (state.Zapret.StandardDomains == null) state.Zapret.StandardDomains = new List<string>();
		RoutingInspector.NormalizeDomains(state.Routing.DirectDomains);
		RoutingInspector.NormalizeDomains(state.Routing.ProxyDomains);
	}
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaVpn;

/// <summary>
/// Moves active VPN routes to direct routing while Zapret is enabled, then
/// restores the original VPN entries when Zapret is disabled.
/// </summary>
public static class ZapretRoutingTransitionService
{
	private static readonly string[] TargetDomainRoots =
	{
		"discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media",
		"youtube.com", "youtube-nocookie.com", "youtu.be", "ytimg.com", "googlevideo.com",
		"youtube.googleapis.com", "youtubei.googleapis.com"
	};

	private static readonly string[] TargetDiscordProcesses = { "discord.exe", "discordptb.exe", "discordcanary.exe" };

	public static int SetActive(AppState state, bool active)
	{
		if (state == null) throw new ArgumentNullException(nameof(state));
		Ensure(state);
		return active ? Activate(state) : Restore(state);
	}

	private static int Activate(AppState state)
	{
		ZapretSettings zapret = state.Zapret;
		RoutingSettings routing = state.Routing;
		int changed = 0;
		if (!zapret.RoutingChangesApplied)
		{
			zapret.SavedProxyDomains.Clear();
			zapret.AddedDirectDomains.Clear();
			zapret.SavedProxyProcesses.Clear();
			zapret.AddedDirectProcesses.Clear();
			zapret.SavedProxyProcessPaths.Clear();
			zapret.AddedDirectProcessPaths.Clear();
			zapret.RoutingChangesApplied = true;
		}

		// Reconcile snapshots written by earlier versions, which moved every
		// Zapret host and every VPN-routed application to Direct.
		changed += NarrowLegacyTransition(state);
		changed += ApplyZapretDomains(state);
		changed += MoveTargetProcessesToDirect(routing.ProxyProcesses, routing.DirectProcesses,
			zapret.SavedProxyProcesses, zapret.AddedDirectProcesses);
		changed += MoveTargetPathsToDirect(routing.ProxyProcessPaths, routing.DirectProcessPaths,
			zapret.SavedProxyProcessPaths, zapret.AddedDirectProcessPaths);
		return changed;
	}

	private static int ApplyZapretDomains(AppState state)
	{
		ZapretSettings zapret = state.Zapret;
		RoutingSettings routing = state.Routing;
		int changed = 0;
		foreach (string source in GetTargetDomains(zapret.StandardDomains))
		{
			string directRule = ToDirectRule(source);
			if (!ContainsDomain(routing.ZapretDirectDomains, directRule))
				routing.ZapretDirectDomains.Add(directRule);
			if (!ContainsDomain(routing.DirectDomains, directRule))
			{
				routing.DirectDomains.Add(directRule);
				AddUniqueDomain(zapret.AddedDirectDomains, directRule);
				changed++;
			}

			string[] suppressed = routing.ProxyDomains.Where(proxyRule => IsCoveredByZapretRule(directRule, proxyRule)).ToArray();
			foreach (string proxyRule in suppressed)
			{
				routing.ProxyDomains.Remove(proxyRule);
				AddUniqueDomain(zapret.SavedProxyDomains, proxyRule);
				changed++;
			}
		}
		return changed;
	}

	private static int NarrowLegacyTransition(AppState state)
	{
		ZapretSettings zapret = state.Zapret;
		RoutingSettings routing = state.Routing;
		HashSet<string> targetedDomains = new HashSet<string>(GetTargetDomains(zapret.StandardDomains).Select(ToDirectRule), StringComparer.OrdinalIgnoreCase);
		int changed = 0;

		foreach (string domain in zapret.AddedDirectDomains.ToArray())
		{
			if (targetedDomains.Contains(domain) || IsTargetDomain(domain)) continue;
			changed += routing.DirectDomains.RemoveAll(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase));
			zapret.AddedDirectDomains.RemoveAll(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase));
		}
		foreach (string domain in zapret.SavedProxyDomains.ToArray())
		{
			if (IsCoveredByAnyTarget(targetedDomains, domain)) continue;
			if (!ContainsDomain(routing.ProxyDomains, domain)) { routing.ProxyDomains.Add(domain); changed++; }
			zapret.SavedProxyDomains.RemoveAll(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase));
		}

		changed += ReconcileProcessSnapshot(routing.ProxyProcesses, routing.DirectProcesses,
			zapret.SavedProxyProcesses, zapret.AddedDirectProcesses, IsDiscordProcess);
		changed += ReconcilePathSnapshot(routing.ProxyProcessPaths, routing.DirectProcessPaths,
			zapret.SavedProxyProcessPaths, zapret.AddedDirectProcessPaths, IsDiscordPath);
		if (routing.ZapretDirectDomains == null) routing.ZapretDirectDomains = new List<string>();
		foreach (string existing in routing.ZapretDirectDomains.ToArray())
		{
			if (targetedDomains.Contains(existing) || IsTargetDomain(existing)) continue;
			routing.ZapretDirectDomains.RemoveAll(item => string.Equals(item, existing, StringComparison.OrdinalIgnoreCase));
			changed++;
		}
		return changed;
	}

	private static int ReconcileProcessSnapshot(List<string> proxy, List<string> direct, List<string> savedProxy, List<string> addedDirect, Func<string, bool> isTarget)
	{
		int changed = 0;
		foreach (string process in addedDirect.ToArray())
		{
			if (isTarget(process)) continue;
			changed += direct.RemoveAll(item => string.Equals(item, process, StringComparison.OrdinalIgnoreCase));
			addedDirect.RemoveAll(item => string.Equals(item, process, StringComparison.OrdinalIgnoreCase));
		}
		foreach (string process in savedProxy.ToArray())
		{
			if (isTarget(process)) continue;
			if (!Contains(proxy, process)) { proxy.Add(process); changed++; }
			savedProxy.RemoveAll(item => string.Equals(item, process, StringComparison.OrdinalIgnoreCase));
		}
		return changed;
	}

	private static int ReconcilePathSnapshot(List<string> proxy, List<string> direct, List<string> savedProxy, List<string> addedDirect, Func<string, bool> isTarget)
	{
		int changed = 0;
		foreach (string path in addedDirect.ToArray())
		{
			if (isTarget(path)) continue;
			changed += direct.RemoveAll(item => ApplicationRoutingService.SamePath(item, path));
			addedDirect.RemoveAll(item => ApplicationRoutingService.SamePath(item, path));
		}
		foreach (string path in savedProxy.ToArray())
		{
			if (isTarget(path)) continue;
			if (!ContainsPath(proxy, path)) { proxy.Add(path); changed++; }
			savedProxy.RemoveAll(item => ApplicationRoutingService.SamePath(item, path));
		}
		return changed;
	}

	private static int MoveTargetProcessesToDirect(List<string> proxy, List<string> direct, List<string> savedProxy, List<string> addedDirect)
	{
		int changed = 0;
		foreach (string process in proxy.ToArray())
		{
			if (!IsDiscordProcess(process)) continue;
			if (!Contains(savedProxy, process)) savedProxy.Add(process);
			if (!Contains(direct, process))
			{
				direct.Add(process);
				if (!Contains(addedDirect, process)) addedDirect.Add(process);
			}
			proxy.RemoveAll(item => string.Equals(item, process, StringComparison.OrdinalIgnoreCase));
			changed++;
		}
		foreach (string process in TargetDiscordProcesses)
		{
			if (Contains(direct, process)) continue;
			direct.Add(process);
			if (!Contains(addedDirect, process)) addedDirect.Add(process);
			changed++;
		}
		return changed;
	}

	private static int MoveTargetPathsToDirect(List<string> proxy, List<string> direct, List<string> savedProxy, List<string> addedDirect)
	{
		int changed = 0;
		foreach (string path in proxy.ToArray())
		{
			if (!IsDiscordPath(path)) continue;
			if (!ContainsPath(savedProxy, path)) savedProxy.Add(path);
			if (!ContainsPath(direct, path))
			{
				direct.Add(path);
				if (!ContainsPath(addedDirect, path)) addedDirect.Add(path);
			}
			proxy.RemoveAll(item => ApplicationRoutingService.SamePath(item, path));
			changed++;
		}
		return changed;
	}

	private static IEnumerable<string> GetTargetDomains(IEnumerable<string> domains)
	{
		return TargetDomainRoots.Concat(Normalize(domains).Where(IsTargetDomain))
			.Select(RoutingInspector.NormalizeDomainRule)
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsTargetDomain(string domain)
	{
		string value = RoutingInspector.NormalizeDomainRule(domain).TrimStart('*').TrimStart('.');
		if (string.IsNullOrWhiteSpace(value)) return false;
		return TargetDomainRoots.Any(root => value.Equals(root, StringComparison.OrdinalIgnoreCase)
			|| value.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsCoveredByAnyTarget(IEnumerable<string> rules, string candidate) =>
		rules.Any(rule => IsCoveredByZapretRule(rule, candidate));

	private static bool IsDiscordProcess(string process)
	{
		string name = System.IO.Path.GetFileNameWithoutExtension((process ?? "").Trim());
		return name.Equals("discord", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("discordptb", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("discordcanary", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsDiscordPath(string path)
	{
		string normalized = (path ?? "").Replace('/', '\\');
		string name = System.IO.Path.GetFileNameWithoutExtension(normalized);
		return IsDiscordProcess(name) || normalized.IndexOf("\\Discord\\", StringComparison.OrdinalIgnoreCase) >= 0
			|| normalized.IndexOf("\\DiscordPTB\\", StringComparison.OrdinalIgnoreCase) >= 0
			|| normalized.IndexOf("\\DiscordCanary\\", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static int Restore(AppState state)
	{
		ZapretSettings zapret = state.Zapret;
		RoutingSettings routing = state.Routing;
		if (!zapret.RoutingChangesApplied) return 0;
		int changed = 0;

		foreach (string domain in zapret.AddedDirectDomains.ToArray())
			changed += routing.DirectDomains.RemoveAll(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase));
		routing.ZapretDirectDomains.Clear();
		foreach (string process in zapret.AddedDirectProcesses.ToArray())
			changed += routing.DirectProcesses.RemoveAll(item => string.Equals(item, process, StringComparison.OrdinalIgnoreCase));
		foreach (string path in zapret.AddedDirectProcessPaths.ToArray())
			changed += routing.DirectProcessPaths.RemoveAll(item => ApplicationRoutingService.SamePath(item, path));

		foreach (string domain in zapret.SavedProxyDomains)
		{
			if (!ContainsDomain(routing.ProxyDomains, domain)) { routing.ProxyDomains.Add(domain); changed++; }
		}
		foreach (string process in zapret.SavedProxyProcesses)
		{
			if (!Contains(routing.ProxyProcesses, process)) { routing.ProxyProcesses.Add(process); changed++; }
		}
		foreach (string path in zapret.SavedProxyProcessPaths)
		{
			if (!ContainsPath(routing.ProxyProcessPaths, path)) { routing.ProxyProcessPaths.Add(path); changed++; }
		}

		zapret.RoutingChangesApplied = false;
		zapret.SavedProxyDomains.Clear();
		zapret.AddedDirectDomains.Clear();
		zapret.SavedProxyProcesses.Clear();
		zapret.AddedDirectProcesses.Clear();
		zapret.SavedProxyProcessPaths.Clear();
		zapret.AddedDirectProcessPaths.Clear();
		return changed;
	}

	private static IEnumerable<string> Normalize(IEnumerable<string> domains)
	{
		return (domains ?? Enumerable.Empty<string>())
			.Select(RoutingInspector.NormalizeDomainRule)
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static string ToDirectRule(string domain)
	{
		if (domain.StartsWith("*.", StringComparison.Ordinal) || domain.StartsWith(".", StringComparison.Ordinal)
			|| System.Net.IPAddress.TryParse(domain, out _)) return domain;
		return "*." + domain;
	}

	private static bool IsCoveredByZapretRule(string zapretRule, string proxyRule)
	{
		string zapret = RoutingInspector.NormalizeDomainRule(zapretRule);
		string proxy = RoutingInspector.NormalizeDomainRule(proxyRule);
		if (string.IsNullOrWhiteSpace(zapret) || string.IsNullOrWhiteSpace(proxy)) return false;
		if (!zapret.StartsWith("*.", StringComparison.Ordinal) && !zapret.StartsWith(".", StringComparison.Ordinal))
			return string.Equals(zapret, proxy, StringComparison.OrdinalIgnoreCase);
		string suffix = zapret.TrimStart('*').TrimStart('.');
		string proxyBase = proxy.TrimStart('*').TrimStart('.');
		return proxyBase.Equals(suffix, StringComparison.OrdinalIgnoreCase)
			|| proxyBase.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
	}

	private static bool ContainsDomain(IEnumerable<string> values, string value) =>
		values != null && values.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

	private static void AddUniqueDomain(List<string> values, string value)
	{
		if (!ContainsDomain(values, value)) values.Add(value);
	}

	private static bool Contains(IEnumerable<string> values, string value) =>
		values != null && values.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

	private static bool ContainsPath(IEnumerable<string> values, string path) =>
		values != null && values.Any(item => ApplicationRoutingService.SamePath(item, path));

	private static void Ensure(AppState state)
	{
		if (state.Routing == null) state.Routing = new RoutingSettings();
		if (state.Zapret == null) state.Zapret = new ZapretSettings();
		if (state.Routing.DirectDomains == null) state.Routing.DirectDomains = new List<string>();
		if (state.Routing.ZapretDirectDomains == null) state.Routing.ZapretDirectDomains = new List<string>();
		if (state.Routing.ProxyDomains == null) state.Routing.ProxyDomains = new List<string>();
		if (state.Routing.DirectProcesses == null) state.Routing.DirectProcesses = new List<string>();
		if (state.Routing.ProxyProcesses == null) state.Routing.ProxyProcesses = new List<string>();
		if (state.Routing.DirectProcessPaths == null) state.Routing.DirectProcessPaths = new List<string>();
		if (state.Routing.ProxyProcessPaths == null) state.Routing.ProxyProcessPaths = new List<string>();
		if (state.Zapret.StandardDomains == null) state.Zapret.StandardDomains = new List<string>();
		if (state.Zapret.SavedProxyDomains == null) state.Zapret.SavedProxyDomains = new List<string>();
		if (state.Zapret.AddedDirectDomains == null) state.Zapret.AddedDirectDomains = new List<string>();
		if (state.Zapret.SavedProxyProcesses == null) state.Zapret.SavedProxyProcesses = new List<string>();
		if (state.Zapret.AddedDirectProcesses == null) state.Zapret.AddedDirectProcesses = new List<string>();
		if (state.Zapret.SavedProxyProcessPaths == null) state.Zapret.SavedProxyProcessPaths = new List<string>();
		if (state.Zapret.AddedDirectProcessPaths == null) state.Zapret.AddedDirectProcessPaths = new List<string>();
		RoutingInspector.NormalizeDomains(state.Routing.DirectDomains);
		RoutingInspector.NormalizeDomains(state.Routing.ZapretDirectDomains);
		RoutingInspector.NormalizeDomains(state.Routing.ProxyDomains);
	}
}

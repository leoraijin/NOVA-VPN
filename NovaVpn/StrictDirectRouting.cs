using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace NovaVpn;

// A dedicated physical-network exit: never reuse an imported selector/detour.
public static class StrictDirectRouting
{
	internal const string ExitTag = "nova-strict-direct";
	private const string DnsTag = "nova-strict-direct-dns";

	public static bool Matches(IEnumerable<string> domains, string host, out string matched)
	{
		matched = Roots(domains).FirstOrDefault(root => host.Equals(root, StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));
		return matched != null;
	}

	public static string Apply(string json, RoutingSettings routing)
	{
		string[] roots = Roots(routing.DirectDomains).ToArray();
		if (roots.Length == 0) return json;
		var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
		var config = serializer.DeserializeObject(json) as Dictionary<string, object>;
		if (config == null) throw new InvalidOperationException("Некорректная конфигурация строгого прямого маршрута.");
		if (config.TryGetValue("endpoints", out object endpoints) && endpoints is object[] entries
			&& entries.Any(item => item is Dictionary<string, object> endpoint && Tag(endpoint) == ExitTag))
			throw new InvalidOperationException("Тег nova-strict-direct уже занят в импортированном JSON.");
		var outbounds = Array(config, "outbounds");
		outbounds.RemoveAll(item => item is Dictionary<string, object> existing && Tag(existing) == ExitTag);
		outbounds.Add(Obj("type", "direct", "tag", ExitTag, "domain_resolver", DnsTag));
		config["outbounds"] = outbounds.ToArray();
		var dns = Object(config, "dns");
		var servers = Array(dns, "servers");
		servers.RemoveAll(item => item is Dictionary<string, object> existing && Tag(existing) == DnsTag);
		servers.Add(Obj("type", "local", "tag", DnsTag, "detour", ExitTag));
		dns["servers"] = servers.ToArray();
		dns["reverse_mapping"] = true;
		dns["independent_cache"] = true;
		var dnsRules = Array(dns, "rules");
		dnsRules.RemoveAll(item => item is Dictionary<string, object> rule && rule.TryGetValue("server", out object server) && Equals(server, DnsTag));
		string[] names = roots.Where(root => !IPAddress.TryParse(root, out _)).ToArray();
		if (names.Length > 0) dnsRules.Insert(0, Obj("domain", names, "domain_suffix", names.Select(root => "." + root).ToArray(), "action", "route", "server", DnsTag));
		dns["rules"] = dnsRules.ToArray();
		var route = Object(config, "route");
		route["auto_detect_interface"] = true;
		var rules = Array(route, "rules");
		rules.RemoveAll(item => item is Dictionary<string, object> rule && rule.TryGetValue("outbound", out object outbound) && Equals(outbound, ExitTag));
		// Sniff and DNS interception must run before any imported browser routes.
		rules.Insert(0, Obj("protocol", new[] { "dns" }, "action", "hijack-dns"));
		rules.Insert(0, Obj("action", "sniff", "timeout", "300ms"));
		var directRules = new List<object>();
		if (names.Length > 0) directRules.Add(Obj("domain", names, "domain_suffix", names.Select(root => "." + root).ToArray(), "action", "route", "outbound", ExitTag));
		string[] ips = roots.Where(root => IPAddress.TryParse(root, out _)).Select(root => root + (root.Contains(":") ? "/128" : "/32")).ToArray();
		if (ips.Length > 0) directRules.Add(Obj("ip_cidr", ips, "action", "route", "outbound", ExitTag));
		// Retain explicit block/reject rules ahead of direct exceptions.
		int prefix = 2;
		while (prefix < rules.Count && rules[prefix] is Dictionary<string, object> rule
			&& rule.TryGetValue("action", out object action) && new[] { "sniff", "hijack-dns", "reject" }.Contains(action as string)) prefix++;
		rules.InsertRange(prefix, directRules);
		route["rules"] = rules.ToArray();
		return serializer.Serialize(config);
	}

	internal const string RussianDomainSuffix = ".ru";

	internal static bool IsRussianDomain(string host) => host != null && host.Length > RussianDomainSuffix.Length
		&& host.EndsWith(RussianDomainSuffix, StringComparison.OrdinalIgnoreCase);

	internal static bool IsRussianSelector(string selector)
	{
		string domain = RoutingInspector.NormalizeDomainRule(selector).TrimStart('*');
		return domain == RussianDomainSuffix || IsRussianDomain(domain);
	}

	internal static string NormalizeRussianDomain(string value)
	{
		string normalized = RoutingInspector.NormalizeDomainRule(value);
		try { return MultiServerRule.NormalizeDomainSelector(normalized); }
		catch (ArgumentException) { return normalized; }
	}

	internal static Dictionary<string, object> RussianDomainMatch() =>
		Obj("domain_suffix", new[] { RussianDomainSuffix });

	internal static string ApplyRussianDomains(string json, RoutingSettings routing, string proxy = "proxy")
	{
		var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
		var config = serializer.Deserialize<Dictionary<string, object>>(json);
		var outbounds = Array(config, "outbounds");
		var dns = Object(config, "dns");
		var servers = Array(dns, "servers");
		// Reserve unique tags, including for imported profiles with their own direct detours.
		string direct = AvailableTag("nova-ru-direct", outbounds.Concat(Array(config, "endpoints")));
		string resolver = AvailableTag("nova-ru-dns", servers);
		string vpnResolver = Value(dns, "final") ?? servers.OfType<Dictionary<string, object>>().Select(Tag).FirstOrDefault();
		outbounds.Add(Obj("type", "direct", "tag", direct, "domain_resolver", resolver));
		servers.Add(Obj("type", "local", "tag", resolver, "detour", direct));
		config["outbounds"] = outbounds.ToArray();
		dns["servers"] = servers.ToArray();
		dns["reverse_mapping"] = true;
		dns["independent_cache"] = true;
		var route = Object(config, "route");
		if (string.IsNullOrEmpty(Value(route, "default_interface"))) route["auto_detect_interface"] = true;
		InsertRussianDomainRules(config, routing, direct, resolver, proxy, vpnResolver ?? resolver);
		return serializer.Serialize(config);
	}

	internal static void ApplyRussianDomainsToReality(Dictionary<string, object> config, RoutingSettings routing)
	{
		var dns = Object(config, "dns");
		var servers = Array(dns, "servers");
		const string fake = "nova-ru-fakeip";
		var fakeServer = Obj("type", "fakeip", "tag", fake, "inet4_range", "198.19.0.0/16");
		if (routing.EnableIpv6) fakeServer["inet6_range"] = "fc00::/18";
		servers.Add(fakeServer);
		dns["servers"] = servers.ToArray();
		InsertRussianDomainRules(config, routing, "direct", "nova-bootstrap", "proxy", "secure-dns", fake);
	}

	private static void InsertRussianDomainRules(Dictionary<string, object> config, RoutingSettings routing,
		string direct, string resolver, string proxy, string vpnResolver, string fakeResolver = null)
	{
		var route = Object(config, "route");
		var rules = Array(route, "rules");
		var detection = rules.TakeWhile(rule => Value(rule, "action") == "sniff" || Value(rule, "action") == "hijack-dns").ToArray();
		if (!detection.Any(rule => Value(rule, "action") == "sniff"))
			rules.Insert(0, Obj("action", "sniff", "timeout", "300ms"));
		if (!detection.Any(rule => Value(rule, "action") == "hijack-dns"))
			rules.Insert(rules.TakeWhile(rule => Value(rule, "action") == "sniff").Count(),
				Obj("protocol", "dns", "action", "hijack-dns"));
		int start = rules.TakeWhile(rule => Value(rule, "action") == "sniff" || Value(rule, "action") == "hijack-dns").Count();

		var blocks = new List<object>();
		ConfigBuilder.AddDomainRule(blocks, (routing.BlockDomains ?? new List<string>()).Where(IsRussianSelector).Select(NormalizeRussianDomain), "reject");
		var overrides = new List<object>();
		if (proxy != null)
			ConfigBuilder.AddDomainRule(overrides, (routing.ProxyDomains ?? new List<string>()).Where(IsRussianSelector).Select(NormalizeRussianDomain), "proxy");

		// Keep explicit rejects in imported policies ahead of .ru without reordering non-.ru traffic.
		var scopedRejects = rules.Skip(start).OfType<Dictionary<string, object>>()
			.Where(rule => Value(rule, "action") == "reject" && rule.Keys.Any(key =>
				key != "type" && key != "action" && key != "method" && key != "no_drop"))
			.Select(rule =>
			{
				var condition = new Dictionary<string, object>(rule);
				condition.Remove("action");
				var reject = Obj("type", "logical", "mode", "and", "rules", new[] { RussianDomainMatch(), condition },
					"action", "reject");
				foreach (string key in new[] { "method", "no_drop" })
					if (condition.TryGetValue(key, out object value)) { condition.Remove(key); reject[key] = value; }
				return reject;
			}).ToArray();
		foreach (Dictionary<string, object> exception in overrides) exception["outbound"] = proxy;
		var priority = scopedRejects.Cast<object>().Concat(blocks).Concat(overrides).ToArray();
		rules.InsertRange(start, priority);
		int fallback = start + priority.Length;
		// Explicit legacy direct routes already have the same physical exit.
		while (fallback < rules.Count && Value(rules[fallback], "outbound") == ExitTag) fallback++;
		var directRule = RussianDomainMatch();
		directRule["action"] = "route";
		directRule["outbound"] = direct;
		rules.Insert(fallback, directRule);
		route["rules"] = rules.ToArray();

		var dns = Object(config, "dns");
		var dnsRules = Array(dns, "rules");
		var queries = new List<object>(blocks);
		if (fakeResolver != null)
		{
			var addressLookup = RussianDomainMatch();
			addressLookup["query_type"] = routing.EnableIpv6 ? new[] { "A", "AAAA" } : new[] { "A" };
			addressLookup["action"] = "route";
			addressLookup["server"] = fakeResolver;
			queries.Add(addressLookup);
		}
		foreach (Dictionary<string, object> exception in overrides)
		{
			var lookup = new Dictionary<string, object>(exception) { ["server"] = vpnResolver };
			lookup.Remove("outbound");
			queries.Add(lookup);
		}
		int dnsStart = dnsRules.TakeWhile(rule => Value(rule, "action") == "reject").Count();
		dnsRules.InsertRange(dnsStart, queries);
		int dnsFallback = dnsStart + queries.Count;
		while (dnsFallback < dnsRules.Count && Value(dnsRules[dnsFallback], "server") == DnsTag) dnsFallback++;
		var directLookup = RussianDomainMatch();
		directLookup["action"] = "route";
		directLookup["server"] = resolver;
		dnsRules.Insert(dnsFallback, directLookup);
		dns["rules"] = dnsRules.ToArray();
	}

	private static string AvailableTag(string basis, IEnumerable<object> items)
	{
		var occupied = new HashSet<string>(items.OfType<Dictionary<string, object>>().Select(Tag), StringComparer.Ordinal);
		string tag = basis;
		for (int i = 2; occupied.Contains(tag); i++) tag = basis + "-" + i;
		return tag;
	}

	private static string Value(object item, string key) =>
		item is Dictionary<string, object> rule && rule.TryGetValue(key, out object value) ? value as string : null;

	private static IEnumerable<string> Roots(IEnumerable<string> values) => (values ?? Enumerable.Empty<string>())
		.Select(RoutingInspector.NormalizeDomainRule).Select(value => value.TrimStart('*', '.'))
		.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
	private static string Tag(Dictionary<string, object> item) => item.TryGetValue("tag", out object tag) ? tag as string : null;
	private static Dictionary<string, object> Object(Dictionary<string, object> parent, string key)
	{
		if (!parent.TryGetValue(key, out object value)) { var created = new Dictionary<string, object>(); parent[key] = created; return created; }
		return value as Dictionary<string, object> ?? throw new InvalidOperationException("Некорректный раздел " + key);
	}
	private static List<object> Array(Dictionary<string, object> parent, string key)
	{
		if (!parent.TryGetValue(key, out object value)) return new List<object>();
		return value is System.Collections.IList items ? items.Cast<object>().ToList() : throw new InvalidOperationException("Некорректный список " + key);
	}
	private static Dictionary<string, object> Obj(params object[] values)
	{
		var result = new Dictionary<string, object>();
		for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1];
		return result;
	}
}

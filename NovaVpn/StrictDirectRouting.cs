using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace NovaVpn;

// A dedicated physical-network exit: never reuse an imported selector/detour.
public static class StrictDirectRouting
{
	private const string ExitTag = "nova-strict-direct";
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
		return value is object[] items ? items.ToList() : throw new InvalidOperationException("Некорректный список " + key);
	}
	private static Dictionary<string, object> Obj(params object[] values)
	{
		var result = new Dictionary<string, object>();
		for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1];
		return result;
	}
}

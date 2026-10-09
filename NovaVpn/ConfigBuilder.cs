using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

public static class ConfigBuilder
{
	public static string Build(VpnProfile profile, RoutingSettings routing)
	{
		if (profile == null)
		{
			throw new InvalidOperationException("Сначала выберите сервер.");
		}
		if (profile.Protocol == "sing-box")
		{
			if (string.IsNullOrWhiteSpace(profile.RawJson))
			{
				throw new InvalidOperationException("Пустая sing-box конфигурация.");
			}
			return RawConfigRoutingAdapter.Apply(profile.RawJson, routing);
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary["log"] = Obj("level", "info", "timestamp", true);
		Dictionary<string, object> dictionary2 = BuildDnsServer(routing);
		Dictionary<string, object> dictionary3 = Obj("type", "local", "tag", "local-dns");
		List<object> dnsRules = new List<object>();
		if (routing.BypassPrivateDns)
		{
			dnsRules.Add(Obj("domain_suffix", new string[1] { ".local" }, "action", "route", "server", "local-dns"));
		}
		dictionary["dns"] = Obj("servers", new object[2] { dictionary2, dictionary3 }, "rules", dnsRules.ToArray(), "final", (routing.DnsMode == "System") ? "local-dns" : "secure-dns", "strategy", DnsStrategy(routing), "reverse_mapping", true, "cache_capacity", 4096);
		List<string> list = new List<string>();
		list.Add("172.19.0.1/30");
		if (routing.EnableIpv6)
		{
			list.Add("fdfe:dcba:9876::1/126");
		}
		dictionary["inbounds"] = new object[1] { Obj("type", "tun", "tag", "tun-in", "interface_name", "NOVA", "address", list.ToArray(), "mtu", 1500, "auto_route", true, "strict_route", routing.StrictRoute, "stack", "mixed") };
		dictionary["outbounds"] = new object[2]
		{
			BuildProxy(profile),
			Obj("type", "direct", "tag", "direct")
		};
		List<object> list2 = new List<object>();
		if (routing.EnableSniffing)
		{
			list2.Add(Obj("action", "sniff", "timeout", "300ms"));
		}
		list2.Add(Obj("protocol", new string[1] { "dns" }, "action", "hijack-dns"));
		if (routing.BypassLan)
		{
			list2.Add(Obj("ip_is_private", true, "action", "route", "outbound", "direct"));
		}
		AddDomainRule(list2, routing.BlockDomains, "reject");
		if (routing.BlockAds)
		{
			AddDomainRule(list2, BuiltInAdDomains(), "reject");
		}
		// Only the curated Discord/YouTube exceptions bypass per-application
		// VPN rules; other traffic from the same browser remains in the VPN.
		AddDomainRule(list2, routing.ZapretDirectDomains, "direct");
		AddAdvancedRules(list2, routing.AdvancedRules);
		// Host/domain routing is more specific than an app-wide browser route.
		// Keep these rules ahead of process_path/process_name so a site rule wins
		// even when the browser itself is explicitly assigned to another route.
		AddOrderedDomainRules(list2, routing);
		// A full application path overrides a generic process-name rule in every mode.
		AddProcessPathRule(list2, routing.DirectProcessPaths, "direct");
		AddProcessPathRule(list2, routing.ProxyProcessPaths, "proxy");
		AddProcessRule(list2, routing.DirectProcesses, "direct");
		AddProcessRule(list2, routing.ProxyProcesses, "proxy");
		string text = "proxy";
		if (string.Equals(routing.Mode, "Direct", StringComparison.OrdinalIgnoreCase))
		{
			text = "direct";
		}
		else if (string.Equals(routing.Mode, "Smart", StringComparison.OrdinalIgnoreCase))
		{
			text = (string.Equals(routing.DefaultRoute, "Direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "proxy");
		}
		dictionary["route"] = Obj("rules", list2.ToArray(), "final", text, "auto_detect_interface", true, "default_domain_resolver", "local-dns");
		JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
		javaScriptSerializer.MaxJsonLength = int.MaxValue;
		return PrettyJson(StrictDirectRouting.Apply(javaScriptSerializer.Serialize(dictionary), routing));
	}

	private static Dictionary<string, object> BuildDnsServer(RoutingSettings routing)
	{
		string provider = routing.DnsProvider ?? "Cloudflare";
		string server = "1.1.1.1";
		string host = "cloudflare-dns.com";
		string path = "/dns-query";
		if (string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase))
		{
			server = "8.8.8.8";
			host = "dns.google";
		}
		else if (string.Equals(provider, "Quad9", StringComparison.OrdinalIgnoreCase))
		{
			server = "9.9.9.9";
			host = "dns.quad9.net";
		}
		else if (string.Equals(provider, "Custom", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(routing.CustomDnsEndpoint, UriKind.Absolute, out Uri custom) && custom.Scheme == Uri.UriSchemeHttps)
		{
			server = custom.Host;
			host = custom.Host;
			path = string.IsNullOrWhiteSpace(custom.PathAndQuery) ? "/dns-query" : custom.PathAndQuery;
		}
		return Obj("type", "https", "tag", "secure-dns", "server", server, "server_port", 443, "path", path, "tls", Obj("enabled", true, "server_name", host));
	}

	private static string DnsStrategy(RoutingSettings routing)
	{
		if (!routing.EnableIpv6 || string.Equals(routing.DnsStrategy, "IPv4Only", StringComparison.OrdinalIgnoreCase))
		{
			return "ipv4_only";
		}
		if (string.Equals(routing.DnsStrategy, "IPv6Only", StringComparison.OrdinalIgnoreCase))
		{
			return "ipv6_only";
		}
		if (string.Equals(routing.DnsStrategy, "PreferIPv6", StringComparison.OrdinalIgnoreCase))
		{
			return "prefer_ipv6";
		}
		return "prefer_ipv4";
	}

	private static Dictionary<string, object> BuildProxy(VpnProfile p)
	{
		Dictionary<string, object> dictionary = Obj("type", p.Protocol, "tag", "proxy", "server", p.Server, "server_port", p.Port, "domain_resolver", "local-dns");
		switch ((p.Protocol ?? "").ToLowerInvariant())
		{
		case "vless":
			dictionary["uuid"] = p.UserId;
			if (!string.IsNullOrWhiteSpace(p.Flow))
			{
				dictionary["flow"] = p.Flow;
			}
			AddTlsAndTransport(dictionary, p);
			break;
		case "vmess":
			dictionary["uuid"] = p.UserId;
			dictionary["security"] = (string.IsNullOrWhiteSpace(p.Method) ? "auto" : p.Method);
			AddTlsAndTransport(dictionary, p);
			break;
		case "trojan":
			dictionary["password"] = p.Password;
			AddTlsAndTransport(dictionary, p);
			break;
		case "shadowsocks":
			dictionary["method"] = p.Method;
			dictionary["password"] = p.Password;
			break;
		case "hysteria2":
			dictionary["password"] = p.Password;
			dictionary["tls"] = BuildTls(p, alwaysEnabled: true);
			if (!string.IsNullOrWhiteSpace(p.Obfs))
			{
				dictionary["obfs"] = Obj("type", p.Obfs, "password", p.ObfsPassword ?? "");
			}
			break;
		case "tuic":
			dictionary["uuid"] = p.UserId;
			dictionary["password"] = p.Password;
			dictionary["congestion_control"] = "bbr";
			dictionary["tls"] = BuildTls(p, alwaysEnabled: true);
			break;
		default:
			throw new NotSupportedException("Протокол " + p.Protocol + " пока не поддерживается.");
		}
		return dictionary;
	}

	private static void AddTlsAndTransport(Dictionary<string, object> outbound, VpnProfile p)
	{
		if (string.Equals(p.Security, "tls", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Security, "reality", StringComparison.OrdinalIgnoreCase))
		{
			outbound["tls"] = BuildTls(p, alwaysEnabled: false);
		}
		switch ((p.Transport ?? "").ToLowerInvariant())
		{
		case "ws":
		case "websocket":
		{
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			if (!string.IsNullOrWhiteSpace(p.Host))
			{
				dictionary["Host"] = p.Host;
			}
			outbound["transport"] = Obj("type", "ws", "path", string.IsNullOrWhiteSpace(p.Path) ? "/" : p.Path, "headers", dictionary);
			break;
		}
		case "grpc":
			outbound["transport"] = Obj("type", "grpc", "service_name", (p.Path ?? "").TrimStart('/'));
			break;
		case "http":
		case "h2":
		{
			object obj = (string.IsNullOrWhiteSpace(p.Host) ? new string[0] : new string[1] { p.Host });
			outbound["transport"] = Obj("type", "http", "host", obj, "path", string.IsNullOrWhiteSpace(p.Path) ? "/" : p.Path);
			break;
		}
		case "httpupgrade":
			outbound["transport"] = Obj("type", "httpupgrade", "host", p.Host ?? "", "path", string.IsNullOrWhiteSpace(p.Path) ? "/" : p.Path);
			break;
		case "quic":
			outbound["transport"] = Obj("type", "quic");
			break;
		}
	}

	private static Dictionary<string, object> BuildTls(VpnProfile p, bool alwaysEnabled)
	{
		Dictionary<string, object> dictionary = Obj("enabled", true, "server_name", string.IsNullOrWhiteSpace(p.Sni) ? p.Server : p.Sni, "insecure", string.Equals(p.Security, "insecure", StringComparison.OrdinalIgnoreCase));
		if (string.Equals(p.Security, "reality", StringComparison.OrdinalIgnoreCase))
		{
			dictionary["utls"] = Obj("enabled", true, "fingerprint", string.IsNullOrWhiteSpace(p.Fingerprint) ? "chrome" : p.Fingerprint);
			dictionary["reality"] = Obj("enabled", true, "public_key", p.PublicKey ?? "", "short_id", p.ShortId ?? "");
		}
		return dictionary;
	}

	private static void AddProcessRule(List<object> rules, IEnumerable<string> values, string outbound)
	{
		string[] array = Clean(values);
		if (array.Length != 0)
		{
			rules.Add(Obj("process_name", array, "action", "route", "outbound", outbound));
		}
	}

	private static void AddProcessPathRule(List<object> rules, IEnumerable<string> values, string outbound)
	{
		string[] array = Clean(values).Select(Environment.ExpandEnvironmentVariables).ToArray();
		if (array.Length != 0)
		{
			rules.Add(Obj("process_path", array, "action", "route", "outbound", outbound));
		}
	}

	private static void AddAdvancedRules(List<object> rules, IEnumerable<string> values)
	{
		foreach (string line in Clean(values))
		{
			string[] parts = line.Split('|');
			if (parts.Length != 3)
			{
				continue;
			}
			string route = parts[0].Trim().ToLowerInvariant();
			string network = parts[1].Trim().ToLowerInvariant();
			string ports = parts[2].Trim();
			if ((route != "proxy" && route != "direct" && route != "block") || (network != "tcp" && network != "udp"))
			{
				continue;
			}
			Dictionary<string, object> rule = Obj("network", network);
			List<int> exact = new List<int>();
			List<string> ranges = new List<string>();
			foreach (string token in ports.Split(','))
			{
				string value = token.Trim();
				if (value.Contains(":"))
				{
					ranges.Add(value);
				}
				else if (int.TryParse(value, out int port) && port > 0 && port <= 65535)
				{
					exact.Add(port);
				}
			}
			if (exact.Count == 0 && ranges.Count == 0)
			{
				continue;
			}
			if (exact.Count > 0) rule["port"] = exact.ToArray();
			if (ranges.Count > 0) rule["port_range"] = ranges.ToArray();
			rule["action"] = route == "block" ? "reject" : "route";
			if (route != "block") rule["outbound"] = route;
			rules.Add(rule);
		}
	}

	private static void AddDomainRule(List<object> rules, IEnumerable<string> values, string action)
	{
		string[] array = Clean(values);
		if (array.Length == 0)
		{
			return;
		}
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		string[] array2 = array;
		foreach (string text in array2)
		{
			string text2 = RoutingInspector.NormalizeDomainRule(text);
			if (string.IsNullOrWhiteSpace(text2))
			{
				continue;
			}
			if (text2.StartsWith("*."))
			{
				list2.Add("." + text2.Substring(2));
			}
			else if (text2.StartsWith("."))
			{
				list2.Add(text2);
			}
			else
			{
				list.Add(text2);
			}
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		if (list.Count > 0)
		{
			dictionary["domain"] = list.ToArray();
		}
		if (list2.Count > 0)
		{
			dictionary["domain_suffix"] = list2.ToArray();
		}
		if (action == "reject")
		{
			dictionary["action"] = "reject";
		}
		else
		{
			dictionary["action"] = "route";
			dictionary["outbound"] = action;
		}
		rules.Add(dictionary);
	}

	private sealed class OrderedDomainRule
	{
		public string Domain;
		public string Outbound;
		public int Score;
	}

	private static void AddOrderedDomainRules(List<object> rules, RoutingSettings routing)
	{
		List<OrderedDomainRule> ordered = new List<OrderedDomainRule>();
		foreach (string value in Clean(routing.ProxyDomains))
		{
			string domain = RoutingInspector.NormalizeDomainRule(value);
			if (!string.IsNullOrWhiteSpace(domain)) ordered.Add(new OrderedDomainRule { Domain = domain, Outbound = "proxy", Score = RoutingInspector.DomainRuleSpecificity(domain) });
		}
		foreach (string value in Clean(routing.DirectDomains))
		{
			string domain = RoutingInspector.NormalizeDomainRule(value);
			if (!string.IsNullOrWhiteSpace(domain)) ordered.Add(new OrderedDomainRule { Domain = domain, Outbound = "direct", Score = RoutingInspector.DomainRuleSpecificity(domain) });
		}
		foreach (OrderedDomainRule item in ordered
			.GroupBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.OrderByDescending(item => string.Equals(item.Outbound, "direct", StringComparison.OrdinalIgnoreCase)).First())
			.OrderByDescending(item => item.Score)
			.ThenBy(item => string.Equals(item.Outbound, "proxy", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
			.ThenBy(item => item.Domain, StringComparer.OrdinalIgnoreCase))
		{
			AddDomainRule(rules, new[] { item.Domain }, item.Outbound);
		}
	}

	private static string[] Clean(IEnumerable<string> values)
	{
		if (values == null)
		{
			return new string[0];
		}
		return (from v in values
			select (v ?? "").Trim() into v
			where v.Length > 0 && !v.StartsWith("#")
			select v).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static string[] BuiltInAdDomains()
	{
		return new string[7] { "*.doubleclick.net", "*.googlesyndication.com", "*.googleadservices.com", "*.adnxs.com", "*.adsrvr.org", "*.criteo.com", "*.scorecardresearch.com" };
	}

	private static Dictionary<string, object> Obj(params object[] pairs)
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		for (int i = 0; i + 1 < pairs.Length; i += 2)
		{
			dictionary[Convert.ToString(pairs[i])] = pairs[i + 1];
		}
		return dictionary;
	}

	private static string PrettyJson(string json)
	{
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		bool flag2 = false;
		int num = 0;
		foreach (char c in json)
		{
			if (flag2)
			{
				stringBuilder.Append(c);
				flag2 = false;
				continue;
			}
			if (c == '\\' && flag)
			{
				stringBuilder.Append(c);
				flag2 = true;
				continue;
			}
			if (c == '"')
			{
				flag = !flag;
			}
			if (!flag)
			{
				switch (c)
				{
				case '[':
				case '{':
					stringBuilder.Append(c).AppendLine();
					num++;
					stringBuilder.Append(new string(' ', num * 2));
					continue;
				case ']':
				case '}':
					stringBuilder.AppendLine();
					num--;
					stringBuilder.Append(new string(' ', num * 2)).Append(c);
					continue;
				case ',':
					stringBuilder.Append(c).AppendLine().Append(new string(' ', num * 2));
					continue;
				case ':':
					stringBuilder.Append(": ");
					continue;
				}
			}
			stringBuilder.Append(c);
		}
		return stringBuilder.ToString();
	}
}

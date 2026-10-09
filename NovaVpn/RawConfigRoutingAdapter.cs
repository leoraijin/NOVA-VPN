using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace NovaVpn;

/// <summary>Applies NOVA's explicit application and domain routes to imported sing-box JSON.</summary>
public static class RawConfigRoutingAdapter
{
	public static string Apply(string rawJson, RoutingSettings routing)
	{
		if (routing == null || !HasRules(routing)) return rawJson;
		JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
		Dictionary<string, object> config;
		try { config = serializer.DeserializeObject(rawJson) as Dictionary<string, object>; }
		catch (Exception ex) { throw new InvalidOperationException("Не удалось прочитать импортированный sing-box JSON: " + ex.Message, ex); }
		if (config == null) throw new InvalidOperationException("Импортированный sing-box JSON должен содержать объект конфигурации.");
		if (!Objects(config, "inbounds").Any(item => Value(item, "type") == "tun"))
			throw new InvalidOperationException("Маршрутизация приложений в импортированном JSON требует входящего интерфейса TUN.");
		List<Dictionary<string, object>> outbounds = Objects(config, "outbounds").ToList();
		if (outbounds.Count == 0) throw new InvalidOperationException("В импортированном JSON не найдены исходящие соединения.");
		string directTag = outbounds.Where(item => Value(item, "type") == "direct").Select(item => Value(item, "tag")).FirstOrDefault(tag => tag.Length > 0);
		Dictionary<string, object> route = config.TryGetValue("route", out object routeValue) ? routeValue as Dictionary<string, object> : null;
		if (route == null) { route = new Dictionary<string, object>(); config["route"] = route; }
		string final = Value(route, "final");
		string proxyTag = outbounds.Any(item => Value(item, "tag") == final && IsProxyType(Value(item, "type"))) ? final
			: outbounds.Where(item => IsProxyType(Value(item, "type"))).Select(item => Value(item, "tag")).FirstOrDefault(tag => tag.Length > 0);
		if (proxyTag == null && HasProxyRules(routing)) throw new InvalidOperationException("В импортированном JSON не найден VPN-выход для выбранных приложений.");
		if (directTag == null && HasDirectRules(routing))
		{
			directTag = "nova-direct";
			if (outbounds.Any(item => Value(item, "tag") == directTag)) throw new InvalidOperationException("Тег nova-direct уже занят в импортированном JSON.");
			outbounds.Add(new Dictionary<string, object> { ["type"] = "direct", ["tag"] = directTag });
			config["outbounds"] = outbounds.ToArray();
		}
		List<object> existing = new List<object>();
		if (route.TryGetValue("rules", out object rulesValue) && rulesValue != null)
		{
			if (!(rulesValue is object[] rules)) throw new InvalidOperationException("В импортированном JSON некорректен список правил route.rules.");
			existing.AddRange(rules);
		}
		List<object> inserted = new List<object>();
		AddDomainRules(inserted, routing.ZapretDirectDomains, directTag);
		// Site-specific decisions must beat broader browser/process assignments.
		AddOrderedDomainRules(inserted, routing, directTag, proxyTag);
		AddProcessRule(inserted, "process_path", routing.DirectProcessPaths, directTag, true);
		AddProcessRule(inserted, "process_path", routing.ProxyProcessPaths, proxyTag, true);
		AddProcessRule(inserted, "process_name", routing.DirectProcesses, directTag, false);
		AddProcessRule(inserted, "process_name", routing.ProxyProcesses, proxyTag, false);
		int prefix = 0;
		while (prefix < existing.Count && existing[prefix] is Dictionary<string, object> rule
			&& (Value(rule, "action") == "sniff" || Value(rule, "action") == "hijack-dns" || Value(rule, "action") == "reject")) prefix++;
		existing.InsertRange(prefix, inserted);
		route["rules"] = existing.ToArray();
		return StrictDirectRouting.Apply(serializer.Serialize(config), routing);
	}

	private static bool HasRules(RoutingSettings routing)
	{
		return HasProxyRules(routing) || HasDirectRules(routing);
	}

	private static bool HasProxyRules(RoutingSettings routing)
	{
		return Count(routing.ProxyDomains) > 0 || Count(routing.ProxyProcessPaths) > 0 || Count(routing.ProxyProcesses) > 0;
	}

	private static bool HasDirectRules(RoutingSettings routing)
	{
		return Count(routing.DirectDomains) > 0 || Count(routing.ZapretDirectDomains) > 0 || Count(routing.DirectProcessPaths) > 0 || Count(routing.DirectProcesses) > 0;
	}

	private static int Count(IEnumerable<string> values) => values == null ? 0 : values.Count(item => !string.IsNullOrWhiteSpace(item));
	private static string Value(Dictionary<string, object> item, string key) => item != null && item.TryGetValue(key, out object value) ? value as string ?? "" : "";
	private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> item, string key)
	{
		return item.TryGetValue(key, out object value) && value is object[] array ? array.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
	}

	private static bool IsProxyType(string type)
	{
		return type.Length > 0 && type != "direct" && type != "block" && type != "dns";
	}

	private static void AddProcessRule(List<object> rules, string field, IEnumerable<string> values, string outbound, bool paths)
	{
		if (values == null || outbound == null) return;
		string[] cleaned = values.Where(item => !string.IsNullOrWhiteSpace(item))
			.Select(item => paths ? ApplicationRoutingService.NormalizePath(item) : item.Trim())
			.Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (cleaned.Length > 0) rules.Add(new Dictionary<string, object> { [field] = cleaned, ["action"] = "route", ["outbound"] = outbound });
	}

	private static void AddDomainRules(List<object> rules, IEnumerable<string> values, string outbound)
	{
		if (values == null || outbound == null) return;
		foreach (string value in values)
		{
			string normalized = RoutingInspector.NormalizeDomainRule(value);
			if (normalized.Length == 0) continue;
			bool suffix = normalized.StartsWith("*.") || normalized.StartsWith(".");
			string field = suffix ? "domain_suffix" : "domain";
			string domain = suffix ? "." + normalized.TrimStart('*').TrimStart('.') : normalized;
			rules.Add(new Dictionary<string, object> { [field] = new[] { domain }, ["action"] = "route", ["outbound"] = outbound });
		}
	}

	private sealed class OrderedDomainRule
	{
		public string Domain;
		public string Outbound;
		public int Score;
	}

	private static void AddOrderedDomainRules(List<object> rules, RoutingSettings routing, string directTag, string proxyTag)
	{
		List<OrderedDomainRule> ordered = new List<OrderedDomainRule>();
		foreach (string value in routing.ProxyDomains ?? new List<string>())
		{
			string domain = RoutingInspector.NormalizeDomainRule(value);
			if (!string.IsNullOrWhiteSpace(domain)) ordered.Add(new OrderedDomainRule { Domain = domain, Outbound = proxyTag, Score = RoutingInspector.DomainRuleSpecificity(domain) });
		}
		foreach (string value in routing.DirectDomains ?? new List<string>())
		{
			string domain = RoutingInspector.NormalizeDomainRule(value);
			if (!string.IsNullOrWhiteSpace(domain)) ordered.Add(new OrderedDomainRule { Domain = domain, Outbound = directTag, Score = RoutingInspector.DomainRuleSpecificity(domain) });
		}
		foreach (OrderedDomainRule item in ordered
			.GroupBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.OrderByDescending(item => string.Equals(item.Outbound, directTag, StringComparison.OrdinalIgnoreCase)).First())
			.OrderByDescending(item => item.Score)
			.ThenBy(item => string.Equals(item.Outbound, proxyTag, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
			.ThenBy(item => item.Domain, StringComparer.OrdinalIgnoreCase))
		{
			AddDomainRules(rules, new[] { item.Domain }, item.Outbound);
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace NovaVpn;

public static class RoutingInspector
{
	public static void NormalizeDomains(List<string> values)
	{
		if (values == null)
		{
			return;
		}
		string[] normalized = values.Select(NormalizeDomainRule).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		values.Clear();
		values.AddRange(normalized);
	}

	public static string NormalizeDomainRule(string value)
	{
		value = (value ?? "").Trim();
		if (value.StartsWith("*."))
		{
			return "*." + value.Substring(2).TrimEnd('.', '/').ToLowerInvariant();
		}
		if (value.StartsWith("."))
		{
			return "." + value.Substring(1).TrimEnd('.', '/').ToLowerInvariant();
		}
		Uri uri;
		if (Uri.TryCreate(value, UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
		{
			return uri.Host.TrimEnd('.').ToLowerInvariant();
		}
		int slash = value.IndexOf('/');
		if (slash >= 0)
		{
			value = value.Substring(0, slash);
		}
		int colon = value.LastIndexOf(':');
		if (colon > 0 && value.IndexOf(':') == colon)
		{
			value = value.Substring(0, colon);
		}
		return value.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
	}

	public static RoutingDecision Explain(RoutingSettings settings, string destination, string process)
	{
		if (settings == null)
		{
			throw new ArgumentNullException(nameof(settings));
		}
		destination = NormalizeHost(destination);
		process = (process ?? "").Trim();
		if (settings.BypassLan && IsPrivateOrLocal(destination))
		{
			return Decision("direct", "Локальный или частный адрес: правило локальной сети имеет наивысший приоритет.");
		}
		string matched;
		if (Matches(settings.BlockDomains, destination, out matched))
		{
			return Decision("block", "Совпало правило блокировки домена: " + matched);
		}
		if (Matches(settings.ZapretDirectDomains, destination, out matched))
		{
			return Decision("direct", "Домен Discord/YouTube направлен напрямую к Zapret: " + matched);
		}
		bool globalMode = string.Equals(settings.Mode, "Global", StringComparison.OrdinalIgnoreCase);
		if (StrictDirectRouting.Matches(settings.DirectDomains, destination, out matched))
			return Decision("direct", "Строгий прямой маршрут сайта и поддоменов, включая DNS, без возврата на VPN: " + matched);
		if (globalMode && Matches(settings.DirectDomains, destination, out matched))
		{
			return Decision("direct", "Совпало прямое правило домена в глобальном режиме: " + matched);
		}
		if (globalMode && Matches(settings.ProxyDomains, destination, out matched))
		{
			return Decision("proxy", "Совпало VPN-правило домена в глобальном режиме: " + matched);
		}
		if (!globalMode)
		{
			DomainMatch direct = FindBestMatch(settings.DirectDomains, destination, "direct");
			DomainMatch proxy = FindBestMatch(settings.ProxyDomains, destination, "proxy");
			if (direct != null || proxy != null)
			{
				DomainMatch selected = direct == null ? proxy : proxy == null ? direct : Choose(direct, proxy);
				return Decision(selected.Route, selected.Route == "direct" ? "Совпало прямое правило домена: " + selected.Rule : "Совпало VPN-правило домена: " + selected.Rule);
			}
		}
		if (MatchesProcessPath(settings.DirectProcessPaths, process, out matched))
		{
			return Decision("direct", "Совпало правило пути приложения напрямую: " + matched);
		}
		if (MatchesProcessPath(settings.ProxyProcessPaths, process, out matched))
		{
			return Decision("proxy", "Совпало правило пути приложения через VPN: " + matched);
		}
		if (MatchesProcessName(settings.DirectProcesses, process, out matched))
		{
			return Decision("direct", "Совпало правило приложения напрямую: " + matched);
		}
		if (MatchesProcessName(settings.ProxyProcesses, process, out matched))
		{
			return Decision("proxy", "Совпало правило приложения через VPN: " + matched);
		}
		if (string.Equals(settings.Mode, "Direct", StringComparison.OrdinalIgnoreCase))
		{
			return Decision("direct", "Режим «Только выбранные через VPN»: правило не найдено.");
		}
		if (string.Equals(settings.Mode, "Global", StringComparison.OrdinalIgnoreCase))
		{
			return Decision("proxy", "Режим «Весь трафик».");
		}
		string route = string.Equals(settings.DefaultRoute, "Direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "proxy";
		return Decision(route, "Использован маршрут по умолчанию режима «По правилам».");
	}

	public static List<string> Validate(RoutingSettings settings)
	{
		List<string> warnings = new List<string>();
		if (settings == null)
		{
			warnings.Add("Настройки маршрутизации отсутствуют.");
			return warnings;
		}
		HashSet<string> direct = new HashSet<string>(Clean(settings.DirectDomains), StringComparer.OrdinalIgnoreCase);
		HashSet<string> proxy = new HashSet<string>(Clean(settings.ProxyDomains), StringComparer.OrdinalIgnoreCase);
		HashSet<string> blocked = new HashSet<string>(Clean(settings.BlockDomains), StringComparer.OrdinalIgnoreCase);
		foreach (string item in direct.Intersect(proxy, StringComparer.OrdinalIgnoreCase))
		{
			warnings.Add("Домен одновременно указан напрямую и через VPN: " + item);
		}
		foreach (string item in direct.Intersect(blocked, StringComparer.OrdinalIgnoreCase).Concat(proxy.Intersect(blocked, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			warnings.Add("Домен одновременно маршрутизируется и блокируется: " + item);
		}
		foreach (string item in direct.Concat(proxy).Concat(blocked).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!IsValidDomainRule(item))
			{
				warnings.Add("Проверьте формат домена: " + item);
			}
		}
		foreach (string path in Clean(settings.DirectProcessPaths).Where(item => Clean(settings.ProxyProcessPaths).Any(other => ApplicationRoutingService.SamePath(item, other))))
		{
			warnings.Add("Путь приложения одновременно указан напрямую и через VPN: " + path);
		}
		foreach (string name in Clean(settings.DirectProcesses).Intersect(Clean(settings.ProxyProcesses), StringComparer.OrdinalIgnoreCase))
		{
			warnings.Add("Имя приложения одновременно указано напрямую и через VPN: " + name);
		}
		foreach (string rule in Clean(settings.AdvancedRules))
		{
			string[] parts = rule.Split('|');
			if (parts.Length != 3 || !(new[] { "proxy", "direct", "block" }).Contains(parts[0].Trim(), StringComparer.OrdinalIgnoreCase) || !(new[] { "tcp", "udp" }).Contains(parts[1].Trim(), StringComparer.OrdinalIgnoreCase) || !ValidPorts(parts[2]))
			{
				warnings.Add("Некорректное расширенное правило: " + rule);
			}
		}
		return warnings;
	}

	private static RoutingDecision Decision(string route, string reason)
	{
		return new RoutingDecision { Route = route, Reason = reason };
	}

	private sealed class DomainMatch
	{
		public string Rule;
		public string Route;
		public int Score;
	}

	private static DomainMatch Choose(DomainMatch first, DomainMatch second)
	{
		if (first.Score != second.Score) return first.Score > second.Score ? first : second;
		return string.Equals(first.Route, "direct", StringComparison.OrdinalIgnoreCase) ? first : second;
	}

	private static DomainMatch FindBestMatch(IEnumerable<string> rules, string host, string route)
	{
		if (string.IsNullOrWhiteSpace(host)) return null;
		DomainMatch best = null;
		foreach (string rule in Clean(rules))
		{
			string value = NormalizeDomainRule(rule);
			bool isMatch = false;
			int score = 0;
			if (value.StartsWith("*."))
			{
				string suffix = value.Substring(2);
				if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
				{
					isMatch = true;
					score = 50000 + suffix.Length;
				}
			}
			else if (value.StartsWith("."))
			{
				string suffix = value.Substring(1);
				if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
				{
					isMatch = true;
					score = 50000 + suffix.Length;
				}
			}
			else if (host.Equals(value, StringComparison.OrdinalIgnoreCase))
			{
				isMatch = true;
				score = 100000 + value.Length;
			}
			if (isMatch && (best == null || score > best.Score)) best = new DomainMatch { Rule = rule, Route = route, Score = score };
		}
		return best;
	}

	private static bool Matches(IEnumerable<string> rules, string host, out string matched)
	{
		DomainMatch match = FindBestMatch(rules, host, "match");
		matched = match == null ? "" : match.Rule;
		return match != null;
	}

	public static int DomainRuleSpecificity(string rule)
	{
		string value = NormalizeDomainRule(rule);
		if (string.IsNullOrWhiteSpace(value)) return 0;
		return value.StartsWith("*.") || value.StartsWith(".") ? 50000 + value.Length : 100000 + value.Length;
	}

	private static bool MatchesProcessName(IEnumerable<string> names, string process, out string matched)
	{
		matched = "";
		if (string.IsNullOrWhiteSpace(process))
		{
			return false;
		}
		string fileName = Path.GetFileName(process);
		foreach (string name in Clean(names))
		{
			if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
			{
				matched = name;
				return true;
			}
		}
		return false;
	}

	private static bool MatchesProcessPath(IEnumerable<string> paths, string process, out string matched)
	{
		matched = "";
		foreach (string path in Clean(paths))
		{
			if (ApplicationRoutingService.SamePath(path, process))
			{
				matched = path;
				return true;
			}
		}
		return false;
	}

	private static string NormalizeHost(string value)
	{
		value = (value ?? "").Trim();
		Uri uri;
		if (Uri.TryCreate(value, UriKind.Absolute, out uri))
		{
			return uri.Host.TrimEnd('.');
		}
		int colon = value.LastIndexOf(':');
		if (colon > 0 && value.IndexOf(':') == colon)
		{
			value = value.Substring(0, colon);
		}
		return value.Trim('[', ']').TrimEnd('.');
	}

	private static bool IsPrivateOrLocal(string host)
	{
		if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		IPAddress ip;
		if (!IPAddress.TryParse(host, out ip))
		{
			return false;
		}
		byte[] bytes = ip.GetAddressBytes();
		if (bytes.Length == 4)
		{
			return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
		}
		return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || IPAddress.IPv6Loopback.Equals(ip) || (bytes[0] & 0xFE) == 0xFC;
	}

	private static IEnumerable<string> Clean(IEnumerable<string> values)
	{
		return (values ?? Enumerable.Empty<string>()).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0 && !v.StartsWith("#"));
	}

	private static bool IsValidDomainRule(string rule)
	{
		string value = NormalizeDomainRule(rule).TrimStart('*').TrimStart('.');
		IPAddress ignored;
		if (IPAddress.TryParse(value, out ignored))
		{
			return true;
		}
		return value.Length > 0 && value.Length <= 253 && value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_');
	}

	private static bool ValidPorts(string value)
	{
		foreach (string token in (value ?? "").Split(','))
		{
			string[] bounds = token.Trim().Split(':');
			if (bounds.Length < 1 || bounds.Length > 2 || bounds.Any(bound => !int.TryParse(bound, out int port) || port < 1 || port > 65535))
			{
				return false;
			}
			if (bounds.Length == 2 && int.Parse(bounds[0]) > int.Parse(bounds[1]))
			{
				return false;
			}
		}
		return !string.IsNullOrWhiteSpace(value);
	}
}

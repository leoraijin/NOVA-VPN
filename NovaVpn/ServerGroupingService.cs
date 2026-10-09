using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NovaVpn;

public static class ServerGroupingService
{
	private static readonly Dictionary<string, string> Countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ "RU", "Россия" }, { "DE", "Германия" }, { "NL", "Нидерланды" }, { "FI", "Финляндия" },
		{ "FR", "Франция" }, { "GB", "Великобритания" }, { "UK", "Великобритания" }, { "US", "США" },
		{ "CA", "Канада" }, { "SE", "Швеция" }, { "PL", "Польша" }, { "TR", "Турция" },
		{ "KZ", "Казахстан" }, { "JP", "Япония" }, { "SG", "Сингапур" }, { "EU", "Европа" }
	};

	public static string CountryLabel(VpnProfile profile)
	{
		string name = profile?.Name ?? "";
		Match match = Regex.Match(name, @"(?:^|\s|[-_\[])\b([A-Za-z]{2})\b");
		if (match.Success && Countries.TryGetValue(match.Groups[1].Value, out string country))
		{
			return country;
		}
		return "Другие";
	}

	public static string CountryCode(VpnProfile profile)
	{
		string name = profile?.Name ?? "";
		Match match = Regex.Match(name, @"(?:^|\s|[-_\[])\b([A-Za-z]{2})\b");
		return match.Success && Countries.ContainsKey(match.Groups[1].Value) ? match.Groups[1].Value.ToUpperInvariant().Replace("UK", "GB") : "NV";
	}

	public static string ProviderLabel(VpnProfile profile)
	{
		Uri uri;
		if (profile != null && Uri.TryCreate(profile.SubscriptionUrl, UriKind.Absolute, out uri))
		{
			return uri.Host;
		}
		return profile?.Server ?? "локальный профиль";
	}
}

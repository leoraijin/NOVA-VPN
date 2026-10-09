using System;
using System.Collections.Generic;

namespace NovaVpn;

public static class RoutingPresetService
{
	public static void Apply(RoutingSettings settings, string preset)
	{
		if (settings == null)
		{
			throw new ArgumentNullException(nameof(settings));
		}
		switch ((preset ?? "").ToLowerInvariant())
		{
			case "messengers":
				AddUnique(settings.ProxyDomains, "*.discord.com", "*.discord.gg", "*.telegram.org", "*.t.me", "*.whatsapp.com");
				AddUnique(settings.ProxyProcesses, "discord.exe", "telegram.exe", "whatsapp.exe");
				break;
			case "games-direct":
				AddUnique(settings.DirectProcesses, "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "battle.net.exe");
				break;
			case "privacy":
				settings.BlockAds = true;
				settings.StrictRoute = true;
				settings.EnableSniffing = true;
				settings.DnsMode = "Secure";
				break;
			case "compatibility":
				settings.BypassLan = true;
				settings.EnableIpv6 = false;
				settings.StrictRoute = false;
				break;
			default:
				throw new ArgumentException("Неизвестный набор правил.", nameof(preset));
		}
	}

	private static void AddUnique(List<string> target, params string[] values)
	{
		foreach (string value in values)
		{
			if (!target.Exists(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
			{
				target.Add(value);
			}
		}
	}
}

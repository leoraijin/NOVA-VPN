using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace NovaVpn;

public enum GameAdapterKind { Generic, Fortnite, WarThunder, Rust, Source }

public static class GameAdapter
{
    public static GameAdapterKind Kind(InstalledGame game)
    {
        string id = game.Id ?? "", name = game.Name ?? "";
        if (id.StartsWith("steam:252490:") || name.Equals("Rust", StringComparison.OrdinalIgnoreCase)) return GameAdapterKind.Rust;
        if (id.StartsWith("steam:236390:") || name.Equals("War Thunder", StringComparison.OrdinalIgnoreCase)) return GameAdapterKind.WarThunder;
        if (id.StartsWith("steam:730:") || name.Equals("Counter-Strike 2", StringComparison.OrdinalIgnoreCase)) return GameAdapterKind.Source;
        if (name.Equals("Fortnite", StringComparison.OrdinalIgnoreCase)) return GameAdapterKind.Fortnite;
        return GameAdapterKind.Generic;
    }
    public static List<AutomaticGameTarget> Targets(InstalledGame game, IEnumerable<GameEndpoint> observed)
    {
        var endpoints = observed.Where(e => e != null && e.Port > 0 && e.Port <= 65535 && IPAddress.TryParse(e.Address, out var ip) &&
            !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !ip.Equals(IPAddress.Broadcast) &&
            !ip.IsIPv6Multicast && !ip.IsIPv6LinkLocal && (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.GetAddressBytes()[0] < 224))
            .GroupBy(e => e.Network + e.Address + ":" + e.Port).Select(g => g.OrderByDescending(e => e.Bytes).First()).ToList();
        var kind = Kind(game); var result = new List<AutomaticGameTarget>();
        foreach (var endpoint in endpoints.Where(e => e.Network == "udp").OrderByDescending(e => e.Bytes).GroupBy(e => e.Address).Select(g => g.First()).Take(2)) {
            if (kind == GameAdapterKind.Rust) {
                // Facepunch documents a separate query port. +1/+2 are bounded guesses,
                // accepted only if an actual A2S response is validated by the probe.
                foreach (int port in new[] { endpoint.Port + 1, endpoint.Port + 2, endpoint.Port }.Where(p => p <= 65535))
                    result.Add(new AutomaticGameTarget { Target = GamingTarget.Parse(endpoint.Address, port.ToString(), true), GameProtocol = true });
            } else if (kind == GameAdapterKind.Source) result.Add(new AutomaticGameTarget { Target = GamingTarget.Parse(endpoint.Address, endpoint.Port.ToString(), true), GameProtocol = true });
        }
        result.AddRange(endpoints.Where(e => e.Network == "tcp").OrderByDescending(e => e.Bytes).Take(2)
            .Select(e => new AutomaticGameTarget { Target = GamingTarget.Parse(e.Address, e.Port.ToString(), false), GameProtocol = false }));
        return result.GroupBy(t => t.Target.Key).Select(g => g.First()).ToList();
    }
    public static string ReadRegionHint(InstalledGame game, string localAppData)
    {
        IEnumerable<string> files;
        switch (Kind(game)) {
            case GameAdapterKind.Fortnite:
                files = new[] { "WindowsClient", "Windows" }.Select(directory => Path.Combine(localAppData, "FortniteGame", "Saved", "Config", directory, "GameUserSettings.ini")); break;
            case GameAdapterKind.WarThunder:
                if (string.IsNullOrEmpty(game.InstallDirectory)) return null;
                files = new[] { Path.Combine(game.InstallDirectory, "config.blk"), Path.Combine(game.InstallDirectory, "gameSettings.blk") }; break;
            default: return null;
        }
        foreach (string file in files) try {
            if (!File.Exists(file) || new FileInfo(file).Length > 512 * 1024) continue;
            string hint = ParseRegionHint(File.ReadAllText(file)); if (hint != null) return hint;
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return null;
    }
    public static string ParseRegionHint(string configuration)
    {
        // Configuration is only a local preference hint; it is never treated as the current match's location.
        var match = Regex.Match(configuration, "(?im)^\\s*(?:LastSelectedRegionId|RegionId|selectedClusters?|preferredClusters?)(?::t)?\\s*=\\s*\"?([A-Za-z0-9;,_ -]{1,80})\"?\\s*$");
        if (!match.Success) return null;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["EU"] = "Европа", ["US"] = "Северная Америка", ["NAE"] = "Северная Америка — восток", ["NAW"] = "Северная Америка — запад", ["NAC"] = "Северная Америка — центр", ["RU"] = "Россия", ["ASIA"] = "Азия", ["AS"] = "Азия", ["OCE"] = "Океания", ["BR"] = "Бразилия", ["SA"] = "Южная Америка", ["ME"] = "Ближний Восток" };
        var regions = match.Groups[1].Value.Trim().Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (regions.Length == 0 || regions.Any(r => !names.ContainsKey(r))) return null;
        return string.Join(", ", regions.Select(r => names[r]).Distinct());
    }
}

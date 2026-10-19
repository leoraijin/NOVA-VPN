using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class GameDefinition
{
    public string Name { get; set; }
    public string[] Processes { get; set; }
    public override string ToString() => Name;
}

public static class GameCatalog
{
    public static readonly GameDefinition[] Games = {
        new GameDefinition { Name = "Fortnite", Processes = new[] { "FortniteClient-Win64-Shipping.exe" } },
        new GameDefinition { Name = "War Thunder", Processes = new[] { "aces.exe" } },
        new GameDefinition { Name = "Rust", Processes = new[] { "RustClient.exe", "Rust.exe" } },
        new GameDefinition { Name = "Counter-Strike 2", Processes = new[] { "cs2.exe" } },
        new GameDefinition { Name = "Dota 2", Processes = new[] { "dota2.exe" } },
        new GameDefinition { Name = "VALORANT", Processes = new[] { "VALORANT-Win64-Shipping.exe" } },
        new GameDefinition { Name = "League of Legends", Processes = new[] { "League of Legends.exe" } },
        new GameDefinition { Name = "Apex Legends", Processes = new[] { "r5apex.exe", "r5apex_dx12.exe" } },
        new GameDefinition { Name = "World of Warcraft", Processes = new[] { "Wow.exe", "WowClassic.exe" } },
        new GameDefinition { Name = "Overwatch", Processes = new[] { "Overwatch.exe" } },
        new GameDefinition { Name = "Minecraft", Processes = new[] { "Minecraft.Windows.exe" } },
        new GameDefinition { Name = "Другая игра / EXE", Processes = new string[0] }
    };

    public static bool IsRunning(IEnumerable<string> processes)
    {
        var names = new HashSet<string>((processes ?? new string[0]).Select(System.IO.Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses()) {
            using (process) { try { if (names.Contains(process.ProcessName)) return true; } catch (InvalidOperationException) { } }
        }
        return false;
    }

    public static string NetworkKey()
    {
        return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.Id + ":" + string.Join(",", n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()).OrderBy(a => a)) + ":" +
                string.Join(",", n.GetIPProperties().GatewayAddresses.Select(a => a.Address.ToString()).OrderBy(a => a)))
            .OrderBy(n => n));
    }
}

public sealed class GamingTarget
{
    public string Host;
    public int Port;
    public bool A2S;
    public string Key => Host.ToLowerInvariant() + ":" + Port + (A2S ? "/a2s" : "/tcp");
    public string Method => A2S ? "A2S UDP: ответ query-порта, не телеметрия матча" : "TCP CONNECT: предварительная оценка, не игровой UDP-пинг";
    public static GamingTarget Parse(string host, string port, bool a2s)
    {
        host = (host ?? "").Trim();
        int number;
        if (host.Length == 0 || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !int.TryParse(port, out number) || number < 1 || number > 65535)
            throw new ArgumentException("Укажите адрес сервера без схемы и порт 1–65535.");
        return new GamingTarget { Host = host, Port = number, A2S = a2s };
    }
}

public sealed class GamingProbeResult
{
    public string ProfileId;
    public string Name;
    public readonly List<double> Samples = new List<double>();
    public int Attempts;
    public string Error;
    public bool IsDirect => ProfileId == null;
    public bool Reliable => Samples.Count >= 8 && FailureRate <= 0.2;
    public double FailureRate => Attempts == 0 ? 1 : 1 - (double)Samples.Count / Attempts;
    public double Median => Quantile(0.5);
    public double P95 => Quantile(0.95);
    public double Spread => P95 - Median;
    private double Quantile(double q) {
        if (Samples.Count == 0) return double.PositiveInfinity;
        var sorted = Samples.OrderBy(v => v).ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
    }
    public string Summary => Samples.Count == 0 ? (Error ?? "Нет ответа; маршрут не оценён") :
        string.Format("{0:F1} мс · p95 {1:F1} · разброс {2:F1} · нет ответа {3:P0} · {4}/{5}", Median, P95, Spread, FailureRate, Samples.Count, Attempts);
}

public static class GamingRouteSelector
{
    // A single fast packet never wins. Require a material improvement without worse tails or failures.
    public static GamingProbeResult Choose(IList<GamingProbeResult> results, bool allowDirect)
    {
        var direct = results.FirstOrDefault(r => r.IsDirect && r.Reliable);
        var best = results.Where(r => !r.IsDirect && r.Reliable)
            .OrderBy(r => r.Median + 0.5 * r.Spread + 300 * r.FailureRate).FirstOrDefault();
        if (!allowDirect) return best;
        if (direct == null) return null; // Cannot claim an improvement over an unmeasured baseline.
        if (best == null || direct.Median - best.Median < Math.Max(5, direct.Median * 0.1) ||
            best.P95 > direct.P95 + 5 || best.FailureRate > direct.FailureRate) return direct;
        return best;
    }
}

public sealed class GamingRoute
{
    public string GameId;
    public string Game;
    public string[] Processes;
    public string ExecutablePath;
    public string[] ExecutablePaths;
    public bool GameOnly;
    public VpnProfile Profile; // null means explicitly permitted direct route
}

[DataContract]
public sealed class GamingHistoryEntry
{
    [DataMember] public string Game;
    [DataMember] public string Method;
    [DataMember] public string Result;
    [DataMember] public DateTime Utc;
}

public static class GamingRouting
{
    public static RoutingSettings Effective(RoutingSettings basis, GamingRoute game, bool killSwitch)
    {
        if (game != null && killSwitch && (game.Profile == null || game.GameOnly))
            throw new InvalidOperationException("Kill Switch запрещает прямой выход. Для игрового режима сначала подключите обычный VPN.");
        var result = RoutingOverlayService.Clone(basis); result.Gaming = game;
        if (game?.GameOnly == true) { result.Mode = "Direct"; result.DnsMode = "System"; }
        return result;
    }
    public static string Apply(string json, GamingRoute game)
    {
        if (game == null) return json;
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var config = serializer.Deserialize<Dictionary<string, object>>(json);
        var route = (Dictionary<string, object>)config["route"];
        var rules = ((System.Collections.IEnumerable)route["rules"]).Cast<object>().ToList();
        string tag = "direct";
        if (game.Profile != null) {
            tag = "nova-game";
            var outbound = serializer.Deserialize<Dictionary<string, object>>(ConfigBuilder.ExportOutbound(game.Profile));
            outbound["tag"] = tag;
            config["outbounds"] = ((System.Collections.IEnumerable)config["outbounds"]).Cast<object>().Concat(new object[] { outbound }).ToArray();
        }
        var rule = new Dictionary<string, object> { ["action"] = "route", ["outbound"] = tag };
        if (game.ExecutablePaths?.Length > 0) rule["process_path"] = game.ExecutablePaths;
        else if (!string.IsNullOrEmpty(game.ExecutablePath)) rule["process_path"] = new[] { game.ExecutablePath };
        else rule["process_name"] = game.Processes;
        bool IsPrefix(object item) => item is Dictionary<string, object> d && d.TryGetValue("action", out object a) && new[] { "sniff", "hijack-dns", "reject" }.Contains(a as string);
        // Blocks and DNS interception retain priority; all permitted game traffic uses its own exit.
        var prefix = rules.Where(IsPrefix).ToList();
        var remainder = rules.Where(r => !IsPrefix(r)).ToList();
        if (game.GameOnly) { remainder.Clear(); route["final"] = "direct"; }
        rules = prefix.Concat(new object[] { rule }).Concat(remainder).ToList();
        route["rules"] = rules.ToArray();
        if (config.TryGetValue("dns", out object dnsValue) && dnsValue is Dictionary<string, object> dns) {
            var servers = ((System.Collections.IEnumerable)dns["servers"]).Cast<object>().ToList();
            var template = servers.OfType<Dictionary<string, object>>().FirstOrDefault(s => s.TryGetValue("type", out object t) && Equals(t, "https"));
            var gameDns = game.Profile == null ? new Dictionary<string, object> { ["type"] = "local" } :
                template == null ? new Dictionary<string, object> { ["type"] = "https", ["server"] = "1.1.1.1", ["server_port"] = 443, ["path"] = "/dns-query" } : new Dictionary<string, object>(template);
            gameDns["tag"] = "nova-game-dns";
            if (game.Profile != null) gameDns["detour"] = tag;
            servers.Add(gameDns); dns["servers"] = servers.ToArray();
            var dnsRules = dns.TryGetValue("rules", out object existingRules) ? ((System.Collections.IEnumerable)existingRules).Cast<object>().ToList() : new List<object>();
            var dnsRule = new Dictionary<string, object> { ["action"] = "route", ["server"] = "nova-game-dns" };
            if (rule.TryGetValue("process_path", out object paths)) dnsRule["process_path"] = paths;
            else dnsRule["process_name"] = game.Processes;
            dnsRules.Insert(0, dnsRule); dns["rules"] = dnsRules.ToArray();
        }
        return serializer.Serialize(config);
    }
}

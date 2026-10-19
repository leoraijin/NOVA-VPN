using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using NovaVpn;

internal static class AutomaticGamingTests
{
    static int checks;
    static void Check(bool value, string description) { checks++; if (!value) throw new Exception(description); }
    static GamingProbeResult Row(string id, double milliseconds, int replies = 10) {
        var result = new GamingProbeResult { ProfileId = id, Attempts = 10 };
        for (int i = 0; i < replies; i++) result.Samples.Add(milliseconds);
        return result;
    }
    static List<GamingProbeResult> Rows(params GamingProbeResult[] rows) => rows.ToList();
    static int Main(string[] args) {
        try {
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 50), Row("b", 47)) }, "a") == "a", "Tiny gain changed current exit");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 50), Row("b", 35)) }, "a") == "b", "Material stable improvement ignored");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 50), Row("b", 1, 1)) }, "a") == "a", "One fast reply won");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 50), Row("b", 30)), Rows(Row("a", 50), Row("b", 30, 1)) }, "a") == "a", "Winner failed another target");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 100), Row("b", 20)), Rows(Row("a", 50), Row("b", 60)) }, "a") == "a", "Average gain hid worse tail at second server");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 50, 1), Row("b", 60)) }, "a") == "b", "Unreliable baseline was retained");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 1, 1)) }, null) == null, "Failed measurement produced a recommendation");
            var endpoints = new List<GameEndpoint>();
            for (int i = 1; i <= 5; i++) endpoints.Add(new GameEndpoint { Address = "203.0.113." + i, Port = 27015, Network = "udp", Bytes = i * 100 });
            endpoints.Add(new GameEndpoint { Address = "203.0.113.10", Port = 443, Network = "tcp", Bytes = 100 });
            var rust = AutomaticGamingService.Targets(new InstalledGame { Name = "Rust" }, endpoints);
            Check(rust.Count == 7 && rust.Count(t => t.GameProtocol) == 6 && rust.Any(t => !t.GameProtocol), "Rust query candidates hid TCP fallback");
            Check(rust.First().Target.Host == "203.0.113.5", "Traffic ranking ignored");
            Check(rust.Take(3).Select(t => t.Target.Port).SequenceEqual(new[] { 27016, 27017, 27015 }), "Rust separate query-port candidates missing");
            Check(!GameAdapter.Targets(new InstalledGame { Name = "Rusty Tool" }, endpoints).Any(t => t.GameProtocol), "Unrelated product inherited Rust query protocol");
            Check(GameAdapter.ParseRegionHint("LastSelectedRegionId=EU\n") == "Европа", "Fortnite region hint not read");
            Check(GameAdapter.ParseRegionHint("selectedClusters:t=\"EU;US\"\n") == "Европа, Северная Америка", "Multiple War Thunder cluster hints lost");
            Check(GameAdapter.ParseRegionHint("LastSelectedRegionId=AUTO\n") == null, "Auto region presented as a measured location");
            var fortnite = AutomaticGamingService.Targets(new InstalledGame { Name = "Fortnite" }, endpoints);
            Check(fortnite.Count == 1 && !fortnite[0].GameProtocol, "Fortnite UDP was falsely labelled query-compatible");
            endpoints.Add(endpoints.Last());
            Check(AutomaticGamingService.Targets(new InstalledGame { Name = "Fortnite" }, endpoints).Count == 1, "Duplicate destination measured twice");
            var seen = new HashSet<string>(); var verified = new HashSet<string>(); int largestBatch = 0;
            var engine = new AutomaticGamingService((target, profiles, rounds, token) => {
                token.ThrowIfCancellationRequested(); largestBatch = Math.Max(largestBatch, profiles.Count);
                foreach (var profile in profiles) { seen.Add(profile.Id); if (rounds == 10) verified.Add(profile.Id); }
                return Task.FromResult(profiles.Select(p => Row(p.Id, p.Id == "p9" ? 10 : 100 + int.Parse(p.Id.Substring(1)))).ToList());
            });
            var users = Enumerable.Range(1, 9).Select(i => new VpnProfile { Id = "p" + i, Protocol = "vless" }).ToList();
            var automatic = engine.SelectAsync(new InstalledGame { Name = "Fortnite" }, endpoints, users, "p1", "unused", "unused", null, CancellationToken.None).GetAwaiter().GetResult();
            Check(seen.Count == 9 && automatic.ProfileId == "p9", "Best user server beyond first eight was never tested");
            Check(largestBatch <= 4 && verified.Contains("p9") && verified.Contains("p1"), "Shortlist missed winner or baseline, or spawned unbounded cores");
            Check(!automatic.GameProtocol && !automatic.KeptCurrent, "Service TCP estimate claimed game-protocol evidence");
            bool cancelled = false; using (var cts = new CancellationTokenSource()) {
                cts.Cancel(); try { engine.SelectAsync(new InstalledGame { Name = "Fortnite" }, endpoints, users, null, "unused", "unused", null, cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Check(cancelled, "Cancellation produced an automatic recommendation");
            bool directSeen = false;
            var directEngine = new AutomaticGamingService((target, profiles, rounds, token) => {
                directSeen |= profiles.Any(p => p.Id == "direct");
                return Task.FromResult(profiles.Select(p => Row(p.Id, p.Id == "direct" ? 20 : 40)).ToList());
            });
            var withoutDirect = directEngine.SelectAsync(new InstalledGame { Name = "Fortnite" }, endpoints, users, null, "unused", "unused", null, CancellationToken.None).GetAwaiter().GetResult();
            Check(!directSeen && withoutDirect.ProfileId != "direct", "Direct baseline escaped explicit caller policy");
            var withDirect = directEngine.SelectAsync(new InstalledGame { Name = "Fortnite" }, endpoints, users, null, "unused", "unused", null, CancellationToken.None, true).GetAwaiter().GetResult();
            Check(directSeen && withDirect.ProfileId == "direct" && withDirect.KeptCurrent, "Ordinary connection was not kept when better");
            var steam = GameLaunchService.Build(new InstalledGame { Source = "Steam", LaunchUri = "steam://rungameid/252490", InstallDirectory = @"D:\Games" });
            Check(steam.FileName == "steam://rungameid/252490", "Steam game bypassed launcher");
            bool rejected = false;
            try { GameLaunchService.Build(new InstalledGame { Source = "Steam", LaunchUri = "https://example.com/download.exe", Executables = new List<string>() }); } catch (System.IO.IOException) { rejected = true; }
            Check(rejected, "Untrusted launcher scheme accepted");
            rejected = false;
            try { GameLaunchService.Build(new InstalledGame { Source = "Riot Games", Executables = new List<string> { @"C:\Game.exe" } }); } catch (System.IO.IOException) { rejected = true; }
            Check(rejected, "Riot game bypassed required launcher");
            string file = Path.Combine(args[0], "stamp-fixture.exe"); File.WriteAllText(file, "initial fixture");
            var identity = new InstalledGame { Id = "fixture", Executables = new List<string> { file } };
            var profileSet = new[] { new VpnProfile { Id = "vpn", Protocol = "vless", Server = "example.com", Password = "private-fixture" } };
            DateTime now = DateTime.UtcNow;
            var stamp = new GamingRecommendationStamp(identity, profileSet, "network-a", "vpn", false, VpnEngine.SingBox, CoreStatus.Connected, now);
            Func<bool> current = () => stamp.IsCurrent(identity, profileSet, "network-a", "vpn", false, VpnEngine.SingBox, CoreStatus.Connected, now.AddSeconds(1));
            Check(current(), "Unchanged recommendation invalid");
            profileSet[0].LastLatency = 20; Check(current(), "Background health stats invalidated measured server");
            profileSet[0].Password = "changed-fixture"; Check(!current(), "Changed transport credential retained recommendation");
            profileSet[0].Password = "private-fixture";
            Check(!stamp.IsCurrent(identity, profileSet, "network-b", "vpn", false, VpnEngine.SingBox, CoreStatus.Connected, now), "Changed network retained recommendation");
            Check(!stamp.IsCurrent(identity, profileSet, "network-a", "vpn", true, VpnEngine.SingBox, CoreStatus.Connected, now), "Changed Kill Switch retained recommendation");
            Check(!stamp.IsCurrent(identity, profileSet, "network-a", "vpn", false, VpnEngine.SingBox, CoreStatus.Connected, now.AddMinutes(6)), "Expired recommendation accepted");
            Check(!stamp.IsCurrent(identity, new VpnProfile[0], "network-a", "vpn", false, VpnEngine.SingBox, CoreStatus.Connected, now), "Deleted profile retained recommendation");
            File.WriteAllText(file, "new game version with a different size"); Check(!current(), "Game update retained recommendation");
            File.Delete(file);
            Console.WriteLine("PASS: " + checks + " automatic selection checks: comparable targets, stable current route, unreliable probes and protocol confidence.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

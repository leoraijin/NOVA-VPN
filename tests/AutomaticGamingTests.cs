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
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("a", 50), Row("b", 35)) }, "a") == null, "VPN-only comparison authorized a route without direct");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), Row("b", 47)) }, "a") == "direct", "Noise must retain direct");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), Row("b", 35)) }, "a") == "b", "Material stable improvement ignored");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), Row("b", 1, 1)) }, "a") == "direct", "One fast reply won");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), Row("b", 30)), Rows(Row("direct", 50), Row("b", 30, 1)) }, "a") == "direct", "Winner failed another target");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 100), Row("b", 20)), Rows(Row("direct", 50), Row("b", 60)) }, "a") == "direct", "Average gain hid worse second server");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50, 1), Row("b", 30)) }, "a") == null, "Unreliable direct baseline authorized VPN");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), Row("b", 50)) }, "a") == "direct", "Equal ping enabled VPN");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 40), Row("b", 50)) }, "b") == "direct", "Slower active VPN beat direct");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 100), Row("active", 30), Row("best", 25)) }, "active") == "best", "Active VPN preference hid minimum median");
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), Row("b", 20)), Rows(Row("b", 20)) }, "a") == null, "Missing direct on second target accepted");
            var tail = Row("vpn", 20); tail.Samples[9] = 60;
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), tail) }, "a") == "direct", "Worse VPN tail accepted");
            var loss = Row("vpn", 20, 8);
            Check(AutomaticGamingService.Choose(new[] { Rows(Row("direct", 50), loss) }, "a") == "direct", "Worse VPN loss accepted");
            var directJson = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(AutomaticGamingService.DirectProfile("Physical-Fixture").PortableOutboundJson);
            Check((string)directJson["type"] == "direct" && (string)directJson["bind_interface"] == "Physical-Fixture" && !directJson.ContainsKey("detour"), "Direct control was not pinned to a physical exit");
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
            int measurementCalls = 0;
            var seen = new HashSet<string>(); var verified = new HashSet<string>(); int largestBatch = 0;
            var engine = new AutomaticGamingService((target, profiles, rounds, token) => {
                measurementCalls++;
                token.ThrowIfCancellationRequested(); largestBatch = Math.Max(largestBatch, profiles.Count);
                foreach (var profile in profiles) { seen.Add(profile.Id); if (rounds == 10) verified.Add(profile.Id); }
                return Task.FromResult(profiles.Select(p => Row(p.Id, p.Id == "direct" ? 150 : p.Id == "p9" ? 10 : 100 + int.Parse(p.Id.Substring(1)))).ToList());
            });
            var users = Enumerable.Range(1, 9).Select(i => new VpnProfile { Id = "p" + i, Protocol = "vless" }).ToList();
            var automatic = engine.SelectAsync(new InstalledGame { Name = "Rust" }, endpoints, users, "p1", "unused", "unused", null, CancellationToken.None).GetAwaiter().GetResult();
            Check(seen.Count == 10 && automatic.ProfileId == "p9", "Best user server beyond first eight or mandatory direct was not tested");
            Check(largestBatch <= 4 && verified.Count == 10 && verified.Contains("p9") && verified.Contains("direct"), "Shortlist missed winner or direct baseline, or spawned unbounded cores");
            Check(automatic.GameProtocol && automatic.HasVerifiedImprovement && automatic.CanPlay, "Verified query improvement rejected");
            Check(automatic.Comparisons.All(c => c.Direct.ProfileId == "direct" && c.Candidate.ProfileId == "p9"), "Result lost same-target direct evidence");
            automatic.ProfileId = "p2"; Check(!automatic.HasVerifiedImprovement && !automatic.CanPlay, "Changed profile inherited another profile's evidence"); automatic.ProfileId = "p9";
            automatic.GameProtocol = false; Check(!automatic.CanPlay, "TCP result authorized game route"); automatic.GameProtocol = true;
            automatic.Comparisons[0].Direct.Samples.Clear(); Check(!automatic.CanPlay, "Damaged direct evidence authorized route");
            int callsBefore = measurementCalls;
            var unsupported = engine.SelectAsync(new InstalledGame { Name = "Fortnite" }, endpoints, users, "p1", "unused", "unused", null, CancellationToken.None).GetAwaiter().GetResult();
            Check(unsupported.ProfileId == "direct" && unsupported.CanPlay && !unsupported.HasVerifiedImprovement && measurementCalls == callsBefore, "Fortnite TCP estimate became an automatic VPN recommendation");
            bool cancelled = false; using (var cts = new CancellationTokenSource()) {
                cts.Cancel(); try { engine.SelectAsync(new InstalledGame { Name = "Fortnite" }, endpoints, users, null, "unused", "unused", null, cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Check(cancelled, "Cancellation produced an automatic recommendation");
            bool directSeen = false; int directCalls = 0;
            var directEngine = new AutomaticGamingService((target, profiles, rounds, token) => {
                directCalls++; directSeen |= profiles.Any(p => p.Id == "direct");
                return Task.FromResult(profiles.Select(p => Row(p.Id, p.Id == "direct" ? 20 : 40)).ToList());
            });
            var blocked = directEngine.SelectAsync(new InstalledGame { Name = "Rust" }, endpoints, users, "p1", "unused", "unused", null, CancellationToken.None, false).GetAwaiter().GetResult();
            Check(!directSeen && directCalls == 0 && blocked.ProfileId == null, "Kill Switch policy bypassed or missing direct allowed recommendation");
            var withDirect = directEngine.SelectAsync(new InstalledGame { Name = "Rust" }, endpoints, users, "p1", "unused", "unused", null, CancellationToken.None, true).GetAwaiter().GetResult();
            Check(directSeen && withDirect.ProfileId == "direct" && withDirect.CanPlay && !withDirect.HasVerifiedImprovement, "Direct was not selected when VPN was worse despite an active VPN baseline");
            Check(withDirect.Message.Contains("Лучше остаться на прямом"), "Slower VPN result lacks direct recommendation");
            var tcpOnly = directEngine.SelectAsync(new InstalledGame { Name = "Rust" }, endpoints.Where(e => e.Network == "tcp"), users, "p1", "unused", "unused", null, CancellationToken.None).GetAwaiter().GetResult();
            Check(tcpOnly.ProfileId == null && !tcpOnly.CanPlay, "Rust TCP fallback authorized VPN without game-query evidence");
            var unavailable = new AutomaticGamingService((target, profiles, rounds, token) => Task.FromResult(profiles.Select(p => Row(p.Id, 10, p.Id == "direct" ? 0 : 10)).ToList()));
            var noDirect = unavailable.SelectAsync(new InstalledGame { Name = "Rust" }, endpoints, users, null, "unused", "unused", null, CancellationToken.None).GetAwaiter().GetResult();
            Check(noDirect.ProfileId == null && !noDirect.CanPlay, "VPN-only reachable target authorized route");
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
            Console.WriteLine("PASS: " + checks + " automatic selection checks: mandatory physical direct baseline, strict improvements, same targets, protocol evidence and activation gates.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

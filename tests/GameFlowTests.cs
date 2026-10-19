using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using NovaVpn;

internal static class GameFlowTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
    static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private).SetValue(owner, value);
    static Task Call(MainWindow window, string method, params object[] args) => (Task)typeof(MainWindow).GetMethod(method, Private).Invoke(window, args);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static VpnProfile Profile(string id, int port) => new VpnProfile { Id = id, Name = id, Protocol = "vless", PortableOutboundJson = new JavaScriptSerializer().Serialize(new { type = "socks", tag = "proxy", server = "127.0.0.1", server_port = port, version = "5", bind_interface = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Name }) };
    static async Task<string> Request(string executable, string stage, CoreManager core, FixtureSocksServer regular, FixtureSocksServer gaming) {
        Console.WriteLine("FLOW REQUEST: " + stage + " · " + Path.GetFileName(executable) + " · core=" + core.Status);
        using (var process = Process.Start(new ProcessStartInfo(executable, "198.18.0.10 18080") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (await Task.WhenAny(output, Task.Delay(10000)) != output) { process.Kill(); process.WaitForExit(3000); throw new Exception("Client flow traffic timed out at " + stage + "; core=" + core.Status + "; regular=" + regular.Diagnostics + "; gaming=" + gaming.Diagnostics + "\nCORE LOG:\n" + core.Logs); }
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Traffic failed at " + stage + "; core=" + core.Status +
                "; regular=" + regular.Diagnostics + "; gaming=" + gaming.Diagnostics + "\n" + await error + "\nCORE LOG:\n" + core.Logs);
            Console.WriteLine("FLOW RESPONSE: " + stage + " · " + await output); return await output;
        }
    }
    static async Task Run(string corePath, string work)
    {
        string coreDir = Path.Combine(work, "core"); Directory.CreateDirectory(coreDir);
        File.Copy(corePath, Path.Combine(coreDir, "sing-box.exe"), true);
        File.Copy(Path.Combine(Path.GetDirectoryName(corePath), "wintun.dll"), Path.Combine(coreDir, "wintun.dll"), true);
        string gameExe = Path.Combine(work, "GameTrafficFixture.exe"), otherExe = Path.Combine(work, "OtherTrafficFixture.exe"); File.Copy(gameExe, otherExe, true);
        var window = new MainWindow(true); var state = (AppState)Field(window, "state"); var core = (CoreManager)Field(window, "core");
        Set(core, "verificationBusy", true); // No external health, IP or game-account requests in this fixture.
        state.Profiles.Clear(); state.AutoReconnect = false; state.AutoConnect = false; state.ShowNotifications = false;
        var game = new InstalledGame { Id = "flow-fixture", Name = "Flow Fixture", InstallDirectory = work, Executables = new List<string> { gameExe } };
        Set(window, "gamingSelected", game); Set(window, "gamingLibrary", new List<InstalledGame> { game });
        using (var regular = new FixtureSocksServer("REGULAR")) using (var gaming = new FixtureSocksServer("GAME")) try {
            var basis = Profile("base", regular.Port); var picked = Profile("game", gaming.Port);
            state.Profiles.Add(basis); state.Profiles.Add(picked); state.SelectedProfileId = basis.Id;
            state.Routing = new RoutingSettings { Mode = "Direct", DnsMode = "System", EnableSniffing = false, BypassLan = false };
            state.Routing.ProxyProcessPaths.Add(otherExe);
            var probeProfile = new VpnProfile { Id = "probe", Protocol = "vless", PortableOutboundJson = new JavaScriptSerializer().Serialize(new { type = "direct", tag = "proxy", bind_interface = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Name }) };
            using (var cancellation = new System.Threading.CancellationTokenSource()) {
                var scan = new GamingProbeService().ScanAsync(new GamingTarget { Host = "127.0.0.1", Port = regular.Port }, new[] { probeProfile }, corePath, Path.Combine(work, "flow-probe"), false, null, cancellation.Token, 20);
                try { await core.StartAsync(basis, state.Routing); await scan; }
                finally { cancellation.Cancel(); try { await scan; } catch (OperationCanceledException) { } }
            }
            Console.WriteLine("PASS: normal client connection can start while isolated gaming probes are active.");
            Check((await Request(otherExe, "baseline", core, regular, gaming)).EndsWith("REGULAR"), "Baseline app rule failed before gaming");
            var preference = (GamePreferences)typeof(MainWindow).GetMethod("GetGamePreferences", Private).Invoke(window, new object[] { game.Id }); preference.ProfileId = picked.Id;
            int baselineOwner = ((Process)Field(core, "process")).Id;
            Set(window, "gamingAutomaticResult", new AutomaticGamingResult { ProfileId = picked.Id });
            Set(window, "gamingRecommendation", new GamingRecommendationStamp(game, state.Profiles, GameCatalog.NetworkKey(), core.ActiveProfileId, false, core.Engine, core.Status, DateTime.UtcNow));
            await Call(window, "ApplyGamingAsync");
            Check(Field(window, "gamingSession") == null && ((Process)Field(core, "process")).Id == baselineOwner, "Unverified TCP recommendation interrupted baseline");
            Console.WriteLine("PASS: unverified gaming result cannot activate a route or restart the working VPN.");
            var directEvidence = new GamingProbeResult { ProfileId = "direct", Attempts = 10 };
            var gameEvidence = new GamingProbeResult { ProfileId = picked.Id, Attempts = 10 };
            directEvidence.Samples.AddRange(Enumerable.Repeat(80.0, 10)); gameEvidence.Samples.AddRange(Enumerable.Repeat(40.0, 10));
            Set(window, "gamingAutomaticResult", new AutomaticGamingResult { ProfileId = picked.Id, GameProtocol = true,
                Comparisons = new List<GamingRouteComparison> { new GamingRouteComparison { TargetKey = "198.18.0.10:18080/a2s", Direct = directEvidence, Candidate = gameEvidence } } });
            Set(window, "gamingRecommendation", new GamingRecommendationStamp(game, state.Profiles, GameCatalog.NetworkKey(), core.ActiveProfileId, false, core.Engine, core.Status, DateTime.UtcNow));
            await Call(window, "ApplyGamingAsync");
            Check(Field(window, "gamingSession") != null && core.Status == CoreStatus.Connected, "Client failed to activate gaming: " + Field(window, "gamingMessage"));
            Check(core.LastConnectedRouting.Gaming != null && core.LastConnectedRouting.Gaming.Profile?.Id == picked.Id, "Active core snapshot lost the game overlay");
            string activeConfig = File.ReadAllText((string)Field(core, "configPath"));
            var activeJson = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(activeConfig);
            Console.WriteLine("FLOW ACTIVE ROUTE: " + new JavaScriptSerializer().Serialize(activeJson["route"]));
            Check((await Request(gameExe, "gaming", core, regular, gaming)).EndsWith("GAME"), "Client game traffic did not use selected config");
            Check((await Request(otherExe, "ordinary-during-game", core, regular, gaming)).EndsWith("REGULAR"), "Client changed ordinary application route");
            state.Routing.ProxyDomains.Add("edited-during-session.invalid"); state.Routing.ProxyProcessPaths.Add(gameExe);
            await Call(window, "StopGamingAsync", "Fixture finished.");
            Check(Field(window, "gamingSession") == null && core.Status == CoreStatus.Connected && core.ActiveProfileId == basis.Id, "Client did not restore original normal connection");
            Check(core.LastConnectedRouting.Gaming == null && core.LastConnectedRouting.ProxyDomains.Contains("edited-during-session.invalid"), "Client restored an obsolete routing snapshot over user edits");
            Check((await Request(gameExe, "game-restored", core, regular, gaming)).EndsWith("REGULAR"), "Game did not return to latest ordinary app rule");
            Check((await Request(otherExe, "ordinary-restored", core, regular, gaming)).EndsWith("REGULAR"), "Ordinary app failed after client session stop");
            var manager = (GamingSessionManager)Field(window, "gamingSessions"); Check(manager.Phase == GamingSessionPhase.Idle, "Client journal was not finished");
            Console.WriteLine("PASS: actual MainWindow/CoreManager lifecycle with real Windows TUN: game-only override, other app preserved, normal VPN restored and latest user edits applied.");
            preference.ProfileId = "direct"; preference.AllowDirect = true;
            Set(window, "gamingAutomaticResult", new AutomaticGamingResult { ProfileId = "direct", Message = "Explicit direct fallback" });
            Set(window, "gamingRecommendation", new GamingRecommendationStamp(game, state.Profiles, GameCatalog.NetworkKey(), core.ActiveProfileId, false, core.Engine, core.Status, DateTime.UtcNow));
            await Call(window, "ApplyGamingAsync");
            Check(Field(window, "gamingSession") != null && core.LastConnectedRouting.Gaming.Profile == null, "Explicit direct fallback failed to activate");
            var directConfig = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText((string)Field(core, "configPath")));
            var directRoute = (Dictionary<string, object>)directConfig["route"];
            var directRules = ((System.Collections.IEnumerable)directRoute["rules"]).Cast<object>().OfType<Dictionary<string, object>>().Where(row => row.ContainsKey("process_path") && ((System.Collections.IEnumerable)row["process_path"]).Cast<string>().Contains(gameExe)).ToList();
            Check(directRules.Count > 0 && (string)directRules[0]["outbound"] == "direct", "Ordinary game VPN rule defeated explicit direct fallback");
            Check((await Request(otherExe, "ordinary-during-direct-game", core, regular, gaming)).EndsWith("REGULAR"), "Direct fallback changed another application's route");
            await Call(window, "StopGamingAsync", "Direct fixture finished.");
            Check((await Request(gameExe, "game-after-direct-session", core, regular, gaming)).EndsWith("REGULAR"), "Direct fallback did not restore ordinary game VPN rule");
            Console.WriteLine("PASS: explicit direct game fallback overrides ordinary VPN rule, preserves other app and restores normal policy.");
            preference.ProfileId = picked.Id; preference.AllowDirect = false;
            Set(window, "gamingAutomaticResult", new AutomaticGamingResult { ProfileId = picked.Id, GameProtocol = true,
                Comparisons = new List<GamingRouteComparison> { new GamingRouteComparison { TargetKey = "198.18.0.10:18080/a2s", Direct = directEvidence, Candidate = gameEvidence } } });
            // A bad route must be rejected before the working baseline process is stopped.
            picked.PortableOutboundJson = "{\"type\":\"unsupported-fixture-outbound\"}";
            Set(window, "gamingRecommendation", new GamingRecommendationStamp(game, state.Profiles, GameCatalog.NetworkKey(), core.ActiveProfileId, false, core.Engine, core.Status, DateTime.UtcNow));
            int owner = ((Process)Field(core, "process")).Id;
            await Call(window, "ApplyGamingAsync");
            Check(core.Status == CoreStatus.Connected && ((Process)Field(core, "process")).Id == owner && Field(window, "gamingSession") == null, "Invalid game config interrupted the baseline VPN");
            Console.WriteLine("PASS: invalid gaming config left the existing normal core process and routing alive.");
        } finally { core.Dispose(); window.Close(); }
    }
    [STAThread] static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") { Console.WriteLine("SKIP: real client lifecycle TUN test runs only in isolated GitHub Windows VM."); return 0; }
        int result = 1; var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Dispatcher.BeginInvoke((Action)(async () => {
            try { await Run(args[0], args[1]); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { application.Shutdown(); }
        }));
        application.Run(); return result;
    }
}

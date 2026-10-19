using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using NovaVpn;

internal static class GameLibraryTests
{
    static int count;
    static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    static void Write(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, text); }
    static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    static void Discovery(string work)
    {
        string a = Path.Combine(work, "Steam A"), b = Path.Combine(work, "Steam B");
        string rust = Path.Combine(a, "steamapps", "common", "Rust", "RustClient.exe"); Write(rust, "fixture");
        Write(Path.Combine(a, "steamapps", "common", "Rust", "Uninstall.exe"), "fixture");
        string war = Path.Combine(b, "steamapps", "common", "War Thunder", "win64", "aces.exe"); Write(war, "fixture");
        Write(Path.Combine(a, "steamapps", "libraryfolders.vdf"), "// modern Steam libraries\n\"libraryfolders\" {\"0\" {\"path\" " + Quote(a) + "} \"1\" {\"path\" " + Quote(b) + "}}");
        Write(Path.Combine(a, "steamapps", "appmanifest_252490.acf"), "\"AppState\" {\"appid\" \"252490\" \"name\" \"Rust\" \"installdir\" \"Rust\" \"StateFlags\" \"4\"}");
        Write(Path.Combine(b, "steamapps", "appmanifest_236390.acf"), "\"AppState\" {\"appid\" \"236390\" \"name\" \"War Thunder\" \"installdir\" \"War Thunder\" \"StateFlags\" \"6\"}");
        Write(Path.Combine(a, "steamapps", "appmanifest_bad.acf"), "\"unclosed");
        Write(Path.Combine(a, "steamapps", "appmanifest_missing.acf"), "\"AppState\" {\"appid\" \"3\" \"name\" \"Removed\" \"installdir\" \"Removed\"}");
        Write(Path.Combine(a, "steamapps", "common", "Downloading", "Game.exe"), "fixture");
        Write(Path.Combine(a, "steamapps", "appmanifest_downloading.acf"), "\"AppState\" {\"appid\" \"4\" \"name\" \"Downloading\" \"installdir\" \"Downloading\" \"StateFlags\" \"2\"}");
        Write(Path.Combine(a, "steamapps", "common", "Soundpad", "soundpad.exe"), "fixture");
        Write(Path.Combine(a, "steamapps", "appmanifest_629520.acf"), "\"AppState\" {\"appid\" \"629520\" \"name\" \"Soundpad\" \"installdir\" \"Soundpad\" \"StateFlags\" \"4\"}");
        var games = GameDiscoveryService.DiscoverSteam(a);
        Check(games.Count == 2, "Steam secondary library, malformed/deleted/downloading manifests");
        Check(games.First(g => g.Name == "Rust").Executables.SequenceEqual(new[] { rust }), "Uninstaller routed as game");
        Check(games.First(g => g.Name == "War Thunder").Executables.Contains(war), "Nested game EXE not found");
        Write(Path.Combine(a, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {\"1\" " + Quote(b) + "}");
        Check(GameDiscoveryService.DiscoverSteam(a).Count == 2, "Legacy Steam library format");
        string epic = Path.Combine(work, "Epic manifests"), install = Path.Combine(work, "Fortnite");
        string executable = Path.Combine(install, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"); Write(executable, "fixture");
        var serializer = new JavaScriptSerializer();
        Write(Path.Combine(epic, "Fortnite.item"), serializer.Serialize(new { AppName = "Fortnite", DisplayName = "Fortnite", InstallLocation = install, LaunchExecutable = "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe" }));
        Write(Path.Combine(epic, "Incomplete.item"), serializer.Serialize(new { AppName = "Other", DisplayName = "Incomplete", InstallLocation = install, bIsIncompleteInstall = true }));
        Check(GameDiscoveryService.DiscoverEpic(epic).Single().Executables.SequenceEqual(new[] { executable }), "Epic executable identity/incomplete state");
        Write(Path.Combine(install, "FortniteLauncher.exe"), "launcher");
        Write(Path.Combine(epic, "Fortnite.item"), serializer.Serialize(new { AppName = "Fortnite", DisplayName = "Fortnite", InstallLocation = install, LaunchExecutable = "FortniteLauncher.exe" }));
        Check(GameDiscoveryService.DiscoverEpic(epic).Single().Executables.SequenceEqual(new[] { executable }), "Epic bootstrap launcher became game routing identity");
        string riot = Path.Combine(work, "Riot", "VALORANT"); Write(Path.Combine(riot, "VALORANT.exe"), "launcher");
        string riotGame = Path.Combine(riot, "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"); Write(riotGame, "game");
        string riotManifest = Path.Combine(work, "RiotClientInstalls.json");
        Write(riotManifest, serializer.Serialize(new { associated_client = new Dictionary<string, object> { [Path.Combine(riot, "VALORANT.exe").Replace('\\','/')] = "RiotClient.exe" } }));
        Check(GameDiscoveryService.DiscoverRiot(riotManifest).Single().Executables.SequenceEqual(new[] { riotGame }), "Riot game identity included shared client/launcher");
        Check(!GameDiscoveryService.IsInside(install + "-other\\Game.exe", install), "Sibling path escaped installation identity");
        bool malformed = false; try { GameDiscoveryService.ParseVdf("\"a\" { \"b\" \"c\""); } catch (IOException) { malformed = true; }
        Check(malformed, "Truncated VDF accepted");
    }
    static void Policy(string work)
    {
        var basis = new RoutingSettings(); basis.DirectDomains.Add("direct.example"); basis.BlockDomains.Add("blocked.example"); basis.ProxyProcessPaths.Add(@"C:\Browser.exe");
        var baseProfile = new VpnProfile { Id = "base", Protocol = "vless", PortableOutboundJson = "{\"type\":\"direct\",\"tag\":\"proxy\"}" };
        var game = new GamingRoute { GameId = "game", Game = "Fixture", ExecutablePaths = new[] { @"C:\Games\Game.exe" }, Profile = baseProfile };
        var serializer = new JavaScriptSerializer();
        var effective = GamingRouting.Effective(basis, game, false);
        var config = (Dictionary<string, object>)serializer.DeserializeObject(ConfigBuilder.Build(baseProfile, effective));
        var rules = ((object[])((Dictionary<string, object>)config["route"])["rules"]).Cast<Dictionary<string, object>>().ToList();
        int index = rules.FindIndex(r => r.TryGetValue("outbound", out object value) && Equals(value, "nova-game"));
        Check(index >= 0 && rules.Take(index).All(r => new[] { "sniff", "hijack-dns", "reject" }.Contains((string)r["action"])), "Game must override sites and app rules but preserve blocks/DNS");
        Check(((object[])rules[index]["process_path"]).Cast<string>().SequenceEqual(game.ExecutablePaths), "Game route not restricted to exact paths");
        Check(!rules[index].ContainsKey("process_name"), "Game routed by broad process name");
        basis.ProxyDomains.Add("edited-during-game.example");
        Check(!effective.ProxyDomains.Contains("edited-during-game.example"), "Frozen session policy mutated");
        var restored = GamingRouting.Effective(basis, null, false);
        Check(restored.Gaming == null && restored.ProxyDomains.Contains("edited-during-game.example"), "Restore overwrote newer user edits");
        game.GameOnly = true;
        config = (Dictionary<string, object>)serializer.DeserializeObject(ConfigBuilder.Build(baseProfile, GamingRouting.Effective(basis, game, false)));
        var route = (Dictionary<string, object>)config["route"];
        Check(Equals(route["final"], "direct"), "Game-only mode redirected other apps");
        Check(!((object[])route["rules"]).Cast<Dictionary<string, object>>().Any(r => r.TryGetValue("outbound", out object value) && Equals(value, "proxy")), "Ordinary app proxy rule leaked into game-only mode");
        bool denied = false; try { GamingRouting.Effective(basis, game, true); } catch (InvalidOperationException) { denied = true; }
        Check(denied, "Kill Switch bypassed by game-only direct fallback");
        var session = new GamingSessionManager(Path.Combine(work, "session"));
        session.Prepare("game", true, "base"); session.Activate();
        Check(session.Phase == GamingSessionPhase.Active && session.BaseWasConnected && session.BaseProfileId == "base", "Session state incorrect");
        string journal = File.ReadAllText(Path.Combine(work, "session", "gaming-session.json"));
        Check(!journal.Contains("password") && !journal.Contains("vless") && !journal.Contains("C:\\Games"), "Private config in journal");
        var afterCrash = new GamingSessionManager(Path.Combine(work, "session"));
        Check(afterCrash.Recover() && afterCrash.Phase == GamingSessionPhase.Idle && !afterCrash.Recover(), "Crash journal recovery not idempotent");
        using (var stream = new MemoryStream()) {
            new DataContractJsonSerializer(typeof(RoutingSettings)).WriteObject(stream, effective);
            Check(!Encoding.UTF8.GetString(stream.ToArray()).Contains("ExecutablePaths"), "Runtime game policy persisted into settings");
        }
    }
    static void Monitor()
    {
        string path = @"C:\Games\Game.exe"; var serializer = new JavaScriptSerializer();
        Func<string, string, string, int, object> row = (process, address, network, port) => new { metadata = new { processPath = process, destinationIP = address, network = network, destinationPort = port.ToString() }, upload = 1024, download = 2048 };
        var json = serializer.Serialize(new { connections = new[] { row(path + " (user)", "203.0.113.1", "udp", 27015), row(@"C:\Other\Game.exe", "203.0.113.2", "tcp", 443), row(path + "-evil.exe", "203.0.113.3", "tcp", 443), row(path, "127.0.0.1", "tcp", 100), row(path, "203.0.113.4", "udp", 53) } });
        var result = GameConnectionMonitor.Parse(json, new[] { path });
        Check(result.Count == 1 && result[0].Address == "203.0.113.1" && result[0].Bytes == 3072, "Endpoint association mixed games, DNS or localhost");
    }
    public static int Main(string[] args)
    {
        try { string work = Path.GetFullPath(args[0]); Directory.CreateDirectory(work); Discovery(work); Policy(work); Monitor(); Console.WriteLine("PASS: " + count + " library, exact-route, edits-preserved, journal-recovery and endpoint-identity checks."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

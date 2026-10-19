using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NovaVpn;

public static class GamingTests
{
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static GamingProbeResult Result(string id, params double[] samples) {
        var r = new GamingProbeResult { ProfileId = id, Name = id ?? "direct", Attempts = samples.Length };
        r.Samples.AddRange(samples); return r;
    }
    static double[] Repeat(double n) => Enumerable.Repeat(n, 10).ToArray();
    static void Selector()
    {
        var direct = Result(null, Repeat(90)); var fast = Result("fast", Repeat(65));
        Check(GamingRouteSelector.Choose(new[] { direct, fast }, true) == fast, "Material improvement should win");
        Check(GamingRouteSelector.Choose(new[] { direct, Result("noise", Repeat(87)) }, true) == direct, "Noise must retain direct");
        var unstable = Result("unstable", 20,20,20,20,20,20,20,20,20,300);
        Check(GamingRouteSelector.Choose(new[] { direct, unstable }, true) == direct, "Bad tail must lose");
        var lossy = Result("lossy", Repeat(60)); lossy.Attempts = 20;
        Check(GamingRouteSelector.Choose(new[] { direct, lossy }, true) == direct, "Failures must not win");
        Check(GamingRouteSelector.Choose(new[] { fast }, true) == null, "No direct evidence means no improvement claim");
        Check(GamingRouteSelector.Choose(new[] { direct, fast }, false) == fast, "Direct forbidden must never be chosen");
        Check(GamingRouteSelector.Choose(new[] { direct }, false) == null, "No eligible VPN must return no selection");
        var sparse = Result("sparse", 1,1,1);
        Check(GamingRouteSelector.Choose(new[] { direct, sparse }, true) == direct, "Insufficient samples must not win");
        foreach (var invalid in new[] { "https://example.com", "", "a/b", "host:80" }) {
            bool rejected = false; try { GamingTarget.Parse(invalid, "443", false); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid host accepted");
        }
        Check(GamingTarget.Parse("127.0.0.1", "27015", true).A2S, "A2S target parse");
    }
    static VpnProfile FakeProfile() {
        string loopback = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Name;
        return new VpnProfile { Id = "fixture", Name = "Synthetic local transport",
            Protocol = "vless", Server = "127.0.0.1", Port = 9, UserId = "00000000-0000-0000-0000-000000000001",
            PortableOutboundJson = new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                ["type"] = "direct", ["tag"] = "proxy", ["bind_interface"] = loopback }) };
    }

    static void Routing(string core, string work)
    {
        var serializer = new JavaScriptSerializer();
        var baseProfile = FakeProfile();
        var routing = new RoutingSettings(); routing.BlockDomains.Add("blocked.example"); routing.ProxyProcesses.Add("RustClient.exe");
        var game = new GamingRoute { Game = "Rust", Processes = new[] { "RustClient.exe" }, Profile = FakeProfile() };
        routing.Gaming = game;
        var json = ConfigBuilder.Build(baseProfile, routing);
        var config = (Dictionary<string, object>)serializer.DeserializeObject(json);
        var rules = ((object[])((Dictionary<string, object>)config["route"])["rules"]).Cast<Dictionary<string, object>>().ToList();
        int gaming = rules.FindIndex(r => r.ContainsKey("outbound") && (string)r["outbound"] == "nova-game");
        int ordinary = rules.FindLastIndex(r => r.ContainsKey("process_name"));
        Check(gaming >= 0 && gaming < ordinary, "Gaming route must precede ordinary application routes");
        Check(rules.FindIndex(r => r.ContainsKey("action") && (string)r["action"] == "reject") < gaming, "Blocking must retain priority");
        Check(((object[])config["outbounds"]).Length == 3, "Gaming must add an independent outbound");
        Check(routing.ProxyProcesses.SequenceEqual(new[] { "RustClient.exe" }), "Stored rules mutated");
        Check(RoutingOverlayService.Clone(routing).Gaming == game, "Reconnect lost game route");
        string path = Path.Combine(work, "game-config.json"); File.WriteAllText(path, json, new UTF8Encoding(false));
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(core, "check -c " + AppUpdateService.QuoteWindowsArgument(path)) { UseShellExecute = false, CreateNoWindow = true })) {
            process.WaitForExit(); Check(process.ExitCode == 0, "Real sing-box rejected gaming configuration");
        }
        game.Profile = null; game.ExecutablePath = "C:\\Games\\RustClient.exe";
        json = ConfigBuilder.Build(baseProfile, routing);
        Check(json.Contains("process_path") && !json.Contains("nova-game"), "Direct/full path route invalid");
        bool rawRejected = false;
        try { ConfigBuilder.Build(new VpnProfile { Protocol = "sing-box", RawJson = "{}" }, routing); } catch (NotSupportedException) { rawRejected = true; }
        Check(rawRejected, "Raw config must not silently ignore gaming overlay");
        File.Delete(path);
    }

    static async Task Probes(string core, string work)
    {
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var cancellation = new CancellationTokenSource()) {
            var challenged = new HashSet<int>(); int challenges = 0;
            var responder = Task.Run(async () => {
                try { while (!cancellation.IsCancellationRequested) {
                    var request = await udp.ReceiveAsync();
                    byte[] response;
                    if (!challenged.Contains(request.RemoteEndPoint.Port)) {
                        challenged.Add(request.RemoteEndPoint.Port); challenges++;
                        response = new byte[] {255,255,255,255,0x41,1,2,3,4};
                    } else {
                        if (request.Buffer.Length < 4 || !request.Buffer.Skip(request.Buffer.Length - 4).SequenceEqual(new byte[] {1,2,3,4})) continue;
                        response = new byte[] {255,255,255,255,0x49,17}.Concat(Encoding.ASCII.GetBytes("Fixture\0Map\0Folder\0Game\0"))
                            .Concat(new byte[] {1,0,1,16,0,100,119,0,1}).Concat(Encoding.ASCII.GetBytes("1.0\0")).ToArray();
                    }
                    await udp.SendAsync(response, response.Length, request.RemoteEndPoint);
                } } catch (ObjectDisposedException) { } catch (SocketException) { }
            });
            try {
                var port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                var results = await new GamingProbeService().ScanAsync(new GamingTarget { Host = "127.0.0.1", Port = port, A2S = true },
                    new[] { FakeProfile() }, core, work, true, null, CancellationToken.None);
                foreach (var r in results) Console.WriteLine(r.Name + ": " + r.Summary + " · " + r.Error);
                Check(results.Count == 2 && results.All(r => r.Reliable && r.Samples.Count == 10), "Real A2S challenge through SOCKS UDP and direct must succeed");
                Check(challenges >= 20, "A2S challenge exchange not exercised");
                Check(!Directory.GetFiles(work, "gaming-*.json").Any(), "Probe credentials/config files leaked");
            } finally { cancellation.Cancel(); udp.Close(); await responder; }
        }
        using (var blackhole = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var cts = new CancellationTokenSource(400)) {
            bool cancelled = false;
            try { await new GamingProbeService().ScanAsync(new GamingTarget { Host = "127.0.0.1", Port = ((IPEndPoint)blackhole.Client.LocalEndPoint).Port, A2S = true },
                new[] { FakeProfile() }, core, work, true, null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !GamingProbeService.IsScanning, "Cancellation did not release scan lease");
            Check(!Directory.GetFiles(work, "gaming-*.json").Any(), "Cancellation leaked temporary config");
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        bool stop = false;
        var accept = Task.Run(async () => { try { while (!stop) { using (var client = await listener.AcceptTcpClientAsync()) { } } } catch (ObjectDisposedException) { } catch (SocketException) { } });
        try {
            var results = await new GamingProbeService().ScanAsync(new GamingTarget { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port },
                new[] { FakeProfile() }, core, work, true, null, CancellationToken.None);
            Check(results.Count == 2 && results.All(r => r.Reliable), "TCP direct and authenticated SOCKS CONNECT failed");
        } finally { stop = true; listener.Stop(); await accept; }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[1]); Selector(); Routing(args[0], args[1]); Probes(args[0], args[1]).GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + assertions + " assertions; route priority, real sing-box validation, authenticated SOCKS TCP/UDP, A2S challenge, cancellation and cleanup. Synthetic loopback only; no live-game claim."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NovaVpn;

internal static class GameTunIsolationTests
{
    static VpnProfile Profile(string id, int port) => new VpnProfile { Id = id, Protocol = "vless", PortableOutboundJson = new JavaScriptSerializer().Serialize(new { type = "socks", tag = "proxy", server = "127.0.0.1", server_port = port, version = "5", bind_interface = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Name }) };
    static string Request(string executable) {
        using (var process = Process.Start(new ProcessStartInfo(executable, "198.18.0.10 18080") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("Traffic fixture timed out"); }
            if (process.ExitCode != 0) throw new Exception("Traffic fixture failed: " + error.Result); return output.Result;
        }
    }
    static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") { Console.WriteLine("SKIP: TUN isolation test runs only in the isolated GitHub Windows VM."); return 0; }
        Process core = null; var log = new StringBuilder();
        try {
            string work = args[1], gameExe = Path.Combine(work, "GameTrafficFixture.exe"), otherExe = Path.Combine(work, "OtherTrafficFixture.exe");
            File.Copy(gameExe, otherExe, true);
            using (var regular = new FixtureSocksServer("REGULAR")) using (var gaming = new FixtureSocksServer("GAME")) {
                var basis = Profile("base", regular.Port); var game = new GamingRoute { Game = "Fixture", ExecutablePaths = new[] { gameExe }, Profile = Profile("game", gaming.Port) };
                var settings = new RoutingSettings { Mode = "Direct", DnsMode = "System", EnableSniffing = false, BypassLan = false }; settings.ProxyProcessPaths.Add(otherExe); settings.Gaming = game;
                var serializer = new JavaScriptSerializer(); var config = (Dictionary<string, object>)serializer.DeserializeObject(ConfigBuilder.Build(basis, settings));
                var tun = (Dictionary<string, object>)((object[])config["inbounds"])[0]; tun["interface_name"] = "NOVA-Library-Test"; tun["address"] = new[] { "172.30.253.1/30" };
                string file = Path.Combine(work, "tun-fixture.json"); File.WriteAllText(file, serializer.Serialize(config), new UTF8Encoding(false));
                core = new Process { StartInfo = OwnedCoreProcess.Wrap(new ProcessStartInfo(args[0], "run -c " + AppUpdateService.QuoteWindowsArgument(file)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) };
                core.OutputDataReceived += (s,e) => { if(e.Data != null) lock(log) log.AppendLine(e.Data); }; core.ErrorDataReceived += (s,e) => { if(e.Data != null) lock(log) log.AppendLine(e.Data); };
                core.Start(); core.BeginOutputReadLine(); core.BeginErrorReadLine(); Thread.Sleep(4000);
                if (core.HasExited) throw new Exception("TUN core exited: " + log);
                if (!Request(gameExe).EndsWith("GAME")) throw new Exception("Game did not use gaming outbound");
                if (!Request(otherExe).EndsWith("REGULAR")) throw new Exception("Other application was diverted to game config");
                core.Kill(); core.WaitForExit(5000); core.Dispose(); core = null;
                // Build a fresh baseline: the same game returns to the user's application rule.
                settings.Gaming = null; settings.ProxyProcessPaths.Add(gameExe);
                config = (Dictionary<string, object>)serializer.DeserializeObject(ConfigBuilder.Build(basis, settings)); tun = (Dictionary<string, object>)((object[])config["inbounds"])[0]; tun["interface_name"] = "NOVA-Library-Test"; tun["address"] = new[] { "172.30.253.1/30" };
                File.WriteAllText(file, serializer.Serialize(config), new UTF8Encoding(false));
                Thread.Sleep(1000);
                core = Process.Start(OwnedCoreProcess.Wrap(new ProcessStartInfo(args[0], "run -c " + AppUpdateService.QuoteWindowsArgument(file)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }));
                core.BeginOutputReadLine(); core.BeginErrorReadLine(); Thread.Sleep(3000);
                if (!Request(gameExe).EndsWith("REGULAR")) throw new Exception("Game did not return to baseline after overlay removal");
            }
            Console.WriteLine("PASS: real Windows TUN routed the game through GAME, other app through REGULAR, then game through REGULAR after removing the session overlay."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine(log.ToString()); return 1; }
        finally { if (core != null) { try { if (!core.HasExited) { core.Kill(); core.WaitForExit(5000); } } finally { core.Dispose(); } } }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NovaVpn;

internal static class GameUdpDiscoveryTests
{
    static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
    static int Main(string[] args) {
        try {
            var type = typeof(GameUdpDiscovery);
            if (IntPtr.Size == 8) {
                Check(Marshal.SizeOf(type.GetNestedType("TraceLogfile", BindingFlags.NonPublic)) == 448, "ETW logfile ABI mismatch");
                Check(Marshal.SizeOf(type.GetNestedType("TraceProperties", BindingFlags.NonPublic)) == 120, "ETW properties ABI mismatch");
                Check(Marshal.SizeOf(type.GetNestedType("EventRecord", BindingFlags.NonPublic)) == 112, "ETW event ABI mismatch");
            }
            Console.WriteLine("PASS: Windows ETW native structure layouts.");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") { Console.WriteLine("SKIP: native UDP session test is restricted to the isolated GitHub Windows VM."); return 0; }
            string work = args[0], fixture = Path.Combine(work, "UdpTrafficFixture.exe"), other = Path.Combine(work, "OtherUdpFixture.exe");
            File.Copy(fixture, other, true);
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .First(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            using (var receiver = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            using (var foreignReceiver = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) {
                int port = ((IPEndPoint)receiver.Client.LocalEndPoint).Port, foreignPort = ((IPEndPoint)foreignReceiver.Client.LocalEndPoint).Port;
                var game = new InstalledGame { Executables = new System.Collections.Generic.List<string> { fixture } };
                using (var observer = new GameUdpDiscovery(game, work)) {
                    Check(observer.Active, "ETW session failed: " + observer.Error);
                    using (var owned = Process.Start(new ProcessStartInfo(fixture, address + " " + port) { UseShellExecute = false, CreateNoWindow = true }))
                    using (var foreign = Process.Start(new ProcessStartInfo(other, address + " " + foreignPort) { UseShellExecute = false, CreateNoWindow = true })) {
                        try {
                            bool found = false;
                            for (int i = 0; i < 30; i++) {
                                Thread.Sleep(250); var snapshot = observer.Snapshot();
                                Check(snapshot.All(e => e.Port != foreignPort), "Unrelated process UDP was attributed to the game");
                                if (snapshot.Any(e => e.Address == address.ToString() && e.Port == port && e.Bytes >= 64)) { found = true; break; }
                            }
                            Check(found, "Real UDP peer not discovered from exact game process");
                        } finally { if (!owned.HasExited) { owned.Kill(); owned.WaitForExit(3000); } if (!foreign.HasExited) { foreign.Kill(); foreign.WaitForExit(3000); } }
                    }
                }
                Check(!Directory.EnumerateFiles(work, "udp-observer-*.json").Any(), "ETW cleanup marker left after normal stop");
            }
            Console.WriteLine("PASS: real Windows UDP peer discovery, exact process-path isolation and owned ETW session cleanup. No TUN or VPN started.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

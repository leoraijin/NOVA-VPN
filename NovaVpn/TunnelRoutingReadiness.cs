using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Proves that the recreated core can identify the local process, not merely that
// its TUN exists. Misses are rejected inside the core; no probe goes to a VPN server.
internal sealed class TunnelRoutingReadiness : IDisposable
{
    private const string Tag = "nova-route-readiness";
    private const string TargetV4 = "198.18.255.254";
    private const string TargetV6 = "2001:db8::ffff";
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly byte[] nonce = new byte[32];
    private readonly string image, loopback;
    private readonly int port;
    private bool disposed;
    public TunnelRoutingReadiness()
    {
        using (var process = Process.GetCurrentProcess()) image = process.MainModule.FileName;
        loopback = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback).Name;
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
        listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = ServeAsync();
    }
    private async Task ServeAsync()
    {
        try {
            while (!disposed) {
                using (var peer = await listener.AcceptTcpClientAsync()) {
                    try { await peer.GetStream().WriteAsync(nonce, 0, nonce.Length); }
                    catch (IOException) { } catch (SocketException) { }
                }
            }
        } catch (ObjectDisposedException) { } catch (SocketException) { }
    }
    public string Apply(string json, bool ipv6)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var config = serializer.Deserialize<Dictionary<string, object>>(json);
        var route = (Dictionary<string, object>)config["route"];
        var targets = ipv6 ? new[] { TargetV4 + "/32", TargetV6 + "/128" } : new[] { TargetV4 + "/32" };
        var probe = new Dictionary<string, object> {
            ["inbound"] = new[] { "tun-in" }, ["ip_cidr"] = targets, ["port"] = new[] { port },
            ["process_path"] = new[] { image }, ["action"] = "route", ["outbound"] = Tag,
            ["override_address"] = "127.0.0.1", ["override_port"] = port
        };
        var reject = new Dictionary<string, object> {
            ["inbound"] = new[] { "tun-in" }, ["ip_cidr"] = targets, ["port"] = new[] { port }, ["action"] = "reject"
        };
        route["rules"] = new object[] { probe, reject }.Concat(((System.Collections.IEnumerable)route["rules"]).Cast<object>()).ToArray();
        config["outbounds"] = ((System.Collections.IEnumerable)config["outbounds"]).Cast<object>().Concat(new object[] {
            new Dictionary<string, object> { ["type"] = "direct", ["tag"] = Tag, ["bind_interface"] = loopback }
        }).ToArray();
        return serializer.Serialize(config);
    }
    public async Task<bool> WaitAsync(Func<bool> current, bool ipv6)
    {
        var clock = Stopwatch.StartNew();
        var pending = new HashSet<string>(ipv6 ? new[] { TargetV4, TargetV6 } : new[] { TargetV4 });
        while (clock.Elapsed < TimeSpan.FromSeconds(20)) {
            if (!current()) return false;
            foreach (string target in pending.ToArray()) {
                using (var client = new TcpClient(target == TargetV4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6)) {
                    try {
                        await GamingProbeService.WithTimeout(ExchangeAsync(client, target), CancellationToken.None, 500);
                        pending.Remove(target);
                    } catch (IOException) { } catch (SocketException) { } catch (TimeoutException) { }
                }
            }
            if (pending.Count == 0) return current();
            await Task.Delay(100);
        }
        throw new IOException("Ядро не подтвердило маршрутизацию процесса через VPN-интерфейс. Повторите подключение.");
    }
    private async Task<bool> ExchangeAsync(TcpClient client, string target)
    {
        await client.ConnectAsync(IPAddress.Parse(target), port);
        var reply = new byte[nonce.Length]; int offset = 0;
        while (offset < reply.Length) {
            int count = await client.GetStream().ReadAsync(reply, offset, reply.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        if (!reply.SequenceEqual(nonce)) throw new IOException("Unexpected readiness response");
        return true;
    }
    public void Dispose() { disposed = true; listener.Stop(); }
}

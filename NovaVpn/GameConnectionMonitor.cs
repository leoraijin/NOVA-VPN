using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class GameEndpoint
{
    public string Address;
    public int Port;
    public string Network;
    public long Bytes;
    public string Label => Network.ToUpperInvariant() + " · " + Address + ":" + Port + " · " + (Bytes / 1024) + " КБ";
}

public sealed class GameConnectionMonitor
{
    private readonly string secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly int port;
    public GameConnectionMonitor()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { port = ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }
    public string Apply(string json)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var config = serializer.Deserialize<Dictionary<string, object>>(json);
        var experimental = config.TryGetValue("experimental", out object value) && value is Dictionary<string, object> existing ? existing : new Dictionary<string, object>();
        experimental["clash_api"] = new Dictionary<string, object> { ["external_controller"] = "127.0.0.1:" + port, ["secret"] = secret };
        config["experimental"] = experimental;
        ((Dictionary<string, object>)config["route"])["find_process"] = true;
        return serializer.Serialize(config);
    }
    public async Task<List<GameEndpoint>> ReadAsync(InstalledGame game, CancellationToken token)
    {
        using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 1024 * 1024 }) {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using (var response = await client.GetAsync("http://127.0.0.1:" + port + "/connections", token)) {
                response.EnsureSuccessStatusCode(); return Parse(await response.Content.ReadAsStringAsync(), game.Executables);
            }
        }
    }
    public static List<GameEndpoint> Parse(string json, IEnumerable<string> executablePaths)
    {
        var allowed = executablePaths.ToArray();
        var data = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
        if (data == null || !data.TryGetValue("connections", out object value) || !(value is object[] connections)) return new List<GameEndpoint>();
        var endpoints = new List<GameEndpoint>();
        foreach (var connection in connections.OfType<Dictionary<string, object>>()) {
            if (!connection.TryGetValue("metadata", out object metadataValue) || !(metadataValue is Dictionary<string, object> m)) continue;
            string Read(string key) => m.TryGetValue(key, out object field) ? Convert.ToString(field) : "";
            string processPath = Read("processPath");
            if (!allowed.Any(path => processPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                (processPath.StartsWith(path + " (", StringComparison.OrdinalIgnoreCase) && processPath.EndsWith(")")))) continue;
            string address = Read("destinationIP"), network = Read("network").ToLowerInvariant();
            if (!IPAddress.TryParse(address, out IPAddress ip) || IPAddress.IsLoopback(ip) || (network != "tcp" && network != "udp")) continue;
            if (!int.TryParse(Read("destinationPort"), out int remotePort) || remotePort <= 0 || remotePort > 65535 || remotePort == 53) continue;
            long Bytes(string key) => connection.TryGetValue(key, out object count) && long.TryParse(Convert.ToString(count), out long number) ? Math.Max(0, number) : 0;
            endpoints.Add(new GameEndpoint { Address = address, Port = remotePort, Network = network, Bytes = Bytes("upload") + Bytes("download") });
        }
        return endpoints.GroupBy(e => e.Network + ":" + e.Address + ":" + e.Port).Select(g => g.OrderByDescending(e => e.Bytes).First())
            .OrderByDescending(e => e.Network == "udp").ThenByDescending(e => e.Bytes).Take(12).ToList();
    }
}

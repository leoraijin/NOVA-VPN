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

namespace NovaVpn;

// Probes own their sing-box processes; no TUN, no fallback and no personal-state access.
public sealed class GamingProbeService
{
    private static int scanning;
    private static readonly List<GamingProbeChannel> owned = new List<GamingProbeChannel>();
    public static void StopAll() { lock (owned) { foreach (var channel in owned) channel.Dispose(); } }
    public static bool IsScanning => Volatile.Read(ref scanning) != 0;

    public async Task<List<GamingProbeResult>> ScanAsync(GamingTarget target, IEnumerable<VpnProfile> profiles,
        string corePath, string runtime, bool includeDirect, IProgress<string> progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0) throw new InvalidOperationException("Проверка уже выполняется.");
        var channels = new List<GamingProbeChannel>();
        try {
            // Resolve once so every candidate reaches the same server, not different DNS-balanced addresses.
            var addresses = await WithTimeout(Dns.GetHostAddressesAsync(target.Host), token);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null) throw new IOException("Для этой проверки нужен IPv4-адрес сервера.");
            var endpoint = new IPEndPoint(ip, target.Port);
            if (includeDirect) channels.Add(new GamingProbeChannel(null, endpoint, target.A2S));
            var skipped = new List<GamingProbeResult>();
            foreach (var profile in profiles.Take(8)) {
                token.ThrowIfCancellationRequested();
                progress?.Report("Подготовка: " + profile.Name);
                try {
                    var channel = new GamingProbeChannel(profile, endpoint, target.A2S);
                    channels.Add(channel); // also dispose partially initialized channels
                    lock (owned) owned.Add(channel);
                    await channel.StartAsync(corePath, runtime, token);
                } catch (OperationCanceledException) { throw; }
                catch (Exception) {
                    var channel = channels.Last(); channels.RemoveAt(channels.Count - 1); channel.Dispose();
                    skipped.Add(new GamingProbeResult { ProfileId = profile.Id, Name = profile.Name, Error = "Измерительный профиль не запустился" });
                }
            }
            // Warm-up is never included. Interleave candidates to reduce temporal bias.
            foreach (var channel in channels) { try { await channel.MeasureAsync(token); } catch (OperationCanceledException) { throw; } catch { } }
            for (int round = 0; round < 10; round++) {
                foreach (var channel in (round % 2 == 0 ? channels : channels.AsEnumerable().Reverse()).ToList()) {
                    token.ThrowIfCancellationRequested();
                    progress?.Report("Серия " + (round + 1) + "/10 · " + channel.Result.Name);
                    channel.Result.Attempts++;
                    try { channel.Result.Samples.Add(await channel.MeasureAsync(token)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { channel.Result.Error = "Нет подтверждённого ответа цели (" + ex.GetType().Name + ")"; }
                    await Task.Delay(120, token);
                }
            }
            return channels.Select(c => c.Result).Concat(skipped).ToList();
        } finally {
            foreach (var channel in channels) channel.Dispose();
            lock (owned) owned.Clear();
            Interlocked.Exchange(ref scanning, 0);
        }
    }

    internal static async Task<T> WithTimeout<T>(Task<T> task, CancellationToken token, int milliseconds = 2500)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
            var delay = Task.Delay(milliseconds, timeout.Token);
            if (await Task.WhenAny(task, delay) != task) {
                // Observe eventual socket/DNS errors after the caller disposes its resources.
                _ = task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                token.ThrowIfCancellationRequested(); throw new TimeoutException();
            }
            timeout.Cancel(); return await task;
        }
    }
}

internal sealed class GamingProbeChannel : IDisposable
{
    private readonly VpnProfile profile;
    private readonly IPEndPoint target;
    private readonly bool a2s;
    private Process process;
    private string configFile;
    private int port;
    private string user, password;
    public readonly GamingProbeResult Result;
    private bool disposed;
    public GamingProbeChannel(VpnProfile profile, IPEndPoint target, bool a2s) {
        this.profile = profile; this.target = target; this.a2s = a2s;
        Result = new GamingProbeResult { ProfileId = profile?.Id, Name = profile?.Name ?? "Напрямую" };
    }

    public async Task StartAsync(string corePath, string runtime, CancellationToken token)
    {
        if (profile.Protocol == "sing-box") throw new NotSupportedException();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { port = ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
        user = Guid.NewGuid().ToString("N"); password = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(runtime);
        configFile = Path.Combine(runtime, "gaming-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(configFile, ConfigBuilder.BuildProbe(profile, port, user, password), new UTF8Encoding(false));
        process = new Process { StartInfo = new ProcessStartInfo(corePath, "run -c " + AppUpdateService.QuoteWindowsArgument(configFile)) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo = OwnedCoreProcess.Wrap(process.StartInfo, configFile);
        process.OutputDataReceived += (s, e) => { }; process.ErrorDataReceived += (s, e) => { };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        for (int i = 0; i < 40 && !process.HasExited; i++) {
            token.ThrowIfCancellationRequested();
            using (var client = new TcpClient()) {
                try { await client.ConnectAsync(IPAddress.Loopback, port); return; } catch (SocketException) { }
            }
            await Task.Delay(75, token);
        }
        throw new IOException("Probe start failed");
    }

    public async Task<double> MeasureAsync(CancellationToken token)
    {
        if (profile != null && (process == null || process.HasExited)) throw new IOException();
        return a2s ? await MeasureUdpAsync(token) : await MeasureTcpAsync(token);
    }

    private async Task<double> MeasureTcpAsync(CancellationToken token)
    {
        using (var client = new TcpClient()) {
            client.NoDelay = true;
            if (profile == null) {
                var clock = Stopwatch.StartNew();
                await ConnectAsync(client, target, token);
                return clock.Elapsed.TotalMilliseconds;
            }
            await ConnectAsync(client, new IPEndPoint(IPAddress.Loopback, port), token);
            var stream = client.GetStream(); await AuthenticateAsync(stream, token);
            var watch = Stopwatch.StartNew();
            await SendAsync(stream, Request(1, target), token);
            await ReadReplyAsync(stream, token);
            return watch.Elapsed.TotalMilliseconds;
        }
    }

    private async Task<double> MeasureUdpAsync(CancellationToken token)
    {
        using (var control = new TcpClient())
        using (var udp = new UdpClient(new IPEndPoint(profile == null ? IPAddress.Any : IPAddress.Loopback, 0))) {
            IPEndPoint relay = target;
            if (profile != null) {
                await ConnectAsync(control, new IPEndPoint(IPAddress.Loopback, port), token);
                var stream = control.GetStream(); await AuthenticateAsync(stream, token);
                await SendAsync(stream, Request(3, (IPEndPoint)udp.Client.LocalEndPoint), token);
                relay = await ReadReplyAsync(stream, token);
                if (relay.Address.Equals(IPAddress.Any)) relay.Address = IPAddress.Loopback;
                if (!IPAddress.IsLoopback(relay.Address)) throw new IOException("Unexpected UDP relay");
            }
            var query = new byte[] { 255,255,255,255,0x54 }.Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray();
            // A2S challenge is setup; exclude it from the reported query RTT.
            var response = await ExchangeAsync(udp, relay, query, token);
            if (response.Length == 9 && response.Take(4).All(b => b == 255) && response[4] == 0x41) query = query.Concat(response.Skip(5)).ToArray();
            else ValidateA2S(response);
            var clock = Stopwatch.StartNew();
            response = await ExchangeAsync(udp, relay, query, token);
            ValidateA2S(response);
            return clock.Elapsed.TotalMilliseconds;
        }
    }

    private async Task<byte[]> ExchangeAsync(UdpClient udp, IPEndPoint relay, byte[] payload, CancellationToken token)
    {
        var packet = profile == null ? payload : new byte[] { 0,0,0 }.Concat(Address(target)).Concat(payload).ToArray();
        udp.Connect(relay);
        await udp.SendAsync(packet, packet.Length);
        var result = await GamingProbeService.WithTimeout(udp.ReceiveAsync(), token);
        if (profile == null) return result.Buffer;
        var data = result.Buffer;
        if (data.Length < 10 || data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 1) throw new IOException("Invalid SOCKS UDP reply");
        if (!data.Skip(4).Take(4).SequenceEqual(target.Address.GetAddressBytes()) || ((data[8] << 8) | data[9]) != target.Port)
            throw new IOException("Unexpected UDP source");
        return data.Skip(10).ToArray();
    }

    internal static void ValidateA2S(byte[] bytes)
    {
        if (bytes.Length < 20 || !bytes.Take(4).All(b => b == 255) || bytes[4] != 0x49)
            throw new IOException("No complete A2S_INFO response");
        int offset = 6;
        for (int i = 0; i < 4; i++) {
            int end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < 0) throw new IOException("Truncated A2S string"); offset = end + 1;
        }
        if (offset + 9 >= bytes.Length || Array.IndexOf(bytes, (byte)0, offset + 9) < 0) throw new IOException("Truncated A2S info");
        // Split/compressed responses are unsupported, never treated as successful samples.
    }

    private async Task AuthenticateAsync(NetworkStream stream, CancellationToken token)
    {
        await SendAsync(stream, new byte[] { 5,1,2 }, token);
        var greeting = await ReadAsync(stream, 2, token);
        if (greeting[0] != 5 || greeting[1] != 2) throw new IOException("SOCKS authentication required");
        var u = Encoding.ASCII.GetBytes(user); var p = Encoding.ASCII.GetBytes(password);
        await SendAsync(stream, new byte[] { 1,(byte)u.Length }.Concat(u).Concat(new byte[] { (byte)p.Length }).Concat(p).ToArray(), token);
        var auth = await ReadAsync(stream, 2, token);
        if (auth[0] != 1 || auth[1] != 0) throw new IOException("SOCKS authentication failed");
    }
    private static byte[] Address(IPEndPoint endpoint) => new byte[] { 1 }.Concat(endpoint.Address.GetAddressBytes()).Concat(new byte[] { (byte)(endpoint.Port >> 8),(byte)endpoint.Port }).ToArray();
    private static byte[] Request(byte command, IPEndPoint endpoint) => new byte[] { 5,command,0 }.Concat(Address(endpoint)).ToArray();
    private static async Task<IPEndPoint> ReadReplyAsync(NetworkStream stream, CancellationToken token) {
        var header = await ReadAsync(stream, 4, token);
        if (header[0] != 5 || header[1] != 0 || header[2] != 0) throw new IOException("SOCKS connect failed");
        int size = header[3] == 1 ? 4 : header[3] == 4 ? 16 : throw new IOException("Unsupported SOCKS address");
        var address = await ReadAsync(stream, size, token); var port = await ReadAsync(stream, 2, token);
        return new IPEndPoint(new IPAddress(address), (port[0] << 8) | port[1]);
    }
    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken token) {
        var bytes = new byte[count]; int read = 0;
        while (read < count) {
            int n = await GamingProbeService.WithTimeout(stream.ReadAsync(bytes, read, count - read, token), token);
            if (n == 0) throw new EndOfStreamException(); read += n;
        }
        return bytes;
    }
    private static async Task SendAsync(NetworkStream stream, byte[] bytes, CancellationToken token) {
        await GamingProbeService.WithTimeout(WriteAsync(stream, bytes, token), token);
    }
    private static async Task<bool> WriteAsync(NetworkStream stream, byte[] bytes, CancellationToken token) { await stream.WriteAsync(bytes, 0, bytes.Length, token); return true; }
    private static async Task ConnectAsync(TcpClient client, IPEndPoint endpoint, CancellationToken token) {
        await GamingProbeService.WithTimeout(ConnectCoreAsync(client, endpoint), token);
    }
    private static async Task<bool> ConnectCoreAsync(TcpClient client, IPEndPoint endpoint) { await client.ConnectAsync(endpoint.Address, endpoint.Port); return true; }
    public void Dispose() {
        if (disposed) return; disposed = true;
        if (process != null) { try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { } process.Dispose(); }
        if (configFile != null) { try { File.Delete(configFile); } catch { } }
    }
}

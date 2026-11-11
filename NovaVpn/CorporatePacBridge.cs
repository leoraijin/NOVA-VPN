using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Intended for the owned core host, never a public/general-purpose proxy.
public sealed class CorporatePacBridge : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly SemaphoreSlim slots = new SemaphoreSlim(8, 8);
    private readonly ConcurrentDictionary<int, TcpClient> clients = new ConcurrentDictionary<int, TcpClient>();
    private readonly ConcurrentDictionary<int, Task> handlers = new ConcurrentDictionary<int, Task>();
    private readonly Func<CancellationToken, Task<CorporatePacInspection>> resolve;
    private readonly CorporatePacQuery query;
    private readonly bool allowDirect;
    private readonly byte[] authorization;
    private readonly Task accepting;
    private int sequence, disposed;
    public int Port { get; }
    public string Username => "nova";
    public string Password { get; }

    public CorporatePacBridge(CorporatePacQuery query, bool allowDirect)
        : this(query, allowDirect, token => CorporatePacResolver.InspectAsync(query, token)) { }

    // Resolver injection is for isolated fixtures; its result must still bind to the exact query/network.
    public CorporatePacBridge(CorporatePacQuery query, bool allowDirect, Func<CancellationToken, Task<CorporatePacInspection>> resolve)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        if (query.Target.AbsolutePath != "/" || !string.IsNullOrEmpty(query.Target.Query)) throw new ArgumentException("PAC bridge требует адрес TCP-сервера без пути.");
        this.allowDirect = allowDirect;
        var secret = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
        Password = Convert.ToBase64String(secret); Array.Clear(secret, 0, secret.Length);
        authorization = Encoding.ASCII.GetBytes("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(Username + ":" + Password)));
        listener.Start(16); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                if (!slots.Wait(0)) { client.Close(); continue; }
                int id = Interlocked.Increment(ref sequence); clients[id] = client;
                // Gate publication so a synchronously completing handler cannot leave a stale entry.
                var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Serve(id, client, published.Task); handlers[id] = task;
                _ = task.ContinueWith(completed => {
                    handlers.TryRemove(id, out _);
                    if (completed.IsFaulted) { _ = completed.Exception; stop.Cancel(); listener.Stop(); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                published.SetResult(true);
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested && (error is SocketException || error is ObjectDisposedException)) { }
    }

    private async Task Serve(int id, TcpClient client, Task published)
    {
        await published.ConfigureAwait(false);
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        using (lifetime.Token.Register(() => { try { client.Close(); } catch { } }))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                var stream = client.GetStream();
                var request = await ReadHeader(stream, lifetime.Token).ConfigureAwait(false);
                if (!Authorized(request)) { await Reply(stream, 407, lifetime.Token).ConfigureAwait(false); return; }
                if (!TargetMatches(request)) { await Reply(stream, 403, lifetime.Token).ConfigureAwait(false); return; }
                var result = await resolve(lifetime.Token).ConfigureAwait(false);
                if (result == null || !result.IsCurrent(query, ProfileHealthEvidence.NetworkKey(), DateTime.UtcNow))
                    throw new IOException("PAC bridge получил несогласованный результат.");
                using (var tunnel = await CorporatePacDialer.ConnectResolvedAsync(result, allowDirect, lifetime.Token).ConfigureAwait(false))
                {
                    await Reply(stream, 200, lifetime.Token).ConfigureAwait(false);
                    lifetime.CancelAfter(Timeout.Infinite);
                    var upload = Upload(stream, tunnel, lifetime.Token);
                    var download = Download(tunnel.Stream, stream, client, lifetime.Token);
                    var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
                    try
                    {
                        await first.ConfigureAwait(false);
                        // A clean EOF is not a failed tunnel. Allow the opposite direction
                        // to deliver its final response, but bound abandoned half-open peers.
                        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
                        await Task.WhenAll(upload, download).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException || error is SocketException) { }
                    finally
                    {
                        lifetime.Cancel();
                        try { await Task.WhenAll(upload, download).ConfigureAwait(false); }
                        catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException || error is SocketException) { }
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException ||
                error is OperationCanceledException || error is TimeoutException || error is InvalidOperationException || error is NotSupportedException)
            {
                // Never retry an established application stream or return a successful CONNECT after an error.
                // No raw request, PAC URL, credentials or exception text is logged.
            }
            finally
            {
                client.Close(); clients.TryRemove(id, out _); slots.Release();
            }
        }
    }

    private static async Task Upload(Stream incoming, CorporatePacTunnel tunnel, CancellationToken token)
    {
        await incoming.CopyToAsync(tunnel.Stream, 32768, token).ConfigureAwait(false);
        tunnel.CompleteSending();
    }
    private static async Task Download(Stream upstream, Stream incoming, TcpClient client, CancellationToken token)
    {
        await upstream.CopyToAsync(incoming, 32768, token).ConfigureAwait(false);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    private bool Authorized(string[] lines)
    {
        var values = lines.Skip(1).Where(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (values.Length != 1) return false;
        byte[] supplied = Encoding.ASCII.GetBytes(values[0].Substring(values[0].IndexOf(':') + 1).Trim());
        int difference = supplied.Length ^ authorization.Length;
        for (int index = 0; index < authorization.Length; index++) difference |= authorization[index] ^ (index < supplied.Length ? supplied[index] : 0);
        return difference == 0;
    }
    private bool TargetMatches(string[] lines)
    {
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0] != "CONNECT" || parts[2] != "HTTP/1.1" ||
            !parts[1].EndsWith(":" + query.Target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !Uri.TryCreate("http://" + parts[1] + "/", UriKind.Absolute, out var target)) return false;
        return string.IsNullOrEmpty(target.UserInfo) && string.IsNullOrEmpty(target.Query) && string.IsNullOrEmpty(target.Fragment) &&
            target.AbsolutePath == "/" && target.Port == query.Target.Port &&
            string.Equals(target.IdnHost, query.Target.IdnHost, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<string[]> ReadHeader(Stream stream, CancellationToken token)
    {
        using (var bytes = new MemoryStream())
        {
            var one = new byte[1]; int tail = 0;
            while (bytes.Length < 32768)
            {
                if (await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false) != 1) throw new IOException("Неполный CONNECT.");
                byte value = one[0];
                if (value > 126 || value < 32 && value != 9 && value != 10 && value != 13) throw new IOException("Некорректный CONNECT.");
                bytes.WriteByte(value); tail = (tail << 8) | value;
                if (tail == 0x0d0a0d0a) return Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            }
            throw new IOException("Слишком большой CONNECT.");
        }
    }
    private static Task Reply(Stream stream, int code, CancellationToken token)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(code == 200 ? "HTTP/1.1 200 Connection Established\r\n\r\n" :
            code == 407 ? "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"NOVA\"\r\nContent-Length: 0\r\n\r\n" :
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
        return stream.WriteAsync(bytes, 0, bytes.Length, token);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop(); foreach (var client in clients.Values) client.Close();
        if (!accepting.Wait(TimeSpan.FromSeconds(5))) throw new IOException("PAC bridge не завершил приём соединений.");
        if (!Task.WaitAll(handlers.Values.ToArray(), TimeSpan.FromSeconds(5))) throw new IOException("PAC bridge не завершил соединения.");
        stop.Dispose(); slots.Dispose(); Array.Clear(authorization, 0, authorization.Length);
    }
}

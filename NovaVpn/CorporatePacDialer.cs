using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Owns the socket and its cancellation registration for the entire tunnel lifetime.
public sealed class CorporatePacTunnel : IDisposable
{
    private readonly TcpClient socket;
    private readonly CancellationTokenRegistration cancellation;
    public Stream Stream { get; }
    public int Position { get; }
    public bool Direct { get; }
    internal CorporatePacTunnel(TcpClient socket, Stream stream, int position, bool direct, CancellationToken token)
    {
        this.socket = socket; Stream = stream; Position = position; Direct = direct;
        cancellation = token.Register(() => { try { socket.Close(); } catch { } });
    }
    public void Dispose()
    {
        cancellation.Dispose();
        try { Stream.Dispose(); } finally { socket.Close(); }
    }
    internal void CompleteSending()
    {
        // Framework SslStream has no independent TLS write-side shutdown API.
        // Keep its read side alive for a bounded drain instead of truncating it.
        if (!(Stream is SslStream)) socket.Client.Shutdown(SocketShutdown.Send);
    }
}

public static class CorporatePacDialer
{
    private static readonly SemaphoreSlim DnsSlots = new SemaphoreSlim(4, 4);
    public static async Task<CorporatePacTunnel> ConnectAsync(CorporatePacQuery query, bool allowDirect, CancellationToken token)
    {
        var inspection = await CorporatePacResolver.InspectAsync(query, token).ConfigureAwait(false);
        return await ConnectResolvedAsync(inspection, allowDirect, token).ConfigureAwait(false);
    }

    // Explicit resolved input also permits isolated tests without executing arbitrary PAC.
    public static async Task<CorporatePacTunnel> ConnectResolvedAsync(CorporatePacInspection inspection, bool allowDirect, CancellationToken token)
    {
        if (inspection == null) throw new ArgumentNullException(nameof(inspection));
        if (inspection.Query.Target.AbsolutePath != "/" || !string.IsNullOrEmpty(inspection.Query.Target.Query))
            throw new ArgumentException("PAC runtime принимает только TCP-адрес сервера, без пути и параметров.");
        if (inspection.Entries.Any(e => e.IsProxy && e.NativeScheme != 1 && e.NativeScheme != 2))
            throw new NotSupportedException("PAC содержит неподдерживаемый тип прокси. Версия SOCKS должна быть известна явно.");
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            foreach (var entry in inspection.Entries)
            {
                token.ThrowIfCancellationRequested(); deadline.Token.ThrowIfCancellationRequested();
                if (!inspection.IsCurrent(inspection.Query, ProfileHealthEvidence.NetworkKey(), DateTime.UtcNow))
                    throw new InvalidOperationException("Результат PAC устарел или физическая сеть изменилась.");
                if (!entry.IsProxy && !allowDirect) continue;
                TcpClient socket = null; Stream stream = null;
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        string endpoint = entry.IsProxy ? entry.Server : inspection.Query.Target.DnsSafeHost;
                        socket = await ConnectEndpoint(endpoint, entry.IsProxy ? entry.Port : inspection.Query.Target.Port, attempt.Token).ConfigureAwait(false);
                        var owned = socket;
                        using (attempt.Token.Register(() => { try { owned.Close(); } catch { } }))
                        {
                            attempt.Token.ThrowIfCancellationRequested();
                            stream = socket.GetStream();
                            if (entry.IsProxy && entry.NativeScheme == 2)
                            {
                                var tls = new SslStream(stream, false); stream = tls;
                                await tls.AuthenticateAsClientAsync(entry.Server, null, SslProtocols.Tls12, true).ConfigureAwait(false);
                            }
                            if (entry.IsProxy)
                            {
                                string host = inspection.Query.Target.IdnHost;
                                if (inspection.Query.Target.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
                                // Uri.Authority omits a default port; CONNECT must always carry it.
                                string authority = host + ":" + inspection.Query.Target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                byte[] request = Encoding.ASCII.GetBytes("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\n");
                                await stream.WriteAsync(request, 0, request.Length, attempt.Token).ConfigureAwait(false);
                                await AcceptConnect(stream, attempt.Token).ConfigureAwait(false);
                            }
                            attempt.Token.ThrowIfCancellationRequested();
                            if (!inspection.IsCurrent(inspection.Query, ProfileHealthEvidence.NetworkKey(), DateTime.UtcNow))
                                throw new InvalidOperationException("Сеть изменилась во время соединения с прокси.");
                            var result = new CorporatePacTunnel(socket, stream, entry.Position, !entry.IsProxy, token);
                            socket = null; stream = null;
                            return result;
                        }
                    }
                    catch (Exception error) when (error is IOException || error is SocketException || error is AuthenticationException ||
                        error is ObjectDisposedException || error is OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested(); deadline.Token.ThrowIfCancellationRequested();
                        // Failover is only before returning a tunnel: no application bytes have been sent.
                        // No credentials are sent to PAC-supplied hosts, and no implicit direct attempt exists.
                    }
                    finally { try { stream?.Dispose(); } finally { socket?.Close(); } }
                }
            }
        }
        throw new IOException("Ни один разрешённый PAC-маршрут не подключился. Прямой обход не выполнялся без разрешения.");
    }

    private static async Task<IPAddress[]> Resolve(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
        await DnsSlots.WaitAsync(token).ConfigureAwait(false);
        Task<IPAddress[]> pending;
        try { pending = Dns.GetHostAddressesAsync(host); }
        catch { DnsSlots.Release(); throw; }
        // Framework DNS cannot be cancelled. Keep capacity until the OS operation
        // actually ends, even when this caller's bounded wait has been cancelled.
        _ = pending.ContinueWith(completed => {
            if (completed.IsFaulted) _ = completed.Exception;
            DnsSlots.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await pending.ConfigureAwait(false);
        }
    }
    private static async Task<TcpClient> ConnectEndpoint(string host, int port, CancellationToken token)
    {
        var addresses = (await Resolve(host, token).ConfigureAwait(false))
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6)
            .Distinct().Take(16).ToArray();
        foreach (var address in addresses)
        using (var dial = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if (addresses.Length > 1) dial.CancelAfter(TimeSpan.FromSeconds(2));
            TcpClient client = null;
            try
            {
                client = new TcpClient(address.AddressFamily); var owned = client;
                using (dial.Token.Register(() => { try { owned.Close(); } catch { } }))
                {
                    await client.ConnectAsync(address, port).ConfigureAwait(false);
                    dial.Token.ThrowIfCancellationRequested();
                    var result = client; client = null; return result;
                }
            }
            catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                // Alternate addresses belong to this same proxy/target. This is
                // never permission to advance to DIRECT or to replay payload.
            }
            finally { client?.Close(); }
        }
        throw new IOException("Не удалось подключиться ни к одному адресу PAC-кандидата.");
    }

    private static async Task AcceptConnect(Stream stream, CancellationToken token)
    {
        int total = 0;
        for (int interim = 0; interim <= 5; interim++)
        {
            using (var header = new MemoryStream())
            {
                var one = new byte[1]; int tail = 0;
                while (true)
                {
                    if (++total > 32768) throw new IOException("Слишком большой ответ CONNECT.");
                    if (await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false) != 1) throw new IOException("Прокси закрыл CONNECT.");
                    byte value = one[0];
                    if (value < 32 && value != 9 && value != 10 && value != 13) throw new IOException("Некорректный ответ CONNECT.");
                    header.WriteByte(value); tail = (tail << 8) | value;
                    if (tail == 0x0d0a0d0a) break;
                }
                string first = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                var parts = first.Split(' ');
                if (parts.Length < 2 || parts[0] != "HTTP/1.1" && parts[0] != "HTTP/1.0" || parts[1].Length != 3 ||
                    !parts[1].All(c => c >= '0' && c <= '9') || !int.TryParse(parts[1], out int code)) throw new IOException("Некорректный статус CONNECT.");
                if (code >= 200 && code < 300) return;
                if (code < 100 || code >= 200 || code == 101) throw new IOException("Прокси отклонил CONNECT.");
            }
        }
        throw new IOException("Слишком много промежуточных ответов CONNECT.");
    }
}

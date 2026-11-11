using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class ServiceCheckTarget
{
    public string Name { get; }
    public Uri Address { get; }
    public ServiceCheckTarget(string name, string address)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Название сервиса: от 1 до 80 символов.");
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048 || address.Any(char.IsControl) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Укажите HTTPS-адрес без логина, пароля и фрагмента #.");
        Name = name.Trim(); Address = uri;
    }
    public static IReadOnlyList<ServiceCheckTarget> Parse(string text)
    {
        if (text == null || text.Length > 18000) throw new ArgumentException("Список адресов слишком велик.");
        var targets = new List<ServiceCheckTarget>();
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            int separator = line.IndexOf('|'); string address = (separator < 0 ? line : line.Substring(separator + 1)).Trim();
            string name = separator < 0 && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : separator < 0 ? "Сервис" : line.Substring(0, separator);
            targets.Add(new ServiceCheckTarget(name, address));
        }
        Validate(targets); return targets.AsReadOnly();
    }
    internal static void Validate(IReadOnlyList<ServiceCheckTarget> targets)
    {
        if (targets == null || targets.Count < 1 || targets.Count > ServiceMatrixRunner.MaximumTargets || targets.Any(t => t == null) || targets.Select(t => t.Address.AbsoluteUri).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new ArgumentException("Выберите от 1 до 8 разных HTTPS-адресов.");
    }
}

public enum ServiceCheckKind { Response, Redirect, AuthenticationRequired, Refused, Throttled, HttpError, NetworkError, Unsupported }

public sealed class ServiceCheckResult
{
    public string ProfileId { get; internal set; }
    public string ProfileName { get; internal set; }
    public ServiceCheckTarget Target { get; internal set; }
    public ServiceCheckKind Kind { get; internal set; }
    public int HttpCode { get; internal set; }
    public long ElapsedMilliseconds { get; internal set; } = -1;
    public DateTime CheckedUtc { get; internal set; }
    public string Message { get; internal set; }
    public string ProfileStamp { get; internal set; }
    public string NetworkStamp { get; internal set; }
    public string CoreStamp { get; internal set; }
    public bool IsCurrent(VpnProfile profile, DateTime now, string network, string core) => profile != null && !profile.RetiredFromSubscription && profile.Id == ProfileId &&
        now >= CheckedUtc && now - CheckedUtc < TimeSpan.FromMinutes(3) && !string.IsNullOrEmpty(network) && !string.IsNullOrEmpty(core) &&
        network == NetworkStamp && core == CoreStamp && ServiceMatrixRunner.ProfileStamp(profile) == ProfileStamp;
    public string Label => HttpCode > 0 ? "HTTP " + HttpCode + (ElapsedMilliseconds >= 0 ? " · " + ElapsedMilliseconds + " мс" : "") : Message;
}

// No TUN or client state writes. Each row owns an authenticated loopback proxy
// with one assigned outbound; pending and live checks share a bounded lifetime.
public sealed class ServiceMatrixRunner : IDisposable
{
    public const int MaximumProfiles = 32, MaximumTargets = 8;
    private static readonly SemaphoreSlim Slots = new SemaphoreSlim(2, 2);
    private readonly object sync = new object();
    private readonly Dictionary<LocalProxyManager, string> active = new Dictionary<LocalProxyManager, string>();
    private CancellationTokenSource cancellation;
    private Task<IReadOnlyList<ServiceCheckResult>> running;
    private bool disposed;
    public bool HasActive { get { lock (sync) return active.Count > 0; } }
    public bool IsRunning { get { lock (sync) return running != null && !running.IsCompleted; } }

    public Task<IReadOnlyList<ServiceCheckResult>> RunAsync(IEnumerable<VpnProfile> profiles, IEnumerable<ServiceCheckTarget> targets, string corePath, string runtime,
        Action<ServiceCheckResult> progress, CancellationToken token)
    {
        if (profiles == null || targets == null) throw new ArgumentNullException();
        var selected = profiles.Take(MaximumProfiles + 1).ToArray(); var checks = targets.Take(MaximumTargets + 1).ToArray(); ServiceCheckTarget.Validate(checks);
        if (selected.Length < 1 || selected.Length > MaximumProfiles || selected.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 100) || selected.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Выберите от 1 до 32 разных профилей.");
        var serializer = new DataContractSerializer(typeof(VpnProfile));
        var frozen = selected.Select(profile => { using (var stream = new MemoryStream()) { serializer.WriteObject(stream, profile); stream.Position = 0; return (VpnProfile)serializer.ReadObject(stream); } }).ToArray();
        runtime = Path.GetFullPath(runtime);
        string network = ProfileHealthEvidence.NetworkKey(), core = ProfileHealthEvidence.CoreKey(corePath);
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ServiceMatrixRunner));
            if (running != null && !running.IsCompleted || active.Count > 0) throw new InvalidOperationException("Завершите предыдущую проверку и остановку её процессов.");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            running = RunCoreAsync(frozen, checks, corePath, runtime, network, core, progress, cancellation);
            return running;
        }
    }
    private async Task<IReadOnlyList<ServiceCheckResult>> RunCoreAsync(VpnProfile[] profiles, ServiceCheckTarget[] targets, string core, string runtime, string networkStamp, string coreStamp,
        Action<ServiceCheckResult> progress, CancellationTokenSource run)
    {
        try
        {
            var rows = await Task.WhenAll(profiles.Select(profile => RowAsync(profile, targets, core, runtime, networkStamp, coreStamp, progress, run))).ConfigureAwait(false);
            return Array.AsReadOnly(rows.SelectMany(row => row).ToArray());
        }
        finally { lock (sync) { if (ReferenceEquals(cancellation, run)) cancellation = null; } run.Dispose(); }
    }
    private async Task<ServiceCheckResult[]> RowAsync(VpnProfile profile, ServiceCheckTarget[] targets, string corePath, string runtime, string network, string core,
        Action<ServiceCheckResult> progress, CancellationTokenSource run)
    {
        var token = run.Token; await Slots.WaitAsync(token).ConfigureAwait(false);
        var results = new List<ServiceCheckResult>(); LocalProxyManager manager = null; string id = null; bool retainedSlot = false;
        try
        {
            string unsupported = profile.RetiredFromSubscription ? "Профиль удалён из подписки" : profile.Protocol == "sing-box" ? "Полный конфиг нельзя изолированно проверить" : !File.Exists(corePath) ? "Не найдено VPN-ядро" : null;
            if (unsupported != null)
            {
                foreach (var target in targets) Publish(profile, target, ServiceCheckKind.Unsupported, unsupported, 0, -1, network, core, results, progress);
                return results.ToArray();
            }
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port;
            try { port = ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
            var endpoint = new LocalProxyEndpoint { ProfileId = profile.Id, Port = port, InboundType = "http", Name = "Проверка HTTPS" }; id = endpoint.Id;
            manager = new LocalProxyManager(runtime); lock (sync) active.Add(manager, id);
            try
            {
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token)) { startup.CancelAfter(TimeSpan.FromSeconds(18)); await manager.StartAsync(endpoint, profile, corePath, startup.Token).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                foreach (var target in targets) Publish(profile, target, ServiceCheckKind.NetworkError, "Ядро не запустилось за отведённое время", 0, -1, network, core, results, progress);
                return results.ToArray();
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                foreach (var target in targets) Publish(profile, target, ServiceCheckKind.Unsupported, "Не удалось создать изолированную проверку профиля", 0, -1, network, core, results, progress);
                return results.ToArray();
            }
                foreach (var target in targets)
                {
                    token.ThrowIfCancellationRequested();
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(8));
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            int code = await ReadHttpsStatusAsync(endpoint, target.Address, deadline.Token).ConfigureAwait(false);
                            Publish(profile, target, Classify(code), "Получен HTTPS-ответ", code, watch.ElapsedMilliseconds, network, core, results, progress);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { Publish(profile, target, ServiceCheckKind.NetworkError, "Тайм-аут HTTPS-запроса", 0, -1, network, core, results, progress); }
                        catch (Exception error) when (error is IOException || error is SocketException || error is AuthenticationException || error is ObjectDisposedException)
                        { token.ThrowIfCancellationRequested(); Publish(profile, target, ServiceCheckKind.NetworkError, deadline.IsCancellationRequested ? "Тайм-аут HTTPS-запроса" : "HTTPS-ответ не получен", 0, -1, network, core, results, progress); }
                    }
                }
            return results.ToArray();
        }
        finally
        {
            try
            {
                if (manager != null)
                {
                    try { await manager.StopAsync(id).ConfigureAwait(false); manager.Dispose(); lock (sync) active.Remove(manager); }
                    catch { retainedSlot = true; try { run.Cancel(); } catch (ObjectDisposedException) { } throw new IOException("Остановка процесса проверки не подтверждена. Повторите остановку."); }
                }
            }
            finally { if (!retainedSlot) Slots.Release(); }
        }
    }
    public static ServiceCheckKind Classify(int status) => status >= 200 && status <= 299 ? ServiceCheckKind.Response : status >= 300 && status <= 399 ? ServiceCheckKind.Redirect :
        status == 401 ? ServiceCheckKind.AuthenticationRequired : status == 403 ? ServiceCheckKind.Refused : status == 429 ? ServiceCheckKind.Throttled : ServiceCheckKind.HttpError;
    // Connect only to the owned authenticated proxy. Windows HTTP handlers can
    // bypass proxies for loopback destinations, even with a custom IWebProxy.
    internal static async Task<int> ReadHttpsStatusAsync(LocalProxyEndpoint endpoint, Uri target, CancellationToken token)
    {
        using (var socket = new TcpClient())
        using (token.Register(() => socket.Close()))
        {
            await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            var stream = socket.GetStream();
            string host = target.IdnHost.Trim('[', ']'), authority = (target.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host) + ":" + target.Port;
            string authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(endpoint.Username + ":" + endpoint.Password));
            byte[] connect = Encoding.ASCII.GetBytes("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\nProxy-Authorization: Basic " + authorization + "\r\n\r\n");
            await stream.WriteAsync(connect, 0, connect.Length, token).ConfigureAwait(false);
            if (await ReadStatusAsync(stream, token).ConfigureAwait(false) != 200) throw new IOException("Proxy did not establish the assigned tunnel.");
            // Default certificate validation is deliberately retained. No cookies,
            // account credentials, redirects or global certificate callbacks.
            using (var tls = new SslStream(stream, false))
            {
                await tls.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, false).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                byte[] request = Encoding.ASCII.GetBytes("GET " + target.PathAndQuery + " HTTP/1.1\r\nHost: " + authority + "\r\nConnection: close\r\nCache-Control: no-cache, no-store\r\n\r\n");
                await tls.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);
                for (int interim = 0; interim < 5; interim++)
                {
                    int code = await ReadStatusAsync(tls, token).ConfigureAwait(false);
                    if (code >= 200 || code == 101) return code;
                }
                throw new IOException("Too many interim responses.");
            }
        }
    }
    private static async Task<int> ReadStatusAsync(Stream stream, CancellationToken token)
    {
        var header = new StringBuilder(); var single = new byte[1];
        while (header.Length < 32768)
        {
            int read = await stream.ReadAsync(single, 0, 1, token).ConfigureAwait(false); if (read == 0) throw new IOException("Incomplete HTTP headers.");
            header.Append((char)single[0]); int n = header.Length;
            if (n < 4 || header[n - 4] != '\r' || header[n - 3] != '\n' || header[n - 2] != '\r' || header[n - 1] != '\n') continue;
            string first = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            if (first.Length < 12 || !first.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && !first.StartsWith("HTTP/1.0 ", StringComparison.Ordinal) ||
                first.Length > 12 && first[12] != ' ' || !int.TryParse(first.Substring(9, 3), out int code) || code < 100 || code > 599) throw new IOException("Invalid HTTP status line.");
            return code;
        }
        throw new IOException("HTTP headers too large.");
    }
    public static string ProfileStamp(VpnProfile profile)
    {
        if (profile == null) return "";
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(SubscriptionManager.ConfigKey(profile))));
    }
    private static void Publish(VpnProfile profile, ServiceCheckTarget target, ServiceCheckKind kind, string message, int code, long elapsed, string network, string core,
        List<ServiceCheckResult> results, Action<ServiceCheckResult> progress)
    {
        var result = new ServiceCheckResult { ProfileId = profile.Id, ProfileName = profile.Name, Target = target, Kind = kind, Message = message, HttpCode = code, ElapsedMilliseconds = elapsed,
            CheckedUtc = DateTime.UtcNow, ProfileStamp = ProfileStamp(profile), NetworkStamp = network, CoreStamp = core };
        results.Add(result); try { progress?.Invoke(result); } catch { /* UI teardown cannot prevent owned process cleanup. */ }
    }
    public async Task StopAsync()
    {
        CancellationTokenSource cancel; Task task; lock (sync) { cancel = cancellation; task = running; }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
        if (task != null) { try { await task.ConfigureAwait(false); } catch { } }
        KeyValuePair<LocalProxyManager, string>[] remaining; lock (sync) remaining = active.ToArray();
        var errors = new List<Exception>();
        foreach (var entry in remaining)
            try
            {
                await entry.Key.StopAsync(entry.Value).ConfigureAwait(false); entry.Key.Dispose();
                bool removed; lock (sync) removed = active.Remove(entry.Key);
                if (removed) Slots.Release(); // A failed row retained this slot until confirmed cleanup.
            }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Не все процессы проверки подтвердили остановку.", errors);
    }
    public void Dispose() { lock (sync) disposed = true; StopAsync().GetAwaiter().GetResult(); }
}

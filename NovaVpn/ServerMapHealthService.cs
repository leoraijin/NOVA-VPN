using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class ServerMapHealth
{
	public bool Success;
	public bool Supported = true;
	public int LatencyMs;
	public DateTime CheckedUtc;
	public string Message = "Не проверен";
    public string ProfileKey = "", NetworkKey = "", CoreKey = "";
    public string Target = "";
    internal ServerMapHealth Copy() => (ServerMapHealth)MemberwiseClone();
	public bool IsFresh => CheckedUtc != default(DateTime) && DateTime.UtcNow >= CheckedUtc && DateTime.UtcNow - CheckedUtc < TimeSpan.FromMinutes(3);
    public bool IsFreshFor(VpnProfile profile) => IsFresh && profile != null && ProfileKey == SubscriptionManager.ConfigKey(profile);
	public string Label => !Supported ? Message : !IsFresh ? "Нужна свежая проверка" :
		(Success ? "HTTPS-отклик · " + LatencyMs + " мс" : Message) + " · " + Math.Max(0, (int)(DateTime.UtcNow - CheckedUtc).TotalSeconds) + " с назад";
}

public static class ServerMapHealthService
{
	private static readonly SemaphoreSlim ProbeSlots = new SemaphoreSlim(2);
    private static readonly object CatalogLock = new object();
    private static readonly Dictionary<Tuple<string, string, string>, CatalogProbe> CatalogProbes =
        new Dictionary<Tuple<string, string, string>, CatalogProbe>();

    private sealed class CatalogProbe
    {
        public readonly CancellationTokenSource Stop = new CancellationTokenSource();
        public Task<ServerMapHealth> Work;
        public int Waiters;
    }

    private static readonly Dictionary<(string Core, string Runtime), List<PendingProbe>> PendingCatalog =
        new Dictionary<(string, string), List<PendingProbe>>();

    private sealed class PendingProbe
    {
        public VpnProfile Profile;
        public CancellationToken Token;
        public Tuple<string, string, string> Key;
        public readonly TaskCompletionSource<ServerMapHealth> Completion =
            new TaskCompletionSource<ServerMapHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // CatalogLock is held by the caller so each queued task has one cache owner.
    private static Task<ServerMapHealth> QueueCatalogProbe(VpnProfile profile, string corePath, string runtime,
        CancellationToken token, Tuple<string, string, string> key)
    {
        if (profile.Protocol == "sing-box" || ConfigBuilder.RequiresXrayTransport(profile) || CorporateProxy.Enabled(profile))
            return Task.Run(() => TestStampedAsync(profile, corePath, runtime, token, key));

        var location = (Core: corePath, Runtime: runtime);
        if (!PendingCatalog.TryGetValue(location, out var batch) || batch.Count == 8)
        {
            batch = new List<PendingProbe>();
            PendingCatalog[location] = batch;
            _ = FlushCatalogAsync(location, batch);
        }
        var probe = new PendingProbe { Profile = profile, Token = token, Key = key };
        batch.Add(probe);
        return probe.Completion.Task;
    }

    private static async Task FlushCatalogAsync((string Core, string Runtime) location, List<PendingProbe> queued)
    {
        await Task.Delay(35).ConfigureAwait(false);
        lock (CatalogLock)
        {
            if (PendingCatalog.TryGetValue(location, out var current) && ReferenceEquals(current, queued))
                PendingCatalog.Remove(location);
        }

        var active = queued.Where(probe => !probe.Token.IsCancellationRequested).ToArray();
        foreach (var probe in queued.Except(active)) probe.Completion.TrySetCanceled();
        if (active.Length == 0) return;
        if (active.Length > 1)
        {
            try
            {
                var results = await RunCatalogBatchAsync(location.Core, location.Runtime, active).ConfigureAwait(false);
                for (int i = 0; i < active.Length; i++)
                {
                    var probe = active[i];
                    if (probe.Token.IsCancellationRequested) probe.Completion.TrySetCanceled();
                    else probe.Completion.TrySetResult(results[i]);
                }
                return;
            }
            catch (Exception ex) { Debug.WriteLine("Catalog batch: " + ex.GetType().Name); }
        }

        // One incompatible config or a failed shared process must not hide healthy profiles.
        await Task.WhenAll(active.Select(async probe =>
        {
            try
            {
                var result = await TestStampedAsync(probe.Profile, location.Core, location.Runtime, probe.Token, probe.Key).ConfigureAwait(false);
                probe.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { probe.Completion.TrySetCanceled(); }
            catch (Exception ex) { probe.Completion.TrySetException(ex); }
        })).ConfigureAwait(false);
    }

    private static async Task<ServerMapHealth[]> RunCatalogBatchAsync(string corePath, string runtime, PendingProbe[] probes)
    {
        using (var stop = new CancellationTokenSource())
        {
            var registrations = probes.Select(probe => probe.Token.Register(() =>
            {
                if (probes.All(item => item.Token.IsCancellationRequested)) stop.Cancel();
            })).ToArray();
            try
            {
                await ProbeSlots.WaitAsync(stop.Token).ConfigureAwait(false);
                try { return await RunCatalogProcessAsync(corePath, runtime, probes, stop.Token).ConfigureAwait(false); }
                finally { ProbeSlots.Release(); }
            }
            finally { foreach (var registration in registrations) registration.Dispose(); }
        }
    }

    private static async Task<ServerMapHealth[]> RunCatalogProcessAsync(string corePath, string runtime,
        PendingProbe[] probes, CancellationToken token)
    {
        var listeners = new List<TcpListener>();
        var endpoints = new List<(VpnProfile Profile, int Port, string User, string Password)>();
        try
        {
            foreach (var probe in probes)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listeners.Add(listener);
                listener.Start();
                endpoints.Add((probe.Profile, ((IPEndPoint)listener.LocalEndpoint).Port,
                    Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")));
            }
        }
        finally { foreach (var listener in listeners) listener.Stop(); }

        token.ThrowIfCancellationRequested();
        string file = PrivateRuntimeFiles.WriteCoreConfig(runtime, ConfigBuilder.BuildCatalogProbeBatch(endpoints));
        Process process = null;
        try
        {
            process = new Process { StartInfo = OwnedCoreProcess.Wrap(new ProcessStartInfo(corePath, "run -c \"" + file + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }, file) };
            process.OutputDataReceived += (s, e) => { };
            process.ErrorDataReceived += (s, e) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await Task.WhenAll(endpoints.Select(endpoint => WaitForListenerAsync(process, endpoint.Port, deadline.Token))).ConfigureAwait(false);
                return await Task.WhenAll(endpoints.Select(async (endpoint, index) =>
                {
                    var probe = probes[index];
                    var result = new ServerMapHealth();
                    using (var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, probe.Token))
                    {
                        try { await MeasureProxyAsync(endpoint.Port, endpoint.User, endpoint.Password, result, request.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { result.Message = "HTTPS: тайм-аут"; }
                        catch (HttpRequestException) { result.Message = "HTTPS через профиль не прошёл"; }
                        catch (IOException) { result.Message = "HTTPS через профиль не прошёл"; }
                    }
                    result.ProfileKey = probe.Key.Item1; result.NetworkKey = probe.Key.Item2; result.CoreKey = probe.Key.Item3;
                    result.CheckedUtc = DateTime.UtcNow;
                    return result;
                })).ConfigureAwait(false);
            }
        }
        finally
        {
            if (process != null) { try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { } process.Dispose(); }
            try { File.Delete(file); } catch { }
        }
    }

    private static async Task WaitForListenerAsync(Process process, int port, CancellationToken token)
    {
        for (int i = 0; i < 40 && !process.HasExited; i++)
        {
            token.ThrowIfCancellationRequested();
            using (var socket = new TcpClient())
            {
                try { await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false); return; }
                catch (SocketException) { }
            }
            await Task.Delay(75, token).ConfigureAwait(false);
        }
        throw new IOException("Probe failed to start");
    }

    internal static async Task<ServerMapHealth> TestCatalogAsync(VpnProfile profile, string corePath,
        string runtimeDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (profile == null) return await TestAsync(profile, corePath, runtimeDirectory, token).ConfigureAwait(false);
        var key = Tuple.Create(SubscriptionManager.ConfigKey(profile), ProfileHealthEvidence.NetworkKey(),
            ProfileHealthEvidence.CoreKey(corePath));
        if (string.IsNullOrEmpty(key.Item2) || string.IsNullOrEmpty(key.Item3))
            return await TestAsync(profile, corePath, runtimeDirectory, token).ConfigureAwait(false);

        CatalogProbe entry;
        lock (CatalogLock)
        {
            if (CatalogProbes.TryGetValue(key, out entry) && entry.Work.IsCompleted &&
                (entry.Work.Status != TaskStatus.RanToCompletion || !entry.Work.Result.Success || !entry.Work.Result.IsFresh ||
                 DateTime.UtcNow - entry.Work.Result.CheckedUtc >= TimeSpan.FromSeconds(150)))
            {
                CatalogProbes.Remove(key);
                entry = null;
            }
            if (entry == null)
            {
                if (CatalogProbes.Count >= 2048)
                {
                    foreach (var expired in CatalogProbes.Where(pair => pair.Value.Work.IsCompleted).Select(pair => pair.Key).ToArray())
                        CatalogProbes.Remove(expired);
                }
                entry = new CatalogProbe();
                var owner = entry;
                entry.Work = QueueCatalogProbe(profile, corePath, runtimeDirectory, owner.Stop.Token, key);
                CatalogProbes.Add(key, entry);
                entry.Work.ContinueWith(_ =>
                {
                    lock (CatalogLock) owner.Stop.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            entry.Waiters++;
        }
        try
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                await Task.WhenAny(entry.Work, cancelled.Task).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var result = await entry.Work.ConfigureAwait(false);
                if (result.ProfileKey != key.Item1 || result.NetworkKey != key.Item2 || result.CoreKey != key.Item3 ||
                    !MatchesCurrentProfile(profile, corePath, key))
                    return new ServerMapHealth { Message = "Параметры проверки изменились. Повторите проверку.", CheckedUtc = DateTime.UtcNow };
                if (profile.LastCheckedUtc < result.CheckedUtc) ProfileHealthEvidence.Record(profile, result);
                // Callers can annotate their display result without changing the shared sample.
                return result.Copy();
            }
        }
        finally
        {
            lock (CatalogLock)
            {
                if (--entry.Waiters == 0 && !entry.Work.IsCompleted)
                {
                    if (CatalogProbes.TryGetValue(key, out var latest) && ReferenceEquals(entry, latest))
                        CatalogProbes.Remove(key);
                    entry.Stop.Cancel();
                }
            }
        }
    }

	// The probe has no TUN, no direct outbound, and no proxy bypass/fallback.
	public static Task<ServerMapHealth> TestAsync(VpnProfile profile, string corePath, string runtimeDirectory, CancellationToken token)
	{
		return TestStampedAsync(profile, corePath, runtimeDirectory, token, null);
	}

	private static async Task<ServerMapHealth> TestStampedAsync(VpnProfile profile, string corePath, string runtimeDirectory,
		CancellationToken token, Tuple<string, string, string> catalogKey)
	{
		await ProbeSlots.WaitAsync(token).ConfigureAwait(false);
		try
		{
			var key = catalogKey ?? Tuple.Create(profile == null ? "" : SubscriptionManager.ConfigKey(profile),
				ProfileHealthEvidence.NetworkKey(), ProfileHealthEvidence.CoreKey(corePath));
			var result = await TestCoreAsync(profile, corePath, runtimeDirectory, token).ConfigureAwait(false);
			token.ThrowIfCancellationRequested();
			if (profile != null && !MatchesCurrentProfile(profile, corePath, key))
				return new ServerMapHealth { Message = "Профиль, ядро или сеть изменились во время проверки. Повторите проверку.", CheckedUtc = DateTime.UtcNow };
			result.ProfileKey = key.Item1; result.NetworkKey = key.Item2; result.CoreKey = key.Item3;
			result.CheckedUtc = DateTime.UtcNow;
			if (catalogKey == null) ProfileHealthEvidence.Record(profile, result);
			return result;
		}
		finally { ProbeSlots.Release(); }
	}

	private static bool MatchesCurrentProfile(VpnProfile profile, string corePath, Tuple<string, string, string> key)
	{
		return SubscriptionManager.ConfigKey(profile) == key.Item1 &&
			ProfileHealthEvidence.NetworkKey() == key.Item2 && ProfileHealthEvidence.CoreKey(corePath) == key.Item3;
	}

	private static async Task<ServerMapHealth> TestCoreAsync(VpnProfile profile, string corePath, string runtimeDirectory, CancellationToken token)
	{
		var result = new ServerMapHealth();
		if (profile == null || profile.Protocol == "sing-box")
			return new ServerMapHealth { Supported = false, Message = "Полный конфиг: проверка при подключении" };
        if (ConfigBuilder.RequiresXrayTransport(profile)) return new ServerMapHealth { Supported = false, Message = "XHTTP требует ядро Xray" };
		if (!File.Exists(corePath)) return new ServerMapHealth { Supported = false, Message = "Нужно установить VPN-ядро" };
		string file = null, pacDescriptor = null;
		Process process = null;
		try
		{
			token.ThrowIfCancellationRequested();
			int port;
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try { port = ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
			string user = Guid.NewGuid().ToString("N"), password = Guid.NewGuid().ToString("N");
            string prepared = ConfigBuilder.BuildProbe(profile, port, user, password);
            var pacPlan = CorporatePacPlan.Freeze(profile, prepared);
			file = PrivateRuntimeFiles.WriteCoreConfig(runtimeDirectory, prepared);
            pacDescriptor = pacPlan?.Prepare(file);
			process = new Process { StartInfo = new ProcessStartInfo(corePath, "run -c \"" + file + "\"") {
				UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
			process.OutputDataReceived += (s, e) => { };
            process.StartInfo = OwnedCoreProcess.Wrap(process.StartInfo, file, pacDescriptorFile: pacDescriptor);
			process.ErrorDataReceived += (s, e) => { };
			process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
			using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
			{
				deadline.CancelAfter(TimeSpan.FromSeconds(30));
				await WaitForListenerAsync(process, port, deadline.Token).ConfigureAwait(false);
				await MeasureProxyAsync(port, user, password, result, deadline.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; result.Message = "HTTPS: тайм-аут"; }
		catch (Exception) { result.Message = "HTTPS через профиль не прошёл"; }
		finally
		{
			if (process != null) { try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { } process.Dispose(); }
			if (file != null) { try { File.Delete(file); } catch { } }
            if (pacDescriptor != null) { try { File.Delete(pacDescriptor); } catch { } }
		}
		result.CheckedUtc = DateTime.UtcNow;
		return result;
	}

	private static async Task MeasureProxyAsync(int port, string user, string password, ServerMapHealth result, CancellationToken token)
	{
		var proxy = new WebProxy("http://127.0.0.1:" + port) { BypassProxyOnLocal = false, Credentials = new NetworkCredential(user, password) };
		using (var handler = new HttpClientHandler { UseProxy = true, Proxy = proxy, AllowAutoRedirect = false })
		using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) })
		{

                    // Failure of one control service is not proof that the profile is down.
                    foreach (string target in new[] { "https://1.1.1.1/cdn-cgi/trace", "https://www.gstatic.com/generate_204" })
                    {
                    try
                    {
                    using (var targetDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                    targetDeadline.CancelAfter(TimeSpan.FromSeconds(7));
                    bool trace = target.Contains("cdn-cgi/trace");
                    result.LatencyMs = await WarmHttpsLatency.MeasureAsync(async () =>
			{
				using (var request = new HttpRequestMessage(HttpMethod.Get, target))
				{
					request.Headers.ConnectionClose = false;
                            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                            var watch = Stopwatch.StartNew();
					using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, targetDeadline.Token).ConfigureAwait(false))
					{
						watch.Stop();
                                if (response.Headers.ConnectionClose == true) throw new IOException("Persistent HTTPS unavailable");
                                if (response.StatusCode != (trace ? HttpStatusCode.OK : HttpStatusCode.NoContent)) throw new IOException("Probe response failed");
						using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
						{
							var buffer = new byte[4096]; int count = 0, read;
							while (count < buffer.Length && (read = await stream.ReadAsync(buffer, count, buffer.Length - count, targetDeadline.Token).ConfigureAwait(false)) > 0) count += read;
							if (trace && !Encoding.UTF8.GetString(buffer, 0, count).Contains("\nip=")) throw new IOException("Unexpected probe body");
						}
                                return (int)Math.Max(1, watch.ElapsedMilliseconds);
					}
				}
			}).ConfigureAwait(false);
			result.Success = true;
			result.Target = new Uri(target).Host;
                    result.Message = "Медиана трёх HTTPS-ответов после установления соединения · " + result.Target;
                    }
                    break;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    catch (HttpRequestException) { }
                    catch (IOException) { }
                    }
                    if (!result.Success) result.Message = "Контрольные HTTPS-адреса через профиль не ответили";
		}
	}
}

internal static class WarmHttpsLatency
{
    internal static async Task<int> MeasureAsync(Func<Task<int>> validResponse)
    {
        // Setup request is validated, but excluded from the reported latency.
        if (await validResponse().ConfigureAwait(false) <= 0) throw new IOException("Invalid warm-up");
        var values = new int[3];
        for (int i = 0; i < values.Length; i++) {
            values[i] = await validResponse().ConfigureAwait(false);
            if (values[i] <= 0) throw new IOException("Invalid sample");
        }
        Array.Sort(values);
        return values[1];
    }
}

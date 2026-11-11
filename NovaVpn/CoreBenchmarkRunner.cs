using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class CoreBenchmarkResult
{
    public VpnEngine Engine { get; internal set; }
    public bool EcoRequested { get; internal set; }
    public int[] HttpCodes { get; internal set; } = new int[0];
    public double? MedianMilliseconds { get; internal set; }
    public double? CpuSeconds { get; internal set; }
    public CoreUsageRecord Resources { get; internal set; }
    public string Message { get; internal set; }
    public string ProfileStamp { get; internal set; }
    public string NetworkStamp { get; internal set; }
    public string CoreStamp { get; internal set; }
    public DateTime CheckedUtc { get; internal set; }
    public bool IsCurrent(VpnProfile profile, string network, string core, DateTime now) =>
        profile != null && !profile.RetiredFromSubscription && !string.IsNullOrEmpty(network) && !string.IsNullOrEmpty(core) && network == NetworkStamp && core == CoreStamp &&
        ServiceMatrixRunner.ProfileStamp(profile) == ProfileStamp && now >= CheckedUtc && now - CheckedUtc < TimeSpan.FromMinutes(3);
    public string EngineLabel => VpnEngines.DisplayName(Engine);
    public string ModeLabel => Resources == null ? (EcoRequested ? "EcoQoS запрошен" : "Исходная политика") : CoreResourceWindow.PowerLabel(Resources.Mode);
    public string RepliesLabel => HttpCodes.Length == 0 ? "—" : HttpCodes.Count(code => code >= 200 && code < 300) + " / " + CoreBenchmarkRunner.MeasuredRequests;
    public string LatencyLabel => MedianMilliseconds.HasValue ? MedianMilliseconds.Value.ToString("0.0") + " мс" : "—";
    public string CpuLabel => Resources?.CpuLabel ?? "—";
    public string CpuTimeLabel => CpuSeconds.HasValue ? CpuSeconds.Value.ToString("0.000") + " с" : "—";
    public string DurationLabel => Resources == null ? "—" : Resources.DurationSeconds.ToString("0.0") + " с";
    public string MemoryLabel => Resources?.MemoryLabel ?? "—";
}

// One frozen profile, one HTTPS URL, sequential owned proxy-only children. No
// engine preference, main tunnel, global power setting or personal state writes.
public sealed class CoreBenchmarkRunner : IDisposable
{
    public const int MeasuredRequests = 10;
    private static readonly SemaphoreSlim Slot = new SemaphoreSlim(1, 1);
    private readonly object sync = new object();
    private CancellationTokenSource cancellation;
    private Task<IReadOnlyList<CoreBenchmarkResult>> running;
    private LocalProxyManager manager;
    private string activeId;
    private bool ownsSlot, disposed;
    private int stopping;
    public bool IsRunning { get { lock (sync) return running != null && !running.IsCompleted; } }
    public bool HasActive { get { lock (sync) return ownsSlot; } }

    public Task<IReadOnlyList<CoreBenchmarkResult>> RunAsync(VpnProfile profile, ServiceCheckTarget target, string singbox, string reality, string runtime,
        bool includeEco, Func<bool> mayRun, Action<CoreBenchmarkResult> progress, CancellationToken token)
    {
        if (profile == null || target == null || mayRun == null) throw new ArgumentNullException();
        VpnProfile frozen;
        using (var data = new MemoryStream())
        {
            var serializer = new DataContractSerializer(typeof(VpnProfile)); serializer.WriteObject(data, profile); data.Position = 0;
            frozen = (VpnProfile)serializer.ReadObject(data);
        }
        // Both configurations must be representable without weakening the profile
        // before any process or private config file can be created.
        var endpoint = new LocalProxyEndpoint { ProfileId = frozen.Id, InboundType = "http" };
        LocalProxyConfiguration.Build(endpoint, frozen, VpnEngine.SingBox);
        LocalProxyConfiguration.Build(endpoint, frozen, VpnEngine.Reality);
        singbox = Path.GetFullPath(singbox); reality = Path.GetFullPath(reality); runtime = Path.GetFullPath(runtime);
        if (!File.Exists(singbox) || !File.Exists(reality)) throw new FileNotFoundException("Для сравнения нужны оба установленных ядра.");
        if (!mayRun()) throw new InvalidOperationException("Перед сравнением отключите основной VPN и игровой режим.");
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CoreBenchmarkRunner));
            if (running != null && !running.IsCompleted || ownsSlot || stopping != 0) throw new InvalidOperationException("Сначала завершите предыдущий тест и остановку его процессов.");
            cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            running = RunCoreAsync(frozen, target, singbox, reality, runtime, includeEco, mayRun, progress, cancellation.Token);
            return running;
        }
    }
    private async Task<IReadOnlyList<CoreBenchmarkResult>> RunCoreAsync(VpnProfile profile, ServiceCheckTarget target, string singbox, string reality, string runtime,
        bool includeEco, Func<bool> mayRun, Action<CoreBenchmarkResult> progress, CancellationToken token)
    {
        await Task.Yield();
        await Slot.WaitAsync(token).ConfigureAwait(false);
        lock (sync) ownsSlot = true;
        var results = new List<CoreBenchmarkResult>();
        string network = ProfileHealthEvidence.NetworkKey();
        try
        {
            manager = new LocalProxyManager(runtime);
            var trials = new[] { Tuple.Create(VpnEngine.SingBox, false), Tuple.Create(VpnEngine.Reality, false), Tuple.Create(VpnEngine.Reality, true), Tuple.Create(VpnEngine.SingBox, true) };
            foreach (var trial in trials.Take(includeEco ? 4 : 2))
            {
                token.ThrowIfCancellationRequested();
                if (!mayRun() || string.IsNullOrEmpty(network) || ProfileHealthEvidence.NetworkKey() != network) throw new InvalidOperationException("Состояние VPN или сети изменилось. Повторите сравнение.");
                string core = trial.Item1 == VpnEngine.SingBox ? singbox : reality, coreStamp = ProfileHealthEvidence.CoreKey(core);
                var result = new CoreBenchmarkResult { Engine = trial.Item1, EcoRequested = trial.Item2, ProfileStamp = ServiceMatrixRunner.ProfileStamp(profile), NetworkStamp = network, CoreStamp = coreStamp };
                var endpoint = new LocalProxyEndpoint { ProfileId = profile.Id, InboundType = "http", Port = FreePort() }; activeId = endpoint.Id;
                using (var trialCancel = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task<Tuple<int[], double?>> requests = null;
                    try
                    {
                        await manager.StartAsync(endpoint, profile, core, trial.Item1, true, trial.Item2, trialCancel.Token).ConfigureAwait(false);
                        int warmup = await RequestAsync(endpoint, target.Address, trialCancel.Token).ConfigureAwait(false);
                        if (warmup < 200 || warmup > 299)
                        {
                            result.Message = "Прогрев: HTTP " + warmup + ". Серия не начата; измерения отсутствуют.";
                            throw new IOException("Warm-up failed.");
                        }
                        var samples = new List<CoreResourceSnapshot> { await manager.ReadResourcesAsync(endpoint.Id, trialCancel.Token).ConfigureAwait(false) };
                        if (samples[0].PowerMode == CorePowerMode.Unavailable || trial.Item2 && samples[0].PowerMode != CorePowerMode.Eco || !trial.Item2 && samples[0].PowerMode == CorePowerMode.Eco)
                            throw new InvalidOperationException("Запрошенный режим не подтверждён Windows.");
                        requests = RequestsAsync(endpoint, target.Address, trialCancel.Token);
                        while (!requests.IsCompleted || samples.Count < 5)
                        {
                            await Task.Delay(500, trialCancel.Token).ConfigureAwait(false);
                            if (!mayRun() || ProfileHealthEvidence.NetworkKey() != network || ProfileHealthEvidence.CoreKey(core) != coreStamp)
                                throw new InvalidOperationException("Контекст сравнения изменился.");
                            if (samples.Count >= 512) throw new TimeoutException("Серия запросов не завершилась.");
                            samples.Add(await manager.ReadResourcesAsync(endpoint.Id, trialCancel.Token).ConfigureAwait(false));
                        }
                        var measured = await requests.ConfigureAwait(false);
                        if (!mayRun() || ProfileHealthEvidence.NetworkKey() != network || ProfileHealthEvidence.CoreKey(core) != coreStamp)
                            throw new InvalidOperationException("Контекст сравнения изменился.");
                        result.HttpCodes = measured.Item1; result.MedianMilliseconds = measured.Item2;
                        result.Resources = CoreUsageRecord.FromBenchmark(samples, result.EngineLabel);
                        result.CpuSeconds = (samples[samples.Count - 1].CpuTicks - samples[0].CpuTicks) / (double)TimeSpan.TicksPerSecond;
                        result.Message = result.HttpCodes.All(code => code >= 200 && code < 300) ? "10 ответов 2xx; измерения включают ожидание сети." :
                            "Есть ошибки: " + string.Join(", ", result.HttpCodes.GroupBy(code => code).Select(group => (group.Key == 0 ? "без ответа" : "HTTP " + group.Key) + " × " + group.Count())) + ". Успешная нагрузка различается.";
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        result.Resources = null; result.MedianMilliseconds = null; result.CpuSeconds = null;
                        if (result.Message == null) result.Message = "Измерение не завершено (" + error.GetType().Name + "). Проверьте профиль, HTTPS-адрес и режим Windows.";
                    }
                    finally
                    {
                        trialCancel.Cancel();
                        if (requests != null) { try { await requests.ConfigureAwait(false); } catch { } }
                        // If stop fails, retain the manager and global slot; no
                        // following trial starts until an explicit retry succeeds.
                        await manager.StopAsync(endpoint.Id).ConfigureAwait(false);
                    }
                }
                token.ThrowIfCancellationRequested(); result.CheckedUtc = DateTime.UtcNow; results.Add(result);
                try { progress?.Invoke(result); } catch { /* A closing view cannot prevent cleanup. */ }
            }
            return results.AsReadOnly();
        }
        finally { Cleanup(); }
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }
    private static async Task<int> RequestAsync(LocalProxyEndpoint endpoint, Uri target, CancellationToken token)
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            return await ServiceMatrixRunner.ReadHttpsStatusAsync(endpoint, target, deadline.Token).ConfigureAwait(false);
        }
    }
    private static async Task<Tuple<int[], double?>> RequestsAsync(LocalProxyEndpoint endpoint, Uri target, CancellationToken token)
    {
        var codes = new int[MeasuredRequests]; var times = new List<double>();
        for (int i = 0; i < MeasuredRequests; i++)
        {
            token.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew();
            try { codes[i] = await RequestAsync(endpoint, target, token).ConfigureAwait(false); if (codes[i] >= 200 && codes[i] < 300) times.Add(watch.Elapsed.TotalMilliseconds); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { codes[i] = 0; }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        times.Sort(); double? median = times.Count == 0 ? (double?)null : times.Count % 2 == 1 ? times[times.Count / 2] : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
        return Tuple.Create(codes, median);
    }
    private void Cleanup()
    {
        LocalProxyManager owned; lock (sync) owned = manager;
        owned?.Dispose();
        lock (sync) { if (!ownsSlot) return; ownsSlot = false; manager = null; activeId = null; Slot.Release(); }
    }
    public async Task StopAsync()
    {
        CancellationTokenSource cancel; Task task;
        lock (sync) { stopping++; cancel = cancellation; task = running; }
        try
        {
            try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
            if (task != null) { try { await task.ConfigureAwait(false); } catch { } }
            // A failed cleanup deliberately keeps its slot. StopAsync can be retried.
            LocalProxyManager owned; string id; lock (sync) { owned = manager; id = activeId; }
            if (owned != null && id != null) await owned.StopAsync(id).ConfigureAwait(false);
            Cleanup();
        }
        finally { lock (sync) stopping--; }
    }
    public void Dispose() { lock (sync) disposed = true; StopAsync().GetAwaiter().GetResult(); cancellation?.Dispose(); }
}

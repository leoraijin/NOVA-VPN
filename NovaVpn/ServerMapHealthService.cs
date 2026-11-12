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
	public bool IsFresh => CheckedUtc != default(DateTime) && DateTime.UtcNow >= CheckedUtc && DateTime.UtcNow - CheckedUtc < TimeSpan.FromMinutes(3);
    public bool IsFreshFor(VpnProfile profile) => IsFresh && profile != null && ProfileKey == SubscriptionManager.ConfigKey(profile);
	public string Label => !Supported ? Message : !IsFresh ? "Нужна свежая проверка" :
		(Success ? "HTTPS-отклик · " + LatencyMs + " мс" : Message) + " · " + Math.Max(0, (int)(DateTime.UtcNow - CheckedUtc).TotalSeconds) + " с назад";
}

public static class ServerMapHealthService
{
	private static readonly SemaphoreSlim ProbeSlots = new SemaphoreSlim(2);
	// The probe has no TUN, no direct outbound, and no proxy bypass/fallback.
	public static async Task<ServerMapHealth> TestAsync(VpnProfile profile, string corePath, string runtimeDirectory, CancellationToken token)
	{
		await ProbeSlots.WaitAsync(token).ConfigureAwait(false);
		try
        {
            string key = profile == null ? "" : SubscriptionManager.ConfigKey(profile);
            string network = ProfileHealthEvidence.NetworkKey(), core = ProfileHealthEvidence.CoreKey(corePath);
            var result = await TestCoreAsync(profile, corePath, runtimeDirectory, token).ConfigureAwait(false);
            if (profile != null && (key != SubscriptionManager.ConfigKey(profile) || network != ProfileHealthEvidence.NetworkKey() || core != ProfileHealthEvidence.CoreKey(corePath)))
                return new ServerMapHealth { Message = "Профиль, ядро или сеть изменились во время проверки. Повторите проверку.", CheckedUtc = DateTime.UtcNow };
            result.ProfileKey = key; result.NetworkKey = network; result.CoreKey = core;
            result.CheckedUtc = DateTime.UtcNow;
            ProfileHealthEvidence.Record(profile, result);
            return result;
        }
		finally { ProbeSlots.Release(); }
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
				bool ready = false;
				for (int i = 0; i < 40 && !process.HasExited; i++)
				{
					deadline.Token.ThrowIfCancellationRequested();
					using (var socket = new TcpClient())
					{
						try { await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false); ready = true; break; }
						catch (SocketException) { }
					}
					await Task.Delay(75, deadline.Token).ConfigureAwait(false);
				}
				if (!ready) throw new IOException("Probe failed to start");
				var proxy = new WebProxy("http://127.0.0.1:" + port) { BypassProxyOnLocal = false, Credentials = new NetworkCredential(user, password) };
				using (var handler = new HttpClientHandler { UseProxy = true, Proxy = proxy, AllowAutoRedirect = false })
				using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) })
				{
					
                    // Failure of one control service is not proof that the profile is down.
                    foreach (string target in new[] { "https://1.1.1.1/cdn-cgi/trace", "https://www.gstatic.com/generate_204" })
                    {
                    try
                    {
                    using (var targetDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
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
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                    catch (HttpRequestException) { }
                    catch (IOException) { }
                    }
                    if (!result.Success) result.Message = "Контрольные HTTPS-адреса через профиль не ответили";
				}
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

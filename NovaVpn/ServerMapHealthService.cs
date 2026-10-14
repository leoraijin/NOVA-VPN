using System;
using System.Diagnostics;
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
	public bool IsFresh => CheckedUtc != default(DateTime) && DateTime.UtcNow - CheckedUtc < TimeSpan.FromMinutes(3);
	public string Label => !Supported ? Message : !IsFresh ? "Нужна свежая проверка" :
		(Success ? "HTTPS работает · " + LatencyMs + " мс" : Message) + " · " + Math.Max(0, (int)(DateTime.UtcNow - CheckedUtc).TotalSeconds) + " с назад";
}

public static class ServerMapHealthService
{
	private static readonly SemaphoreSlim ProbeSlots = new SemaphoreSlim(2);
	// The probe has no TUN, no direct outbound, and no proxy bypass/fallback.
	public static async Task<ServerMapHealth> TestAsync(VpnProfile profile, string corePath, string runtimeDirectory, CancellationToken token)
	{
		await ProbeSlots.WaitAsync(token).ConfigureAwait(false);
		try { return await TestCoreAsync(profile, corePath, runtimeDirectory, token).ConfigureAwait(false); }
		finally { ProbeSlots.Release(); }
	}

	private static async Task<ServerMapHealth> TestCoreAsync(VpnProfile profile, string corePath, string runtimeDirectory, CancellationToken token)
	{
		var result = new ServerMapHealth();
		if (profile == null || profile.Protocol == "sing-box")
			return new ServerMapHealth { Supported = false, Message = "Полный конфиг: проверка при подключении" };
		if (!File.Exists(corePath)) return new ServerMapHealth { Supported = false, Message = "Нужно установить VPN-ядро" };
		string file = null;
		Process process = null;
		try
		{
			token.ThrowIfCancellationRequested();
			int port;
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try { port = ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
			string user = Guid.NewGuid().ToString("N"), password = Guid.NewGuid().ToString("N");
			Directory.CreateDirectory(runtimeDirectory);
			file = Path.Combine(runtimeDirectory, "map-" + Guid.NewGuid().ToString("N") + ".json");
			File.WriteAllText(file, ConfigBuilder.BuildProbe(profile, port, user, password), new UTF8Encoding(false));
			process = new Process { StartInfo = new ProcessStartInfo(corePath, "run -c \"" + file + "\"") {
				UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
			process.OutputDataReceived += (s, e) => { };
			process.ErrorDataReceived += (s, e) => { };
			process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
			using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
			{
				deadline.CancelAfter(TimeSpan.FromSeconds(16));
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
					var watch = Stopwatch.StartNew();
					// Numeric HTTPS endpoint avoids depending on the user's browser DNS.
					for (int i = 0; i < 2; i++)
					{
						using (var request = new HttpRequestMessage(HttpMethod.Get, "https://1.1.1.1/cdn-cgi/trace"))
						{
							request.Headers.ConnectionClose = true;
							using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
							{
								if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Probe response failed");
								using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
								{
									var buffer = new byte[4096]; int count = 0, read;
									while (count < buffer.Length && (read = await stream.ReadAsync(buffer, count, buffer.Length - count, deadline.Token).ConfigureAwait(false)) > 0) count += read;
									if (!Encoding.UTF8.GetString(buffer, 0, count).Contains("\nip=")) throw new IOException("Unexpected probe body");
								}
							}
						}
					}
					result.LatencyMs = (int)Math.Max(1, watch.ElapsedMilliseconds / 2);
					result.Success = true;
					result.Message = "Два HTTPS-запроса через профиль выполнены";
				}
			}
		}
		catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; result.Message = "HTTPS: тайм-аут"; }
		catch (Exception) { result.Message = "HTTPS через профиль не прошёл"; }
		finally
		{
			if (process != null) { try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { } process.Dispose(); }
			if (file != null) { try { File.Delete(file); } catch { } }
		}
		result.CheckedUtc = DateTime.UtcNow;
		return result;
	}
}

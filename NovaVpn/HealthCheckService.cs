using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public static class HealthCheckService
{
    public static async Task<ServerHealthResult> TestAsync(VpnProfile profile, int timeoutMs = 30000)
    {
        using (var deadline = new CancellationTokenSource(Math.Max(1000, timeoutMs)))
        {
            try
            {
                var result = await ServerMapHealthService.TestAsync(profile, VpnEngines.ExecutablePath(VpnEngine.SingBox),
                    Path.Combine(StateStore.DirectoryPath, "runtime", "profile-probes"), deadline.Token);
                return FromProbe(result);
            }
            catch (OperationCanceledException)
            {
                if (profile != null) { profile.LastLatency = -1; profile.HealthMeasurement = ""; profile.HealthStatus = "Тайм-аут"; }
                return new ServerHealthResult { Status = "Тайм-аут", Message = "HTTPS через профиль: время проверки истекло." };
            }
        }
    }

    public static ServerHealthResult FromProbe(ServerMapHealth health) => new ServerHealthResult {
        Success = health.Success, Supported = health.Supported, LatencyMs = health.Success ? health.LatencyMs : -1,
        CheckedUtc = health.CheckedUtc, Measurement = "https-profile", PacketLossPercent = -1,
        SampleCount = health.Success ? 3 : 0, SuccessfulSamples = health.Success ? 3 : 0,
        Status = !health.Supported ? "Не проверен" : health.Success ? "HTTPS работает" : "Проверка не прошла", Message = health.Message };

    // Optional port diagnostic; it does not rank profiles or claim VPN connectivity.
	public static async Task<ServerHealthResult> TestPortAsync(VpnProfile profile, int timeoutMs = 4500)
	{
		ServerHealthResult result = new ServerHealthResult();
		if (profile == null)
		{
			result.Status = "Ошибка";
			result.Message = "Профиль не выбран.";
			return result;
		}
		if (profile.Protocol == "sing-box")
		{
			result.Success = !string.IsNullOrWhiteSpace(profile.RawJson);
			result.Status = result.Success ? "Конфигурация" : "Ошибка";
			result.Message = result.Success ? "Полная конфигурация проверяется перед подключением." : "Конфигурация пуста.";
			return result;
		}
		if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port < 1 || profile.Port > 65535)
		{
			result.Status = "Ошибка";
			result.Message = "Некорректный адрес или порт сервера.";
			return result;
		}
		try
		{
			Task<IPAddress[]> dnsTask = Dns.GetHostAddressesAsync(profile.Server);
			if (await Task.WhenAny(dnsTask, Task.Delay(timeoutMs)) != dnsTask)
			{
				throw new TimeoutException("DNS не ответил вовремя.");
			}
			IPAddress[] addresses = await dnsTask;
			if (addresses.Length == 0)
			{
				throw new SocketException((int)SocketError.HostNotFound);
			}
			List<int> samples = new List<int>();
			Exception lastError = null;
			for (int attempt = 0; attempt < 3; attempt++)
			{
				try
				{
					Stopwatch watch = Stopwatch.StartNew();
					using (TcpClient client = new TcpClient())
					{
						Task connect = client.ConnectAsync(profile.Server, profile.Port);
						if (await Task.WhenAny(connect, Task.Delay(Math.Max(1200, timeoutMs / 2))) != connect)
						{
							throw new TimeoutException("Сервер не ответил вовремя.");
						}
						await connect;
						if (!client.Connected) throw new SocketException((int)SocketError.ConnectionRefused);
					}
					watch.Stop();
					samples.Add((int)Math.Max(1L, watch.ElapsedMilliseconds));
				}
				catch (Exception ex)
				{
					lastError = ex;
				}
			}
			result.SampleCount = 3;
			result.SuccessfulSamples = samples.Count;
			if (samples.Count == 0)
			{
				if (lastError != null) throw lastError;
				throw new TimeoutException("Сервер не ответил вовремя.");
			}
			result.Success = true;
			result.LatencyMs = (int)Math.Round(samples.Average());
            result.Measurement = "tcp-connect";
			result.Status = "TCP-порт доступен";
			result.Message = "TCP-подключений: " + samples.Count + "/3. Это не проверка VPN и не измерение потерь пакетов.";
		}
		catch (TimeoutException ex)
		{
			result.Status = "Тайм-аут";
			result.Message = ex.Message;
		}
		catch (SocketException ex)
		{
			result.Status = "Недоступен";
			result.Message = FriendlySocketError(ex.SocketErrorCode);
		}
		catch (Exception ex)
		{
			result.Status = "Ошибка";
			result.Message = ex.Message;
		}
		return result;
	}

	public static async Task<List<ServerHealthResult>> TestAllAsync(IList<VpnProfile> profiles, int maxParallel = 4)
	{
		List<ServerHealthResult> results = new List<ServerHealthResult>();
		if (profiles == null || profiles.Count == 0)
		{
			return results;
		}
		using (SemaphoreSlim gate = new SemaphoreSlim(Math.Max(1, maxParallel)))
		{
			Task<ServerHealthResult>[] tasks = profiles.Where(p => p != null && !p.RetiredFromSubscription).ToArray().Select(async profile =>
			{
				await gate.WaitAsync();
				try
				{
					return await TestAsync(profile);
				}
				finally
				{
					gate.Release();
				}
			}).ToArray();
			results.AddRange(await Task.WhenAll(tasks));
		}
		return results;
	}

	public static VpnProfile BestAvailable(IEnumerable<VpnProfile> profiles)
	{
        string network = ProfileHealthEvidence.NetworkKey();
        string core = ProfileHealthEvidence.CoreKey(VpnEngines.ExecutablePath(VpnEngine.SingBox));
        return BestAvailableFor(profiles, DateTime.UtcNow, network, core);
    }

    public static VpnProfile BestAvailableFor(IEnumerable<VpnProfile> profiles, DateTime now, string network, string core)
    {
        // Compare measurements to the same control endpoint, never unrelated RTTs.
        var group = (profiles ?? Enumerable.Empty<VpnProfile>())
            .Where(p => ProfileHealthEvidence.IsCurrent(p, now, network, core))
            .GroupBy(p => p.HealthTarget ?? "")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        return group?.OrderBy(p => p.LastLatency).ThenByDescending(p => p.IsFavorite).FirstOrDefault();
	}

	private static string FriendlySocketError(SocketError error)
	{
		switch (error)
		{
			case SocketError.HostNotFound:
			case SocketError.NoData:
				return "DNS не смог найти адрес сервера.";
			case SocketError.ConnectionRefused:
				return "Сервер отклонил TCP-подключение.";
			case SocketError.NetworkUnreachable:
			case SocketError.HostUnreachable:
				return "Маршрут до сервера недоступен.";
			default:
				return "Сетевая ошибка: " + error + ".";
		}
	}
}

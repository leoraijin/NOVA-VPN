using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public static class HealthCheckService
{
	public static async Task<ServerHealthResult> TestAsync(VpnProfile profile, int timeoutMs = 4500)
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
			Apply(profile, result);
			return result;
		}
		if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port < 1 || profile.Port > 65535)
		{
			result.Status = "Ошибка";
			result.Message = "Некорректный адрес или порт сервера.";
			Apply(profile, result);
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
			result.PacketLossPercent = (3 - samples.Count) * 100 / 3;
			if (samples.Count == 0)
			{
				if (lastError != null) throw lastError;
				throw new TimeoutException("Сервер не ответил вовремя.");
			}
			result.Success = true;
			result.LatencyMs = (int)Math.Round(samples.Average());
			result.Status = result.PacketLossPercent > 0 ? "Нестабильный" : result.LatencyMs <= 120 ? "Быстрый" : result.LatencyMs <= 260 ? "Доступен" : "Медленный";
			result.Message = "DNS и TCP работают · потери " + result.PacketLossPercent + "% · " + samples.Count + "/3 ответов.";
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
		Apply(profile, result);
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
			Task<ServerHealthResult>[] tasks = profiles.Select(async profile =>
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
		return (profiles ?? Enumerable.Empty<VpnProfile>())
			.Where(p => p != null && p.LastLatency > 0 && p.ConsecutiveFailures == 0)
			.OrderBy(p => p.AverageLatency > 0 ? p.AverageLatency : p.LastLatency)
			.ThenByDescending(p => p.IsFavorite)
			.FirstOrDefault();
	}

	private static void Apply(VpnProfile profile, ServerHealthResult result)
	{
		profile.LastCheckedUtc = result.CheckedUtc;
		profile.LastLatency = result.LatencyMs;
		profile.HealthStatus = result.Status;
		profile.LastTestMessage = result.Message;
		profile.LastLossPercent = result.PacketLossPercent;
		if (result.Success && result.LatencyMs > 0)
		{
			profile.AverageLatency = profile.AverageLatency > 0 ? (profile.AverageLatency * 2 + result.LatencyMs) / 3 : result.LatencyMs;
			profile.ConsecutiveFailures = 0;
		}
		else if (!result.Success)
		{
			profile.ConsecutiveFailures++;
		}
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

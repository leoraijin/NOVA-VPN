using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace NovaVpn;

public static class NetworkSpeedService
{
	public static async Task<NetworkSpeedResult> MeasureAsync()
	{
		try
		{
			using (HttpClient client = new HttpClient())
			{
				client.Timeout = TimeSpan.FromSeconds(12.0);
				client.DefaultRequestHeaders.UserAgent.ParseAdd("NOVA-VPN/2.0");
				Stopwatch watch = Stopwatch.StartNew();
				byte[] data = await client.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=1000000");
				watch.Stop();
				double seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
				double mbps = data.LongLength * 8.0 / seconds / 1000000.0;
				return new NetworkSpeedResult
				{
					Success = data.Length > 0,
					MegabitsPerSecond = mbps,
					ElapsedMilliseconds = (int)watch.ElapsedMilliseconds,
					Message = "Получено " + data.Length / 1000 + " КБ за " + watch.ElapsedMilliseconds + " мс."
				};
			}
		}
		catch (Exception ex)
		{
			return new NetworkSpeedResult { Success = false, Message = ex.Message };
		}
	}
}

using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace NovaVpn;

public static class ExternalIpService
{
    internal static async Task RefreshAsync(Func<Task<string>> query, Func<bool> isCurrent, Func<string, Task> publish)
    {
        if (!isCurrent()) return;
        string value = await query();
        if (!isCurrent()) return;
        await publish(value);
    }
	public static async Task<string> GetAsync()
	{
		try
		{
			using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) })
			{
				string trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
				foreach (string line in trace.Split('\n')) if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase)) return line.Substring(3).Trim();
			}
		}
		catch { }
		return "недоступен";
	}
}

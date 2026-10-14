using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public static class StartupProfileSelector
{
	public static async Task<VpnProfile> SelectAsync(IEnumerable<VpnProfile> source, string preferredId,
		Func<VpnProfile, Task<ServerMapHealth>> check, CancellationToken token)
	{
		var profiles = source.Where(p => p != null).ToArray();
		var preferred = profiles.FirstOrDefault(p => p.Id == preferredId);
		if (preferred != null) {
			token.ThrowIfCancellationRequested();
			var health = await check(preferred);
			token.ThrowIfCancellationRequested();
			// A full configuration must be tried and verified by the real running core.
			if (!health.Supported || health.Success) return preferred;
		}
		var results = new List<Tuple<VpnProfile, ServerMapHealth>>();
		using (var slots = new SemaphoreSlim(2)) {
			await Task.WhenAll(profiles.Where(p => p != preferred).Select(async p => {
				await slots.WaitAsync(token);
				try { var health = await check(p); if (health.Supported && health.Success) lock (results) results.Add(Tuple.Create(p, health)); }
				finally { slots.Release(); }
			}));
		}
		token.ThrowIfCancellationRequested();
		return results.OrderBy(x => x.Item2.LatencyMs).Select(x => x.Item1).FirstOrDefault();
	}
}

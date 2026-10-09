using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace NovaVpn;

public static class SubscriptionManager
{
	private static readonly SemaphoreSlim UpdateGate = new SemaphoreSlim(1, 1);

	public static void MergeImported(AppState state, ImportResult imported)
	{
		if (state == null) throw new ArgumentNullException(nameof(state));
		if (imported == null || !imported.IsSubscription) throw new ArgumentException("Импорт не является подпиской.", nameof(imported));
		MergeSubscription(state, imported.SubscriptionUrl, imported.Profiles);
		state.LastSubscriptionUpdateUtc = DateTime.UtcNow;
	}

	public static async Task<SubscriptionUpdateResult> UpdateAllAsync(AppState state)
	{
		await UpdateGate.WaitAsync();
		try
		{
		SubscriptionUpdateResult report = new SubscriptionUpdateResult();
		if (state == null || state.Profiles == null)
		{
			return report;
		}
		string[] urls = state.Profiles
			.Select(p => p == null ? "" : p.SubscriptionUrl)
			.Where(url => !string.IsNullOrWhiteSpace(url))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		report.SubscriptionCount = urls.Length;
		foreach (string url in urls)
		{
			try
			{
				ImportResult imported = await LinkParser.ImportDetailedAsync(url);
				MergeSubscription(state, url, imported.Profiles);
				report.UpdatedProfiles += imported.Profiles.Count;
			}
			catch (Exception ex)
			{
				report.Errors.Add("Подписка " + SafeHost(url) + ": " + ex.Message);
			}
		}
		state.LastSubscriptionUpdateUtc = DateTime.UtcNow;
		StateStore.Save(state);
		return report;
		}
		finally
		{
			UpdateGate.Release();
		}
	}

	private static void MergeSubscription(AppState state, string url, List<VpnProfile> incoming)
	{
		List<VpnProfile> old = state.Profiles.Where(p => p != null && string.Equals(p.SubscriptionUrl, url, StringComparison.OrdinalIgnoreCase)).ToList();
		List<VpnProfile> claimed = new List<VpnProfile>();
		foreach (VpnProfile profile in incoming)
		{
			VpnProfile previous = old.FirstOrDefault(p => !claimed.Contains(p) && SameEndpoint(p, profile))
				?? state.Profiles.FirstOrDefault(p => p != null && !claimed.Contains(p) && !String.IsNullOrWhiteSpace(p.SubscriptionUrl) && !String.IsNullOrWhiteSpace(profile.Source) && string.Equals(p.Source, profile.Source, StringComparison.Ordinal))
				?? state.Profiles.FirstOrDefault(p => p != null && !claimed.Contains(p) && !String.IsNullOrWhiteSpace(p.SubscriptionUrl) && SameEndpoint(p, profile));
			if (previous != null)
			{
				claimed.Add(previous);
				profile.Id = previous.Id;
				profile.IsFavorite = previous.IsFavorite;
				profile.LastLatency = previous.LastLatency;
				profile.AverageLatency = previous.AverageLatency;
				profile.LastCheckedUtc = previous.LastCheckedUtc;
				profile.ConsecutiveFailures = previous.ConsecutiveFailures;
				profile.HealthStatus = previous.HealthStatus;
				profile.LastTestMessage = previous.LastTestMessage;
			}
		}
		List<VpnProfile> replacing = old.Concat(claimed).Distinct().ToList();
		int insertAt = replacing.Count == 0 ? state.Profiles.Count : replacing.Select(profile => state.Profiles.IndexOf(profile)).Where(index => index >= 0).DefaultIfEmpty(state.Profiles.Count).Min();
		state.Profiles.RemoveAll(p => p != null && (string.Equals(p.SubscriptionUrl, url, StringComparison.OrdinalIgnoreCase) || claimed.Contains(p)));
		state.Profiles.InsertRange(Math.Min(insertAt, state.Profiles.Count), incoming);
		if (!state.Profiles.Any(p => p.Id == state.SelectedProfileId))
		{
			state.SelectedProfileId = incoming.FirstOrDefault()?.Id ?? state.Profiles.FirstOrDefault()?.Id;
		}
	}

	private static bool SameEndpoint(VpnProfile left, VpnProfile right)
	{
		return string.Equals(left.Protocol, right.Protocol, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.Server, right.Server, StringComparison.OrdinalIgnoreCase)
			&& left.Port == right.Port
			&& string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
	}

	private static string SafeHost(string url)
	{
		Uri uri;
		return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : "неизвестная";
	}
}

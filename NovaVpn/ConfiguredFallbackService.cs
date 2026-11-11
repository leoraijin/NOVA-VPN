using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class FallbackCandidatePlan
{
    public VpnProfile Root { get; internal set; }
    public bool IsConfigured { get; internal set; }
    public List<VpnProfile> Candidates { get; } = new List<VpnProfile>();
    public List<string> Warnings { get; } = new List<string>();
}

public static class ConfiguredFallbackService
{
    public const int MaxAlternatives = 16;

    public static FallbackCandidatePlan Build(IEnumerable<VpnProfile> profiles, string selectedId, string rootId, string failedId = null)
        => BuildPreferred(profiles, selectedId, rootId, failedId, null);

    public static FallbackCandidatePlan BuildPreferred(IEnumerable<VpnProfile> profiles, string selectedId, string rootId, string failedId, string preferredId)
    {
        var all = (profiles ?? Enumerable.Empty<VpnProfile>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var plan = new FallbackCandidatePlan();
        if (selectedId == null || !all.TryGetValue(selectedId, out var selected)) return plan;
        var root = rootId != null && all.TryGetValue(rootId, out var stored) &&
            (stored.Id == selectedId || (stored.FallbackProfileIds ?? new List<string>()).Contains(selectedId)) ? stored : selected;
        plan.Root = root;
        var ids = (root.FallbackProfileIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id) && id != root.Id)
            .Distinct(StringComparer.Ordinal).Take(MaxAlternatives).ToList();
        plan.IsConfigured = ids.Count > 0;
        ids.Insert(0, root.Id);
        foreach (string id in ids)
        {
            if (!all.TryGetValue(id, out var profile)) { plan.Warnings.Add("Резервный профиль отсутствует: " + id); continue; }
            if (profile.RetiredFromSubscription) { plan.Warnings.Add("Профиль удалён из подписки: " + profile.Name); continue; }
            if (plan.IsConfigured && profile.Protocol == "sing-box") { plan.Warnings.Add("Полный конфиг не участвует в автоматическом резервировании: " + profile.Name); continue; }
            plan.Candidates.Add(profile);
        }
        // After a live connection failure, try the next explicitly configured endpoint first.
        int failed = plan.Candidates.FindIndex(p => p.Id == failedId);
        if (failed >= 0 && plan.Candidates.Count > 1)
        {
            var after = plan.Candidates.Skip(failed + 1).Concat(plan.Candidates.Take(failed + 1)).ToList();
            plan.Candidates.Clear(); plan.Candidates.AddRange(after);
        }
        else
        {
            // Rollback starts the previously working member without losing its root policy.
            int preferred = plan.Candidates.FindIndex(p => p.Id == preferredId);
            if (preferred > 0)
            {
                var ordered = plan.Candidates.Skip(preferred).Concat(plan.Candidates.Take(preferred)).ToList();
                plan.Candidates.Clear(); plan.Candidates.AddRange(ordered);
            }
        }
        return plan;
    }

    public static async Task<VpnProfile> RunAsync(FallbackCandidatePlan plan, Func<VpnProfile, CancellationToken, Task<bool>> attempt,
        Action stop, Func<bool> requested, CancellationToken cancellation, Action<VpnProfile, int> progress = null)
    {
        if (plan == null || attempt == null || stop == null || requested == null) throw new ArgumentNullException();
        Exception last = null;
        for (int index = 0; index < plan.Candidates.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!requested()) throw new OperationCanceledException("Подключение отменено.");
            var profile = plan.Candidates[index];
            try
            {
                progress?.Invoke(profile, index);
                bool success = await attempt(profile, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!requested()) throw new OperationCanceledException("Подключение отменено.");
                if (success) return profile;
                last = new InvalidOperationException("Проверка передачи данных не прошла.");
            }
            catch (OperationCanceledException) { stop(); throw; }
            catch (TunnelLeaseBusyException) { stop(); throw; }
            catch (Exception ex) { last = ex; }
            // A failed cleanup aborts the chain: overlapping tunnels are never allowed.
            stop();
        }
        throw new InvalidOperationException("Ни один из явно настроенных профилей не подключился.", last);
    }
}

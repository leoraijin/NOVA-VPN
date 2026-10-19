using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class AutomaticGameTarget
{
    public GamingTarget Target;
    public bool GameProtocol;
}

public sealed class AutomaticGamingResult
{
    public string ProfileId;
    public bool GameProtocol;
    public bool KeptCurrent;
    public List<GamingProbeResult> Measurements = new List<GamingProbeResult>();
    public string Message;
    public string ResponseLabel {
        get {
            if (!GameProtocol || Measurements.Count == 0) return "Предварительная оценка маршрута; игровой пинг пока не подтверждён";
            double low = Measurements.Min(r => r.Median), high = Measurements.Max(r => r.Median);
            return Math.Round(low) == Math.Round(high) ? string.Format("Отклик сервера: {0:F0} мс", high) : string.Format("Отклик серверов: {0:F0}–{1:F0} мс", low, high);
        }
    }
}

// Uses only the supplied user's profiles and observed connections. No external VPN catalogue.
public sealed class AutomaticGamingService
{
    private readonly Func<GamingTarget, List<VpnProfile>, int, CancellationToken, Task<List<GamingProbeResult>>> measurement;
    public AutomaticGamingService(Func<GamingTarget, List<VpnProfile>, int, CancellationToken, Task<List<GamingProbeResult>>> measurement = null) { this.measurement = measurement; }
    private Task<List<GamingProbeResult>> MeasureAsync(GamingTarget target, IEnumerable<VpnProfile> profiles, int rounds,
        string corePath, string runtime, CancellationToken token) => measurement == null ?
        new GamingProbeService().ScanAsync(target, profiles, corePath, runtime, false, null, token, rounds) :
        measurement(target, profiles.ToList(), rounds, token);

    public static List<AutomaticGameTarget> Targets(InstalledGame game, IEnumerable<GameEndpoint> observed)
    {
        return GameAdapter.Targets(game, observed);
    }

    public async Task<AutomaticGamingResult> SelectAsync(InstalledGame game, IEnumerable<GameEndpoint> observed,
        IEnumerable<VpnProfile> profiles, string currentProfileId, string corePath, string runtime,
        IProgress<string> progress, CancellationToken token, bool compareDirect = false)
    {
        var candidates = profiles.Where(p => p != null && p.Protocol != "sing-box" && !string.IsNullOrEmpty(p.Id))
            .GroupBy(p => p.Id).Select(g => g.First()).ToList();
        if (candidates.Count == 0) return new AutomaticGamingResult { Message = "Добавьте серверы в NOVA: подбор использует ваши конфиги." };
        if (compareDirect) {
            candidates.RemoveAll(p => p.Id == "direct");
            candidates.Add(new VpnProfile { Id = "direct", Name = "Обычное подключение", Protocol = "direct", PortableOutboundJson = "{\"type\":\"direct\",\"tag\":\"proxy\"}" });
            currentProfileId = "direct";
        }
        var targets = Targets(game, observed);
        if (targets.Count == 0) return new AutomaticGamingResult { Message = "Пока нет проверяемых соединений игры. Подбор продолжится после подключения к игровому серверу." };
        // A compatible query response is stronger evidence than a service TCP connection.
        foreach (bool gameProtocol in new[] { true, false }) {
            var group = targets.Where(t => t.GameProtocol == gameProtocol).ToList();
            if (group.Count == 0) continue;
            var quick = new Dictionary<string, List<GamingProbeResult>>();
            foreach (var target in group) {
                var results = new List<GamingProbeResult>();
                for (int start = 0; start < candidates.Count; start += 4) {
                    token.ThrowIfCancellationRequested();
                    progress?.Report("Проверяем ваши серверы: " + Math.Min(start + 4, candidates.Count) + "/" + candidates.Count);
                    results.AddRange(await MeasureAsync(target.Target, candidates.Skip(start).Take(4), 3, corePath, runtime, token));
                }
                quick[target.Target.Key] = results;
            }
            // Keep a common set of reachable targets: a fast answer to one target cannot beat
            // a profile that was required to answer a different, slower server.
            var usable = group.Where(t => quick[t.Target.Key].Any(r => r.Samples.Count >= 2)).ToList();
            if (usable.Count == 0) continue;
            var ranked = candidates.Where(p => usable.All(t => quick[t.Target.Key].Any(r => r.ProfileId == p.Id && r.Samples.Count >= 2)))
                .OrderBy(p => usable.Average(t => Cost(quick[t.Target.Key].First(r => r.ProfileId == p.Id)))).Take(3).ToList();
            var current = candidates.FirstOrDefault(p => p.Id == currentProfileId);
            if (current != null && ranked.All(p => p.Id != current.Id)) ranked.Add(current);
            if (ranked.Count == 0) continue;
            var measured = new List<List<GamingProbeResult>>();
            foreach (var target in usable) {
                progress?.Report("Уточняем стабильность лучших маршрутов…");
                token.ThrowIfCancellationRequested();
                measured.Add(await MeasureAsync(target.Target, ranked, 10, corePath, runtime, token));
            }
            var winner = Choose(measured, currentProfileId);
            if (winner == null) continue;
            return new AutomaticGamingResult { ProfileId = winner, GameProtocol = gameProtocol, KeptCurrent = winner == currentProfileId,
                Measurements = measured.SelectMany(x => x).Where(r => r.ProfileId == winner).ToList(),
                Message = winner == "direct" ? "Обычное подключение лучше: игровой VPN не требуется." : winner == currentProfileId ? "Текущий конфиг — лучший из проверенных." : "Лучший из проверенных конфигов подобран." };
        }
        return new AutomaticGamingResult { Message = "Надёжный результат пока не получен. Обычное подключение сохранено." };
    }

    private static double Cost(GamingProbeResult r) => r.Median + 0.5 * r.Spread + 300 * r.FailureRate;
    public static string Choose(IList<List<GamingProbeResult>> measurements, string currentProfileId)
    {
        if (measurements.Count == 0) return null;
        var valid = measurements[0].Where(r => !r.IsDirect && r.Reliable).Select(r => r.ProfileId).Distinct()
            .Where(id => measurements.All(rows => rows.Any(r => r.ProfileId == id && r.Reliable))).ToList();
        if (valid.Count == 0) return null;
        Func<string, double> score = id => measurements.Average(rows => Cost(rows.First(r => r.ProfileId == id)));
        string best = valid.OrderBy(score).First();
        if (!valid.Contains(currentProfileId) || best == currentProfileId) return best;
        // Preserve the current egress for small gains or a worse tail on any tested target.
        bool material = score(currentProfileId) - score(best) >= Math.Max(5, score(currentProfileId) * 0.1);
        bool noWorse = measurements.All(rows => {
            var a = rows.First(r => r.ProfileId == best); var b = rows.First(r => r.ProfileId == currentProfileId);
            return a.P95 <= b.P95 + 5 && a.FailureRate <= b.FailureRate;
        });
        return material && noWorse ? best : currentProfileId;
    }
}

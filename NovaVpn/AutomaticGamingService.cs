using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class AutomaticGameTarget
{
    public GamingTarget Target;
    public bool GameProtocol;
}

public sealed class GamingRouteComparison
{
    public string TargetKey;
    public GamingProbeResult Direct;
    public GamingProbeResult Candidate;
}

public sealed class AutomaticGamingResult
{
    public string ProfileId;
    public bool GameProtocol;
    public bool KeptCurrent;
    public List<GamingProbeResult> Measurements = new List<GamingProbeResult>();
    public List<GamingRouteComparison> Comparisons = new List<GamingRouteComparison>();
    public string Message;
    // Revalidate evidence at activation, not just when a button was created.
    public bool HasVerifiedImprovement => GameProtocol && ProfileId != null && ProfileId != "direct" &&
        Comparisons.Count > 0 && Comparisons.All(c => c != null && !string.IsNullOrEmpty(c.TargetKey) &&
            c.Direct?.ProfileId == "direct" && c.Candidate?.ProfileId == ProfileId) &&
        AutomaticGamingService.BeatsDirect(Comparisons.Select(c => c.Direct).ToList(), Comparisons.Select(c => c.Candidate).ToList());
    // An explicit direct launch needs no claim of a VPN improvement. Only VPN needs evidence.
    public bool CanPlay => ProfileId == "direct" || HasVerifiedImprovement;
    public string ResponseLabel {
        get {
            if (!GameProtocol || Comparisons.Count == 0) return "Игровая задержка не подтверждена. TCP и пинг до VPN не используются для включения маршрута.";
            double direct = Comparisons.Average(c => c.Direct.Median);
            double selected = Comparisons.Average(c => c.Candidate.Median);
            return string.Format("UDP query: напрямую {0:F0} мс · выбранный маршрут {1:F0} мс. Это отклик query-порта, не телеметрия матча.", direct, selected);
        }
    }
}

// Only measured game-query targets may authorize an automatic route. Service TCP never does.
public sealed class AutomaticGamingService
{
    private readonly Func<GamingTarget, List<VpnProfile>, int, CancellationToken, Task<List<GamingProbeResult>>> measurement;
    public AutomaticGamingService(Func<GamingTarget, List<VpnProfile>, int, CancellationToken, Task<List<GamingProbeResult>>> measurement = null) { this.measurement = measurement; }
    private Task<List<GamingProbeResult>> MeasureAsync(GamingTarget target, IEnumerable<VpnProfile> profiles, int rounds,
        string corePath, string runtime, CancellationToken token) => measurement == null ?
        new GamingProbeService().ScanAsync(target, profiles, corePath, runtime, false, null, token, rounds) :
        measurement(target, profiles.ToList(), rounds, token);

    public static List<AutomaticGameTarget> Targets(InstalledGame game, IEnumerable<GameEndpoint> observed) => GameAdapter.Targets(game, observed);
    public static bool SupportsGameQuery(InstalledGame game) => GameAdapter.Kind(game) == GameAdapterKind.Rust || GameAdapter.Kind(game) == GameAdapterKind.Source;
    public static AutomaticGamingResult Unsupported(InstalledGame game) => new AutomaticGamingResult {
        ProfileId = "direct",
        Message = "Не удалось подтвердить меньшую игровую задержку для «" + game.Name + "». " +
            "TCP-соединения и пинг до дата-центра не доказывают выигрыш в матче. Игровой VPN не выбран. Нажмите «Играть напрямую», чтобы исключить игру из обычного VPN."
    };

    // A physical-network baseline must never reuse the active VPN or an imported detour.
    public static VpnProfile DirectProfile(string interfaceName) {
        if (string.IsNullOrWhiteSpace(interfaceName)) throw new InvalidOperationException("Не определён физический интерфейс для прямого сравнения.");
        return new VpnProfile { Id = "direct", Name = "Прямое соединение", Protocol = "direct",
            PortableOutboundJson = new JavaScriptSerializer().Serialize(new { type = "direct", tag = "proxy", bind_interface = interfaceName }) };
    }
    private static string PhysicalInterface() {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
            (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
            n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)) &&
            !new[] { "virtual", "hyper-v", "wintun", "wireguard", "sing-tun", "tap-", "vpn" }.Any(part => (n.Name + " " + n.Description).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        // Ambiguous multi-uplink hosts need route-aware selection, not an arbitrary baseline.
        return interfaces.Count == 1 ? interfaces[0].Name : null;
    }

    public async Task<AutomaticGamingResult> SelectAsync(InstalledGame game, IEnumerable<GameEndpoint> observed,
        IEnumerable<VpnProfile> profiles, string currentProfileId, string corePath, string runtime,
        IProgress<string> progress, CancellationToken token, bool compareDirect = true)
    {
        token.ThrowIfCancellationRequested();
        if (!compareDirect) return new AutomaticGamingResult { Message = "Прямое сравнение запрещено Kill Switch. Игровой VPN без сравнения с прямым соединением не включается." };
        if (!SupportsGameQuery(game)) return Unsupported(game);
        var candidates = profiles.Where(p => p != null && p.Protocol != "sing-box" && !string.IsNullOrEmpty(p.Id) && p.Id != "direct")
            .GroupBy(p => p.Id).Select(g => g.First()).ToList();
        if (candidates.Count == 0) return new AutomaticGamingResult { Message = "Добавьте серверы в NOVA: подбор использует ваши конфиги." };
        string physical = measurement == null ? PhysicalInterface() : "synthetic-test-interface";
        if (physical == null) return new AutomaticGamingResult { Message = "Не удалось однозначно определить прямой выход. Выигрыш у прямого соединения не подтверждён; игровой VPN не включён." };
        var direct = DirectProfile(physical);
        var targets = Targets(game, observed).Where(t => t.GameProtocol).ToList();
        if (targets.Count == 0) return new AutomaticGamingResult { Message = "Нет проверяемого UDP query-сервера игры. Игровой VPN не включён; текущая маршрутизация сохранена." };
        var quick = new Dictionary<string, List<GamingProbeResult>>();
        foreach (var target in targets) {
            var results = new List<GamingProbeResult>();
            // Each batch includes its own concurrent direct control; at most four probe cores.
            for (int start = 0; start < candidates.Count; start += 3) {
                token.ThrowIfCancellationRequested();
                progress?.Report("Сравниваем ваши серверы с прямым соединением: " + Math.Min(start + 3, candidates.Count) + "/" + candidates.Count);
                results.AddRange(await MeasureAsync(target.Target, candidates.Skip(start).Take(3).Concat(new[] { direct }), 3, corePath, runtime, token));
            }
            quick[target.Target.Key] = results;
        }
        // Query-port guesses are usable only if the direct control answered. A VPN-only
        // answer cannot establish an improvement over a missing baseline.
        var usable = targets.Where(t => quick[t.Target.Key].Any(r => r.ProfileId == "direct" && r.Samples.Count >= 2)).ToList();
        if (usable.Count == 0) return new AutomaticGamingResult { Message = "Нет надёжного прямого ответа игрового query-сервера. Сравнение невозможно; игровой VPN не включён." };
        var ranked = candidates.Where(p => usable.All(t => quick[t.Target.Key].Any(r => r.ProfileId == p.Id && r.Samples.Count >= 2)))
            .OrderBy(p => usable.Average(t => quick[t.Target.Key].First(r => r.ProfileId == p.Id).Median)).ToList();
        if (ranked.Count == 0) return new AutomaticGamingResult { Message = "Не удалось подобрать подходящий конфиг: VPN не отвечает на тех же игровых целях, что прямое соединение. Лучше остаться на прямом соединении." };
        var comparisons = ranked.ToDictionary(p => p.Id, p => new List<GamingRouteComparison>());
        var controls = new List<GamingRouteComparison>();
        foreach (var target in usable) {
            // Every reachable user profile receives the full series, not just a top-three
            // shortlist based on a few lucky packets. Each batch has a simultaneous control.
            for (int start = 0; start < ranked.Count; start += 3) {
                progress?.Report("Уточняем VPN и прямой выход: " + Math.Min(start + 3, ranked.Count) + "/" + ranked.Count + " · по 10 измерений");
                token.ThrowIfCancellationRequested();
                var batch = ranked.Skip(start).Take(3).ToList();
                var rows = await MeasureAsync(target.Target, batch.Concat(new[] { direct }), 10, corePath, runtime, token);
                var control = rows.Count(row => row.ProfileId == "direct") == 1 ? rows.First(row => row.ProfileId == "direct") : null;
                if (control == null || !control.Reliable) return new AutomaticGamingResult { Message = "Прямой маршрут измерен ненадёжно. Выигрыш VPN не подтверждён; текущая маршрутизация сохранена." };
                controls.Add(new GamingRouteComparison { TargetKey = target.Target.Key, Direct = control, Candidate = control });
                foreach (var candidate in batch) {
                    if (rows.Count(row => row.ProfileId == candidate.Id) != 1) continue;
                    comparisons[candidate.Id].Add(new GamingRouteComparison { TargetKey = target.Target.Key, Direct = control, Candidate = rows.First(row => row.ProfileId == candidate.Id) });
                }
            }
        }
        token.ThrowIfCancellationRequested();
        var qualified = comparisons.Where(pair => pair.Value.Count == usable.Count &&
            BeatsDirect(pair.Value.Select(c => c.Direct).ToList(), pair.Value.Select(c => c.Candidate).ToList()));
        string winner = qualified.OrderBy(pair => pair.Value.Average(c => c.Candidate.Median))
            .ThenBy(pair => pair.Value.Average(c => c.Candidate.P95)).Select(pair => pair.Key).FirstOrDefault() ?? "direct";
        var comparison = winner == "direct" ? controls : comparisons[winner];
        return new AutomaticGamingResult { ProfileId = winner, GameProtocol = true, KeptCurrent = winner == currentProfileId,
            Measurements = comparison.Select(c => c.Candidate).ToList(), Comparisons = comparison,
            Message = winner == "direct" ? "Не удалось подобрать подходящий конфиг с устойчиво меньшей задержкой. Лучше остаться на прямом соединении." :
                "Подобран конфиг с меньшим откликом игрового query-сервера, чем напрямую. Проверьте пинг в матче: query-ответ не гарантирует такой же выигрыш в игре." };
    }

    public static bool BeatsDirect(IList<GamingProbeResult> direct, IList<GamingProbeResult> candidate) {
        if (direct.Count == 0 || direct.Count != candidate.Count || direct.Any(r => r == null || !r.Reliable) || candidate.Any(r => r == null || !r.Reliable)) return false;
        for (int i = 0; i < direct.Count; i++) {
            // No averaging away a worse destination, tail, or loss.
            if (candidate[i].Median >= direct[i].Median || candidate[i].P95 > direct[i].P95 || candidate[i].FailureRate > direct[i].FailureRate) return false;
        }
        double baseline = direct.Average(r => r.Median), selected = candidate.Average(r => r.Median);
        return baseline - selected >= Math.Max(5, baseline * 0.1);
    }
    public static string Choose(IList<List<GamingProbeResult>> measurements, string currentProfileId) {
        if (measurements.Count == 0 || measurements.Any(rows => rows.Count(r => r.ProfileId == "direct") != 1)) return null;
        var direct = measurements.Select(rows => rows.First(r => r.ProfileId == "direct")).ToList();
        if (direct.Any(r => !r.Reliable)) return null;
        var eligible = measurements[0].Where(r => r.ProfileId != null && r.ProfileId != "direct").Select(r => r.ProfileId).Distinct()
            .Where(id => measurements.All(rows => rows.Count(r => r.ProfileId == id) == 1))
            .Where(id => BeatsDirect(direct, measurements.Select(rows => rows.First(r => r.ProfileId == id)).ToList())).ToList();
        // Prefer lowest median among all qualified candidates, rather than the active VPN.
        return eligible.OrderBy(id => measurements.Average(rows => rows.First(r => r.ProfileId == id).Median))
            .ThenBy(id => measurements.Average(rows => rows.First(r => r.ProfileId == id).P95)).FirstOrDefault() ?? "direct";
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Memory-only validity token. No profile keys or serialized configs are written to a journal.
public sealed class GamingRecommendationStamp
{
    private readonly string signature;
    private readonly DateTime measuredUtc;
    public GamingRecommendationStamp(InstalledGame game, IEnumerable<VpnProfile> profiles, string network,
        string baselineId, bool killSwitch, VpnEngine engine, CoreStatus status, DateTime utc)
    {
        signature = Signature(game, profiles, network, baselineId, killSwitch, engine, status); measuredUtc = utc;
    }
    public bool IsCurrent(InstalledGame game, IEnumerable<VpnProfile> profiles, string network,
        string baselineId, bool killSwitch, VpnEngine engine, CoreStatus status, DateTime utc)
    {
        return utc >= measuredUtc && utc - measuredUtc <= TimeSpan.FromMinutes(5) &&
            Matches(game, profiles, network, baselineId, killSwitch, engine, status);
    }
    public bool Matches(InstalledGame game, IEnumerable<VpnProfile> profiles, string network,
        string baselineId, bool killSwitch, VpnEngine engine, CoreStatus status) => signature == Signature(game, profiles, network, baselineId, killSwitch, engine, status);
    private static string Signature(InstalledGame game, IEnumerable<VpnProfile> profiles, string network,
        string baselineId, bool killSwitch, VpnEngine engine, CoreStatus status)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var configuration = new List<string>();
        foreach (var profile in profiles.Where(p => p.Protocol != "sing-box").OrderBy(p => p.Id, StringComparer.Ordinal)) {
            // Eligibility is represented by the caller's filtered set. Only connection
            // configuration belongs here; background health/aliases must not invalidate a game measurement.
            configuration.Add(serializer.Serialize(new { profile.Id, Configuration = SubscriptionManager.ConfigKey(profile) }));
        }
        var files = new List<string>();
        foreach (string path in game.Executables.Where(p => !string.IsNullOrWhiteSpace(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            try { var file = new FileInfo(path); files.Add(Path.GetFullPath(path).ToLowerInvariant() + ":" + (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing")); }
            catch (IOException) { files.Add(path + ":unavailable"); }
            catch (UnauthorizedAccessException) { files.Add(path + ":denied"); }
        }
        string region = GameAdapter.ReadRegionHint(game, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string input = serializer.Serialize(new { game.Id, game.LaunchUri, game.LauncherPath, game.LauncherProduct, region, files, configuration, network, baselineId, killSwitch, engine, status });
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "");
    }
}

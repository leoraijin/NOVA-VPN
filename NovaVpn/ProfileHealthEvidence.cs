using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NovaVpn;

public static class ProfileHealthEvidence
{
    public static string MeasurementLabel(VpnProfile profile)
    {
        if (profile == null || profile.LastLatency < 1 || profile.HealthMeasurement != "https-profile") return "Нужна проверка HTTPS через профиль";
        int minutes = Math.Max(0, (int)(DateTime.UtcNow - profile.LastCheckedUtc).TotalMinutes);
        return "HTTPS-отклик " + profile.LastLatency + " мс · " + (minutes == 0 ? "только что" : minutes + " мин назад") + (minutes >= 3 ? " · результат устарел" : "");
    }
    public static string NetworkKey()
    {
        try
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(GameCatalog.NetworkKey())));
        }
        catch { return ""; }
    }

    public static string CoreKey(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks : ""; }
        catch { return ""; }
    }

    public static bool IsCurrent(VpnProfile profile, DateTime now, string networkKey, string coreKey)
    {
        if (profile == null || profile.RetiredFromSubscription || profile.LastLatency <= 0 || profile.ConsecutiveFailures != 0 ||
            profile.HealthMeasurement != "https-profile" || string.IsNullOrEmpty(networkKey) || string.IsNullOrEmpty(coreKey)) return false;
        var age = now - profile.LastCheckedUtc;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(3) && profile.HealthNetworkKey == networkKey &&
            profile.HealthCoreKey == coreKey && profile.HealthProfileKey == SubscriptionManager.ConfigKey(profile);
    }

    public static void Record(VpnProfile profile, ServerMapHealth health)
    {
        if (profile == null) return;
        profile.LastCheckedUtc = health.CheckedUtc;
        profile.LastLatency = health.Success ? health.LatencyMs : -1;
        profile.HealthMeasurement = health.Success ? "https-profile" : "";
        profile.HealthProfileKey = health.ProfileKey; profile.HealthNetworkKey = health.NetworkKey; profile.HealthCoreKey = health.CoreKey;
        profile.HealthTarget = health.Target;
        profile.HealthStatus = !health.Supported ? "Не проверен" : health.Success ? "HTTPS работает" : "Проверка не прошла";
        profile.LastTestMessage = health.Message;
        profile.LastLossPercent = -1;
        if (health.Success) { profile.AverageLatency = health.LatencyMs; profile.ConsecutiveFailures = 0; }
        else if (health.Supported) profile.ConsecutiveFailures = Math.Min(1000, profile.ConsecutiveFailures + 1);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class SubscriptionRecord
{
    [DataMember] public string Id = Guid.NewGuid().ToString("N");
    [DataMember] public string Url = "";
    [DataMember] public string Name = "";
    [DataMember] public DateTime LastAttemptUtc;
    [DataMember] public DateTime LastSuccessUtc;
    [DataMember] public DateTime NextAttemptUtc;
    [DataMember] public int ConsecutiveFailures;
    [DataMember] public string LastError = "";
    [DataMember] public long Revision;
    [DataMember] public SubscriptionQuota Quota;
    [DataMember] public SubscriptionSnapshot Previous;
    [DataMember] public string LastChangeSummary = "";
    [DataMember] public string LastNotifiedQuotaState = "";
    [DataMember] public bool QuotaNotificationsEnabled = true;
    [DataMember] public int WarningDays = 3;
    [DataMember] public int WarningRemainingPercent = 10;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : SafeHost(Url);
    public static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "Подписка";
}

[DataContract]
public sealed class SubscriptionQuota
{
    [DataMember] public long? UploadBytes;
    [DataMember] public long? DownloadBytes;
    [DataMember] public long? TotalBytes;
    [DataMember] public DateTime? ExpiresUtc;
    [DataMember] public DateTime ReceivedUtc;
    [DataMember] public List<string> Warnings = new List<string>();
    public bool HasValues => UploadBytes.HasValue || DownloadBytes.HasValue || TotalBytes.HasValue || ExpiresUtc.HasValue;
    public long? UsedBytes => UploadBytes.HasValue && DownloadBytes.HasValue && UploadBytes.Value <= long.MaxValue - DownloadBytes.Value
        ? UploadBytes.Value + DownloadBytes.Value : (long?)null;
    public long? RemainingBytes => TotalBytes.HasValue && TotalBytes.Value > 0 && UsedBytes.HasValue
        ? Math.Max(0L, TotalBytes.Value - UsedBytes.Value) : (long?)null;

    // This is provider metadata, never a local interface traffic counter.
    public static SubscriptionQuota Parse(IEnumerable<string> headers, DateTime receivedUtc)
    {
        var result = new SubscriptionQuota { ReceivedUtc = receivedUtc };
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string header in headers ?? Enumerable.Empty<string>())
        foreach (string item in (header ?? "").Split(';'))
        {
            int equal = item.IndexOf('=');
            if (equal < 1) continue;
            string key = item.Substring(0, equal).Trim(), value = item.Substring(equal + 1).Trim();
            if (key != "" && fields.TryGetValue(key, out string previous) && previous != value) ambiguous.Add(key);
            fields[key] = value;
        }
        long? Read(string key)
        {
            if (!fields.TryGetValue(key, out string value)) return null;
            if (!ambiguous.Contains(key) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number)) return number;
            result.Warnings.Add("Некорректное поле тарифа: " + key); return null;
        }
        result.UploadBytes = Read("upload"); result.DownloadBytes = Read("download"); result.TotalBytes = Read("total");
        long? expiry = Read("expire");
        if (expiry.HasValue && expiry.Value > 0)
        {
            if (expiry.Value <= 253402214399L) result.ExpiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expiry.Value);
            else result.Warnings.Add("Некорректный срок действия тарифа");
        }
        if (result.UploadBytes.HasValue && result.DownloadBytes.HasValue && !result.UsedBytes.HasValue)
            result.Warnings.Add("Счётчики тарифа превышают допустимый размер");
        return result;
    }
}

[DataContract]
public sealed class SubscriptionSnapshot
{
    [DataMember] public List<VpnProfile> Profiles = new List<VpnProfile>();
    [DataMember] public string SelectedProfileId;
    [DataMember] public SubscriptionQuota Quota;
    [DataMember] public DateTime CreatedUtc;
}

public sealed class SubscriptionChangePlan
{
    public string SubscriptionUrl { get; internal set; }
    public string RecordId { get; internal set; }
    public long ExpectedRevision { get; internal set; }
    internal string ExpectedProfiles;
    internal ImportResult Imported;
    public int Added { get; internal set; }
    public int Changed { get; internal set; }
    public int Removed { get; internal set; }
    public int Unchanged { get; internal set; }
    public bool IsPartial => Imported!=null && Imported.Warnings.Count>0;
    public string Summary => "Добавлено: " + Added + " · изменено: " + Changed + " · удалено: " + Removed + " · без изменений: " + Unchanged + (IsPartial ? " · частичный импорт, прежние серверы сохранены" : "");
}

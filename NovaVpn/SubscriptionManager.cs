using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.Serialization;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public static class SubscriptionManager
{
    private static readonly SemaphoreSlim UpdateGate = new SemaphoreSlim(1, 1);
    public static bool IsUpdating => UpdateGate.CurrentCount == 0;

    public static void Normalize(AppState state)
    {
        if (state == null) return;
        if (state.Profiles == null) state.Profiles = new List<VpnProfile>();
        state.Profiles.RemoveAll(p => p == null);
        if (state.Subscriptions == null) state.Subscriptions = new List<SubscriptionRecord>();
        state.Subscriptions.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Url));
        foreach (var record in state.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(record.Id)) record.Id = Guid.NewGuid().ToString("N");
            record.ConsecutiveFailures = Math.Max(0, record.ConsecutiveFailures);
            record.Revision = Math.Max(0, record.Revision);
            if (record.Quota != null && record.Quota.Warnings == null) record.Quota.Warnings = new List<string>();
        }
        foreach (var profile in state.Profiles.Where(p => p != null))
        {
            if (string.IsNullOrWhiteSpace(profile.ProviderName))
            {
                profile.ProviderName = SourceName(profile);
                if (profile.Name != profile.ProviderName) profile.UserName = profile.Name;
            }
            profile.UserName = profile.UserName ?? "";
            if (string.IsNullOrWhiteSpace(profile.SubscriptionUrl)) continue;
            var record = state.Subscriptions.FirstOrDefault(s => SameUrl(s.Url, profile.SubscriptionUrl));
            if (record == null)
            {
                record = new SubscriptionRecord { Url = profile.SubscriptionUrl,
                    LastSuccessUtc = profile.SubscriptionUpdatedUtc != default(DateTime) ? profile.SubscriptionUpdatedUtc : state.LastSubscriptionUpdateUtc };
                if (record.LastSuccessUtc != default(DateTime) && record.LastSuccessUtc.Year < 9998)
                    record.NextAttemptUtc = NextSuccess(record.LastSuccessUtc, state.SubscriptionUpdateIntervalHours);
                state.Subscriptions.Add(record);
            }
            profile.SubscriptionId = record.Id;
        }
        if (state.SchemaVersion < 12) state.SchemaVersion = 12;
    }

    public static void Rename(VpnProfile profile, string name)
    {
        if (profile == null || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Введите название сервера.");
        if (string.IsNullOrWhiteSpace(profile.ProviderName)) profile.ProviderName = SourceName(profile);
        profile.UserName = name.Trim() == profile.ProviderName ? "" : name.Trim();
        profile.Name = string.IsNullOrWhiteSpace(profile.UserName) ? profile.ProviderName : profile.UserName;
    }
    // Subscription tokens and paths can be case-sensitive. Never match across sources.
    public static bool SameUrl(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    public static SubscriptionChangePlan Preview(AppState state, ImportResult imported)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (imported == null || !imported.IsSubscription) throw new ArgumentException("Импорт не является подпиской.");
        if(imported.Profiles.Count==0)throw new InvalidOperationException("Подписка не содержит распознанных серверов. Прежние серверы сохранены.");
        var record = (state.Subscriptions ?? new List<SubscriptionRecord>()).FirstOrDefault(s => SameUrl(s.Url, imported.SubscriptionUrl));
        var old = ProfilesFor(state, imported.SubscriptionUrl);
        var plan = new SubscriptionChangePlan { SubscriptionUrl = imported.SubscriptionUrl, RecordId = record?.Id ?? Guid.NewGuid().ToString("N"),
            ExpectedRevision = record?.Revision ?? 0, ExpectedProfiles = Signature(old),
            Imported = new ImportResult(Clone(imported.Profiles), imported.SubscriptionUrl) { Quota = Clone(imported.Quota) } };
        plan.Imported.Warnings.AddRange(imported.Warnings);
        plan.Imported.RoutingPreviews.AddRange(imported.RoutingPreviews);
        var remaining = new HashSet<VpnProfile>(old);
        var byConfig = remaining.GroupBy(MatchingConfigKey).ToDictionary(g => g.Key, g => g.ToArray());
        var byEndpoint = remaining.Where(p=>!p.RetiredFromSubscription).GroupBy(EndpointKey).ToDictionary(g => g.Key, g => g.ToArray());
        var incomingEndpoints=plan.Imported.Profiles.GroupBy(EndpointKey).ToDictionary(g=>g.Key,g=>g.Count());
        var exactMatches=new Dictionary<VpnProfile,VpnProfile>();
        foreach(var incoming in plan.Imported.Profiles)if(string.IsNullOrWhiteSpace(incoming.ProviderName))incoming.ProviderName=incoming.Name;
        // Reserve all exact identities before any endpoint-based key rotation.
        // Match provider labels first among identical connections, then remaining
        // exact connections. A new row cannot steal an ID from a later exact row.
        foreach(var incoming in plan.Imported.Profiles) {
            if(!byConfig.TryGetValue(MatchingConfigKey(incoming),out var exact))continue;
            var previous=exact.FirstOrDefault(p=>remaining.Contains(p)&&p.ProviderName==incoming.ProviderName);
            if(previous!=null){exactMatches.Add(incoming,previous);remaining.Remove(previous);}
        }
        foreach(var incoming in plan.Imported.Profiles.Where(p=>!exactMatches.ContainsKey(p))) {
            if(!byConfig.TryGetValue(MatchingConfigKey(incoming),out var exact))continue;
            var previous=exact.FirstOrDefault(remaining.Contains);
            if(previous!=null){exactMatches.Add(incoming,previous);remaining.Remove(previous);}
        }
        foreach (var incoming in plan.Imported.Profiles)
        {
            exactMatches.TryGetValue(incoming,out var previous);
            if (previous == null && !plan.IsPartial && CanRotateAtEndpoint(incoming) &&
                incomingEndpoints[EndpointKey(incoming)]==1 && byEndpoint.TryGetValue(EndpointKey(incoming), out var endpoints) &&
                endpoints.Length==1 && remaining.Contains(endpoints[0]) && CanRotateAtEndpoint(endpoints[0]))previous=endpoints[0];
            if (previous == null) { plan.Added++; continue; }
            remaining.Remove(previous);
            bool sameConfig = SourceConfigKey(previous) == SourceConfigKey(incoming);
            if (sameConfig && previous.ProviderName == incoming.ProviderName) plan.Unchanged++; else plan.Changed++;
            incoming.Id = previous.Id; incoming.UserName = previous.UserName ?? "";
            incoming.Name = string.IsNullOrWhiteSpace(incoming.UserName) ? incoming.ProviderName : incoming.UserName;
            incoming.IsFavorite = previous.IsFavorite; incoming.AddedAt = previous.AddedAt;
            incoming.FallbackProfileIds = (previous.FallbackProfileIds ?? new List<string>()).ToList();
            incoming.DeclaredCountryCode = previous.DeclaredCountryCode;
            incoming.CorporateProxy = previous.CorporateProxy?.Copy();
            if (sameConfig && !previous.RetiredFromSubscription)
            {
                incoming.LastLatency = previous.LastLatency; incoming.AverageLatency = previous.AverageLatency;
                incoming.LastCheckedUtc = previous.LastCheckedUtc; incoming.LastLossPercent = previous.LastLossPercent;
                incoming.ConsecutiveFailures = previous.ConsecutiveFailures; incoming.HealthStatus = previous.HealthStatus;
                incoming.LastTestMessage = previous.LastTestMessage;
                incoming.HealthMeasurement = previous.HealthMeasurement; incoming.HealthProfileKey = previous.HealthProfileKey;
                incoming.HealthNetworkKey = previous.HealthNetworkKey; incoming.HealthCoreKey = previous.HealthCoreKey;
                incoming.HealthTarget = previous.HealthTarget;
            } else {
                incoming.LastLatency=incoming.AverageLatency=incoming.LastLossPercent=-1;
                incoming.LastCheckedUtc=default(DateTime);incoming.ConsecutiveFailures=0;
                incoming.HealthStatus="Не проверен";incoming.LastTestMessage="";
                incoming.HealthMeasurement=incoming.HealthProfileKey=incoming.HealthNetworkKey=incoming.HealthCoreKey=incoming.HealthTarget="";
            }
        }
        plan.Removed = plan.IsPartial ? 0 : remaining.Count(p=>!p.RetiredFromSubscription);
        return plan;
    }

    public static void Apply(AppState state, SubscriptionChangePlan plan, string activeProfileId = null, IEnumerable<string> ownedProfileIds = null)
    {
        if (state == null || plan == null) throw new ArgumentNullException();
        var old = ProfilesFor(state, plan.SubscriptionUrl);
        var record = (state.Subscriptions ?? new List<SubscriptionRecord>()).FirstOrDefault(s => SameUrl(s.Url, plan.SubscriptionUrl));
        if ((record?.Revision ?? 0) != plan.ExpectedRevision || Signature(old) != plan.ExpectedProfiles)
            throw new InvalidOperationException("Подписка или пользовательские правки изменились. Повторите предварительный просмотр.");
        if (state.Subscriptions == null) state.Subscriptions = new List<SubscriptionRecord>();
        if (record == null) { record = new SubscriptionRecord { Id = plan.RecordId, Url = plan.SubscriptionUrl }; state.Subscriptions.Add(record); }
        var now = DateTime.UtcNow;
        var incoming = Clone(plan.Imported.Profiles);
        foreach (var p in incoming) { p.SubscriptionId = record.Id; p.SubscriptionUrl = record.Url; p.SubscriptionUpdatedUtc = now; p.RetiredFromSubscription = false; }
        int index = old.Count == 0 ? state.Profiles.Count : old.Select(p => state.Profiles.IndexOf(p)).Min();
        record.Previous = new SubscriptionSnapshot { Profiles = Clone(old), SelectedProfileId = state.SelectedProfileId, Quota = Clone(record.Quota), CreatedUtc = now };
        // Keep a removed active profile until its session ends; refresh never restarts the core.
        var retainedIds = new HashSet<string>(MultiServerSettings.ProfileReferences(state),StringComparer.Ordinal);
        if(activeProfileId!=null)retainedIds.Add(activeProfileId);
        if(ownedProfileIds!=null)retainedIds.UnionWith(ownedProfileIds);
        var retained = old.Where(p => (plan.IsPartial || retainedIds.Contains(p.Id)) && !incoming.Any(n => n.Id == p.Id)).ToArray();
        state.Profiles.RemoveAll(p => old.Contains(p));
        state.Profiles.InsertRange(Math.Min(index, state.Profiles.Count), incoming);
        foreach(var profile in retained) { if(!plan.IsPartial)profile.RetiredFromSubscription = true; state.Profiles.Add(profile); }
        if (!state.Profiles.Any(p => p.Id == state.SelectedProfileId)) state.SelectedProfileId = incoming.FirstOrDefault()?.Id ?? state.Profiles.FirstOrDefault()?.Id;
        if (plan.Imported.Quota != null && (plan.Imported.Quota.HasValues || plan.Imported.Quota.Warnings.Count > 0)) record.Quota = Clone(plan.Imported.Quota);
        if(plan.IsPartial) {
            RecordFailure(record,new FormatException(),now,now);
            record.LastError="Подписка распознана не полностью. Прежние серверы сохранены.";
        } else {
            record.LastAttemptUtc = record.LastSuccessUtc = now; record.NextAttemptUtc = NextSuccess(now, state.SubscriptionUpdateIntervalHours);
            record.ConsecutiveFailures = 0; record.LastError = ""; state.LastSubscriptionUpdateUtc = now;
        }
        record.LastChangeSummary = plan.Summary; record.Revision++;
        // LastSubscriptionUpdateUtc is advanced only for a complete response. // Legacy export compatibility; scheduling is per subscription.
    }

    public static void MergeImported(AppState state, ImportResult imported) { Normalize(state); Apply(state, Preview(state, imported)); }

    public static bool Undo(AppState state, string subscriptionId, string activeProfileId = null, IEnumerable<string> ownedProfileIds = null)
    {
        if (!UpdateGate.Wait(0)) throw new InvalidOperationException("Дождитесь завершения обновления подписок перед откатом.");
        try { return UndoCore(state, subscriptionId, activeProfileId, ownedProfileIds); }
        finally { UpdateGate.Release(); }
    }
    private static bool UndoCore(AppState state, string subscriptionId, string activeProfileId, IEnumerable<string> ownedProfileIds)
    {
        Normalize(state);
        var record = state.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
        if (record?.Previous == null) return false;
        var current = ProfilesFor(state, record.Url); var previous = record.Previous;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var foreignIds = new HashSet<string>(state.Profiles.Where(p => !current.Contains(p)).Select(p => p.Id), StringComparer.Ordinal);
        if (previous.Profiles == null || previous.Profiles.Any(p => p == null || String.IsNullOrWhiteSpace(p.Id) ||
            !SameUrl(p.SubscriptionUrl,record.Url) || !ids.Add(p.Id) || foreignIds.Contains(p.Id)))
            throw new InvalidOperationException("Снимок подписки содержит другой источник или конфликтующие идентификаторы. Список серверов не изменён.");
        var activeIds=new HashSet<string>(ownedProfileIds??Enumerable.Empty<string>(),StringComparer.Ordinal);
        if(activeProfileId!=null)activeIds.Add(activeProfileId);
        if (current.Any(p => activeIds.Contains(p.Id)) || previous.Profiles.Any(p => activeIds.Contains(p.Id)))
            throw new InvalidOperationException("Отключите профиль этой подписки перед отменой обновления.");
        // Prepare the entire restored set before mutating state. Undo reverses
        // provider data; it must not rewind later local policy/name edits.
        var restored = Clone(previous.Profiles);
        var local = current.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Single());
        foreach (var profile in restored)
        {
            if (String.IsNullOrWhiteSpace(profile.ProviderName)) profile.ProviderName = SourceName(profile);
            if (!local.TryGetValue(profile.Id, out var edited)) continue;
            if (edited.HasMissingCorporateBase || edited.EffectivePortableOutboundJson == CorporateProxy.ExportMarker())
                throw new InvalidOperationException("Не удалось сохранить обязательную корпоративную цепочку. Восстановите полные параметры профиля перед откатом.");
            string priorKey = ConfigKey(profile);
            profile.UserName = edited.UserName ?? "";
            profile.Name = String.IsNullOrWhiteSpace(profile.UserName) ? profile.ProviderName : profile.UserName;
            profile.IsFavorite = edited.IsFavorite;
            profile.FallbackProfileIds = (edited.FallbackProfileIds ?? new List<string>()).ToList();
            profile.DeclaredCountryCode = edited.DeclaredCountryCode;
            profile.SetCorporateProxy(edited.CorporateProxy);
            if (ConfigKey(profile) != priorKey)
            {
                profile.LastLatency = profile.AverageLatency = profile.LastLossPercent = -1;
                profile.LastCheckedUtc = default(DateTime); profile.ConsecutiveFailures = 0;
                profile.HealthStatus = "Не проверен"; profile.LastTestMessage = "";
                profile.HealthMeasurement = profile.HealthProfileKey = profile.HealthNetworkKey = profile.HealthCoreKey = profile.HealthTarget = "";
            }
        }
        var restoredQuota = Clone(previous.Quota);
        string selected = state.SelectedProfileId;
        int index = current.Count == 0 ? state.Profiles.Count : current.Select(p => state.Profiles.IndexOf(p)).Min();
        state.Profiles.RemoveAll(p => current.Contains(p));
        state.Profiles.InsertRange(Math.Min(index, state.Profiles.Count), restored);
        record.Quota = restoredQuota; record.Previous = null; record.Revision++;
        record.LastChangeSummary = "Последнее обновление отменено";
        if (!state.Profiles.Any(p => p.Id == selected))
            state.SelectedProfileId = restored.FirstOrDefault(p => p.Id == previous.SelectedProfileId)?.Id ??
                restored.FirstOrDefault()?.Id ?? state.Profiles.FirstOrDefault()?.Id;
        return true;
    }

    public static DateTime? NextDueUtc(AppState state) { Normalize(state); return state.Subscriptions.Count == 0 ? (DateTime?)null : state.Subscriptions.Min(s => s.NextAttemptUtc); }

    public static Task<SubscriptionUpdateResult> UpdateAllAsync(AppState state, bool dueOnly = false, string activeProfileId = null, CancellationToken token = default(CancellationToken), Func<bool> canApply = null, IEnumerable<string> ownedProfileIds = null) =>
        UpdateAsync(state, LinkParser.ImportWithCancellationAsync, StateStore.Save, dueOnly, activeProfileId, token, canApply, ownedProfileIds);

    public static async Task<SubscriptionUpdateResult> UpdateAsync(AppState state, Func<string, CancellationToken, Task<ImportResult>> fetch,
        Action<AppState> persist, bool dueOnly = false, string activeProfileId = null, CancellationToken token = default(CancellationToken), Func<bool> canApply = null, IEnumerable<string> ownedProfileIds = null)
    {
        if (state == null || fetch == null || persist == null) throw new ArgumentNullException();
        var capturedOwners=(ownedProfileIds??Enumerable.Empty<string>()).ToArray();
        void CheckOwner(){token.ThrowIfCancellationRequested();if(canApply!=null&&!canApply())throw new InvalidOperationException("Состояние подключения изменилось. Повторите обновление подписок.");}
        CheckOwner();
        await UpdateGate.WaitAsync(token);
        try
        {
            CheckOwner();
            Normalize(state);var report=new SubscriptionUpdateResult();var fetched=new List<PendingFetch>();
            foreach(var record in state.Subscriptions.ToArray())
            {
                CheckOwner();if(dueOnly&&record.NextAttemptUtc>DateTime.UtcNow)continue;
                var item=new PendingFetch{Record=record,Id=record.Id,Url=record.Url,Revision=record.Revision,AttemptUtc=DateTime.UtcNow};fetched.Add(item);report.SubscriptionCount++;
                try{
                    var imported=await fetch(item.Url,token);token.ThrowIfCancellationRequested();
                    if(!SameUrl(imported.SubscriptionUrl,item.Url))throw new InvalidOperationException("Ответ относится к другой подписке.");
                    // Preview freezes provider data now; later source fetches cannot alter it.
                    item.Imported=Preview(state,imported).Imported;
                }catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                catch(Exception error){item.Error=error;}
                CheckOwner();
            }
            CheckOwner();
            foreach(var item in fetched)if(!state.Subscriptions.Any(r=>ReferenceEquals(r,item.Record)&&r.Id==item.Id&&SameUrl(r.Url,item.Url)&&r.Revision==item.Revision))throw new InvalidOperationException("Подписка изменилась во время загрузки. Повторите обновление.");
            var commit=SubscriptionCommitPlan.PrepareBatch(state,candidate=>{
                foreach(var item in fetched){
                    var record=candidate.Subscriptions.Single(r=>r.Id==item.Id);record.LastAttemptUtc=item.AttemptUtc;
                    try{
                        if(item.Error!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error).Throw();
                        var plan=Preview(candidate,item.Imported);Apply(candidate,plan,activeProfileId,capturedOwners);
                        if(plan.IsPartial)report.PartialSubscriptionCount++;
                        report.UpdatedProfiles+=item.Imported.Profiles.Count;report.Changes.Add(record.DisplayName+": "+plan.Summary);
                        report.Warnings.AddRange(item.Imported.Warnings);report.Warnings.AddRange(item.Imported.RoutingPreviews.Select(p=>p.Summary));
                    }catch(Exception ex){
                        RecordFailure(record,ex,item.AttemptUtc,DateTime.UtcNow);
                        report.Errors.Add(record.DisplayName+": "+record.LastError);
                    }
                }
            });
            commit.Apply(state,()=>!token.IsCancellationRequested&&(canApply==null||canApply()),persist);return report;
        }
        finally { UpdateGate.Release(); }
    }

    private sealed class PendingFetch
    {
        public SubscriptionRecord Record;public string Id,Url;public long Revision;public DateTime AttemptUtc;public ImportResult Imported;public Exception Error;
    }
    internal static void RecordFailure(SubscriptionRecord record,Exception error,DateTime attemptUtc,DateTime nowUtc)
    {
        record.LastAttemptUtc=StorageUtc(attemptUtc);
        record.ConsecutiveFailures=Math.Min(19,Math.Max(0,record.ConsecutiveFailures))+1;
        record.NextAttemptUtc=StorageUtc(nowUtc.AddMinutes(Math.Min(60,2*Math.Pow(2,Math.Min(5,record.ConsecutiveFailures-1)))));
        record.LastError=error is System.Net.Http.HttpRequestException?"Ошибка HTTP при получении подписки":error is OperationCanceledException?"Сервер не ответил вовремя":error is FormatException||error is NotSupportedException?"Не удалось разобрать ответ подписки":"Не удалось обновить подписку";
    }
    private static DateTime StorageUtc(DateTime value) => new DateTime(value.Ticks-value.Ticks%TimeSpan.TicksPerMillisecond,DateTimeKind.Utc);
    private static DateTime NextSuccess(DateTime now, int hours) => now.AddHours(Math.Max(1, Math.Min(168, hours)));
    private static List<VpnProfile> ProfilesFor(AppState state, string url) => (state.Profiles ?? new List<VpnProfile>()).Where(p => p != null && SameUrl(p.SubscriptionUrl, url)).ToList();
    public static string ConfigKey(VpnProfile p) => CorporateProxy.Enabled(p) ? Hash(SourceConfigKey(p) + "\n" + new JavaScriptSerializer().Serialize(p.CorporateProxy)) : SourceConfigKey(p);
    private static string SourceConfigKey(VpnProfile p) => Hash(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new { p.Protocol, p.Server, p.Port, p.UserId, p.Password, p.Security, p.Transport, p.Host, p.Path,
        p.Sni, p.PublicKey, p.ShortId, p.Fingerprint, p.Flow, p.Method, p.Obfs, p.ObfsPassword, p.RawJson, PortableOutboundJson = p.EffectivePortableOutboundJson }));
    private static string CanonicalConnection(object value)
    {
        var codec=new JavaScriptSerializer {MaxJsonLength=8*1024*1024};
        if(value is IDictionary<string,object> map)return "{"+String.Join(",",map.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>codec.Serialize(p.Key)+":"+CanonicalConnection(p.Value)))+"}";
        if(value is object[] array)return "["+String.Join(",",array.Select(CanonicalConnection))+"]";
        return codec.Serialize(value);
    }
    private static Dictionary<string,object> MatchingOutbound(VpnProfile p) =>
        new JavaScriptSerializer {MaxJsonLength=8*1024*1024}.Deserialize<Dictionary<string,object>>(ConfigBuilder.ExportBaseOutbound(p));
    private static bool IsOpaque(VpnProfile p) => ConfigBuilder.RequiresXrayTransport(p) || String.Equals(p.Protocol,"sing-box",StringComparison.OrdinalIgnoreCase)||!String.IsNullOrWhiteSpace(p.RawJson);
    private static string MatchingConfigKey(VpnProfile p) => IsOpaque(p)?"opaque:"+SourceConfigKey(p):Hash(CanonicalConnection(MatchingOutbound(p)));
    private static bool CanRotateAtEndpoint(VpnProfile p)
    {
        if(IsOpaque(p))return false;
        var outbound=MatchingOutbound(p);
        return !outbound.ContainsKey("detour") && (!outbound.TryGetValue("domain_resolver",out var resolver)||Equals(resolver,"local-dns"));
    }
    private static string EndpointKey(VpnProfile p)
    {
        if(!CanRotateAtEndpoint(p))return "opaque:"+SourceConfigKey(p);
        var outbound=MatchingOutbound(p);
        foreach(var key in new[]{"uuid","password","username","auth_str"})outbound.Remove(key);
        if(outbound.TryGetValue("auth",out var auth)&&auth is string)outbound.Remove("auth");
        if(outbound.TryGetValue("server",out var server)&&server is string host)outbound["server"]=host.ToLowerInvariant();
        if(outbound.TryGetValue("tls",out var rawTls)&&rawTls is Dictionary<string,object> tls) {
            if(tls.TryGetValue("server_name",out var name)&&name is string sni)tls["server_name"]=sni.ToLowerInvariant();
            if(tls.TryGetValue("reality",out var rawReality)&&rawReality is Dictionary<string,object> reality){reality.Remove("public_key");reality.Remove("short_id");}
        }
        if(outbound.TryGetValue("transport",out var rawTransport)&&rawTransport is Dictionary<string,object> transport) {
            if(transport.TryGetValue("host",out var rawHost)&&rawHost is string transportHost)transport["host"]=transportHost.ToLowerInvariant();
            if(transport.TryGetValue("headers",out var rawHeaders)&&rawHeaders is Dictionary<string,object> headers)
                foreach(var key in headers.Keys.Where(k=>String.Equals(k,"Host",StringComparison.OrdinalIgnoreCase)).ToArray())
                    if(headers[key] is string headerHost)headers[key]=headerHost.ToLowerInvariant();
        }
        if(outbound.TryGetValue("obfs",out var rawObfs)&&rawObfs is Dictionary<string,object> obfs)obfs.Remove("password");
        return Hash(CanonicalConnection(outbound));
    }
    private static string SourceName(VpnProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.Source) && Uri.TryCreate(p.Source, UriKind.Absolute, out var uri))
        {
            string fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            if (!string.IsNullOrWhiteSpace(fragment)) return fragment;
            if (!string.IsNullOrWhiteSpace(uri.Host)) return uri.Host;
        }
        return p.Name ?? "Сервер";
    }
    private static string Signature(List<VpnProfile> profiles) => Hash(Serialize(profiles));
    private static string Hash(string value) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value))); }
    private static DataContractJsonSerializer Serializer<T>() => new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings {
        DateTimeFormat = new DateTimeFormat("o", CultureInfo.InvariantCulture) { DateTimeStyles = DateTimeStyles.RoundtripKind } });
    private static string Serialize<T>(T value) { using (var stream = new MemoryStream()) { Serializer<T>().WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
    private static T Clone<T>(T value)
    {
        if (ReferenceEquals(value, null)) return default(T);
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(Serialize(value)))) return (T)Serializer<T>().ReadObject(stream);
    }
}

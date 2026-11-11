using System;
using System.Collections.Generic;
using System.Linq;
namespace NovaVpn;

// Synchronous UI commit: persist a private candidate before publishing subscription fields.
public sealed class SubscriptionCommitPlan
{
    readonly string baseline;readonly AppState prepared;readonly bool metadataOnly;
    SubscriptionCommitPlan(string baseline,AppState prepared,bool metadataOnly=false){this.baseline=baseline;this.prepared=prepared;this.metadataOnly=metadataOnly;}
    public static SubscriptionCommitPlan Prepare(AppState target,SubscriptionChangePlan change,string activeProfileId=null,IEnumerable<string> ownedProfileIds=null)
    {
        if(target==null||change==null)throw new ArgumentNullException("subscription commit");
        string baseline=FullStateRestorePlan.Fingerprint(target);var candidate=FullTransfer.Copy(target);
        SubscriptionManager.Apply(candidate,change,activeProfileId,ownedProfileIds);
        return new SubscriptionCommitPlan(baseline,StateStore.SnapshotForSave(candidate));
    }
    public static SubscriptionCommitPlan PrepareUndo(AppState target,string subscriptionId,string activeProfileId=null,IEnumerable<string> ownedProfileIds=null)
    {
        if(target==null||String.IsNullOrEmpty(subscriptionId))throw new ArgumentNullException("subscription undo");
        string baseline=FullStateRestorePlan.Fingerprint(target);var candidate=FullTransfer.Copy(target);
        if(!SubscriptionManager.Undo(candidate,subscriptionId,activeProfileId,ownedProfileIds))throw new InvalidOperationException("Нет обновления подписки для отмены.");
        return new SubscriptionCommitPlan(baseline,StateStore.SnapshotForSave(candidate));
    }
    internal static SubscriptionCommitPlan PrepareBatch(AppState target,Action<AppState> prepare,bool metadataOnly=false)
    {
        string baseline=FullStateRestorePlan.Fingerprint(target);var candidate=FullTransfer.Copy(target);prepare(candidate);
        StateStore.NormalizeForSerialization(candidate);
        return new SubscriptionCommitPlan(baseline,FullTransfer.Copy(candidate),metadataOnly);
    }
    public static SubscriptionCommitPlan PreparePreferences(AppState target,string subscriptionId,string name,bool notifications,int days,int remainingPercent)
    {
        if(days<1||days>365||remainingPercent<1||remainingPercent>99)throw new ArgumentOutOfRangeException("subscription warning threshold");
        return PrepareBatch(target,candidate=>{
            var records=candidate.Subscriptions.FindAll(record=>record.Id==subscriptionId);
            if(records.Count!=1)throw new InvalidOperationException("Подписка изменилась. Откройте её заново.");
            var record=records[0];record.Name=(name??"").Trim();
            if(record.QuotaNotificationsEnabled!=notifications||record.WarningDays!=days||record.WarningRemainingPercent!=remainingPercent)record.LastNotifiedQuotaState="";
            record.QuotaNotificationsEnabled=notifications;record.WarningDays=days;record.WarningRemainingPercent=remainingPercent;
        },true);
    }
    public static SubscriptionCommitPlan PrepareFailure(AppState target,string subscriptionId,string url,Exception error,DateTime attemptUtc,DateTime nowUtc)
    {
        if(target==null||error==null)throw new ArgumentNullException("subscription failure");
        if(attemptUtc.Kind!=DateTimeKind.Utc||nowUtc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC subscription attempt required");
        return PrepareBatch(target,candidate=>{
            var records=candidate.Subscriptions.FindAll(record=>record.Id==subscriptionId&&SubscriptionManager.SameUrl(record.Url,url));
            if(records.Count!=1)throw new InvalidOperationException("Подписка изменилась. Повторите проверку.");
            SubscriptionManager.RecordFailure(records[0],error,attemptUtc,nowUtc);
        },true);
    }
    public void Apply(AppState target,Func<bool> canApply,Action<AppState> persist)
    {
        if(target==null||canApply==null||persist==null)throw new ArgumentNullException("subscription commit");
        void Check(){if(!canApply()||FullStateRestorePlan.Fingerprint(target)!=baseline)throw new InvalidOperationException("Настройки изменились. Повторите предварительный просмотр подписки.");}
        Check();var staged=FullTransfer.Copy(prepared);string expected=FullStateRestorePlan.Fingerprint(staged);persist(staged);
        if(FullStateRestorePlan.Fingerprint(staged)!=expected)throw new InvalidOperationException("Не удалось подтвердить сохранённое обновление подписки.");
        Check();var committed=FullTransfer.Copy(staged);
        target.Subscriptions=committed.Subscriptions;
        if(!metadataOnly){target.Profiles=committed.Profiles;target.SelectedProfileId=committed.SelectedProfileId;target.LastSubscriptionUpdateUtc=committed.LastSubscriptionUpdateUtc;}
    }
}

// Remove provider-retired metadata only after all captured owners have released it.
public sealed class SubscriptionRetirementPlan
{
    readonly string baseline;readonly AppState prepared;readonly string[] removed;
    SubscriptionRetirementPlan(string baseline,AppState prepared,string[] removed){this.baseline=baseline;this.prepared=prepared;this.removed=removed;}
    public int RemovedCount => removed.Length;
    public static SubscriptionRetirementPlan Prepare(AppState target,IEnumerable<string> ownedProfileIds)
    {
        if(target==null||ownedProfileIds==null)throw new ArgumentNullException("subscription retirement");
        var owned=new HashSet<string>(ownedProfileIds.Concat(MultiServerSettings.ProfileReferences(target)),StringComparer.Ordinal);
        var records=(target.Subscriptions??new List<SubscriptionRecord>()).Where(r=>r!=null&&!String.IsNullOrWhiteSpace(r.Id)).GroupBy(r=>r.Id,StringComparer.Ordinal).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.Ordinal);
        bool Eligible(VpnProfile p)=>p.RetiredFromSubscription&&!owned.Contains(p.Id)&&!String.IsNullOrWhiteSpace(p.SubscriptionUrl)&&p.SubscriptionId!=null&&
            records.TryGetValue(p.SubscriptionId,out var record)&&SubscriptionManager.SameUrl(record.Url,p.SubscriptionUrl);
        var unique=(target.Profiles??new List<VpnProfile>()).Where(p=>p!=null&&!String.IsNullOrWhiteSpace(p.Id)).GroupBy(p=>p.Id,StringComparer.Ordinal).Where(g=>g.Count()==1).Select(g=>g.Single());
        var ids=new HashSet<string>(unique.Where(Eligible).Select(p=>p.Id),StringComparer.Ordinal);
        if(ids.Count==0)return null;
        var candidate=FullTransfer.Copy(target);
        candidate.Profiles.RemoveAll(p=>p.RetiredFromSubscription&&ids.Contains(p.Id));
        bool Gone(string id)=>id!=null&&ids.Contains(id)&&!candidate.Profiles.Any(p=>p.Id==id);
        if(Gone(candidate.SelectedProfileId))candidate.SelectedProfileId=ProfileCollectionService.Candidates(candidate).FirstOrDefault()?.Id;
        if(Gone(candidate.FallbackRootProfileId))candidate.FallbackRootProfileId=candidate.SelectedProfileId;
        if(Gone(candidate.LastWorkingProfileId))candidate.LastWorkingProfileId=null;
        if(Gone(candidate.LastConnectedProfileId))candidate.LastConnectedProfileId=null;
        return new SubscriptionRetirementPlan(FullStateRestorePlan.Fingerprint(target),StateStore.SnapshotForSave(candidate),ids.ToArray());
    }
    public void Apply(AppState target,Func<IEnumerable<string>> ownedProfileIds,Func<bool> canApply,Action<AppState> persist)
    {
        if(target==null||ownedProfileIds==null||canApply==null||persist==null)throw new ArgumentNullException("subscription retirement commit");
        void Check(){var owned=new HashSet<string>(ownedProfileIds().Concat(MultiServerSettings.ProfileReferences(target)),StringComparer.Ordinal);if(!canApply()||FullStateRestorePlan.Fingerprint(target)!=baseline||removed.Any(owned.Contains))throw new InvalidOperationException("Профили или активные подключения изменились. Очистка отложена.");}
        Check();var staged=FullTransfer.Copy(prepared);string expected=FullStateRestorePlan.Fingerprint(staged);persist(staged);
        if(FullStateRestorePlan.Fingerprint(staged)!=expected)throw new InvalidOperationException("Сохранённая очистка профилей изменилась.");
        Check();var committed=FullTransfer.Copy(staged);
        target.Profiles=committed.Profiles;target.SelectedProfileId=committed.SelectedProfileId;target.FallbackRootProfileId=committed.FallbackRootProfileId;
        target.LastWorkingProfileId=committed.LastWorkingProfileId;target.LastConnectedProfileId=committed.LastConnectedProfileId;
    }
}

// A dialog owns an exact live profile; only alias fields publish after durable save.
public sealed class ProfileAliasCommitPlan
{
    readonly AppState target;readonly VpnProfile profile;readonly string baseline;
    ProfileAliasCommitPlan(AppState target,VpnProfile profile){this.target=target;this.profile=profile;baseline=FullStateRestorePlan.Fingerprint(target);}
    public static ProfileAliasCommitPlan Capture(AppState target,VpnProfile profile)
    {
        if(target==null||profile==null)throw new ArgumentNullException("profile alias");
        if(String.IsNullOrWhiteSpace(profile.Id)||target.Profiles.Count(p=>p!=null&&p.Id==profile.Id)!=1||!target.Profiles.Any(p=>ReferenceEquals(p,profile)))throw new InvalidOperationException("Профиль изменился. Откройте его заново.");
        return new ProfileAliasCommitPlan(target,profile);
    }
    public void Apply(string name,Func<bool> canApply,Action<AppState> persist)
    {
        if(canApply==null||persist==null)throw new ArgumentNullException("profile alias commit");
        void Check(){if(!canApply()||FullStateRestorePlan.Fingerprint(target)!=baseline||!target.Profiles.Any(p=>ReferenceEquals(p,profile))||target.Profiles.Count(p=>p!=null&&p.Id==profile.Id)!=1)throw new InvalidOperationException("Профиль или настройки изменились. Откройте переименование заново.");}
        Check();var staged=FullTransfer.Copy(target);var edited=staged.Profiles.Single(p=>p.Id==profile.Id);SubscriptionManager.Rename(edited,name);staged=StateStore.SnapshotForSave(staged);
        string expected=FullStateRestorePlan.Fingerprint(staged);persist(staged);
        if(FullStateRestorePlan.Fingerprint(staged)!=expected)throw new InvalidOperationException("Не удалось подтвердить сохранённое название профиля.");
        Check();edited=staged.Profiles.Single(p=>p.Id==profile.Id);
        profile.ProviderName=edited.ProviderName;profile.UserName=edited.UserName;profile.Name=edited.Name;
    }
}

// Local card actions preserve exact live owners and publish only after persistence.
public sealed class ProfileMutationPlan
{
    readonly AppState target;readonly VpnProfile profile;readonly string baseline;readonly bool remove,favorite;
    ProfileMutationPlan(AppState target,VpnProfile profile,bool remove,bool favorite){this.target=target;this.profile=profile;this.remove=remove;this.favorite=favorite;baseline=FullStateRestorePlan.Fingerprint(target);}
    static void Validate(AppState target,VpnProfile profile){if(target==null||profile==null)throw new ArgumentNullException("profile mutation");if(String.IsNullOrWhiteSpace(profile.Id)||target.Profiles.Count(p=>p!=null&&p.Id==profile.Id)!=1||!target.Profiles.Any(p=>ReferenceEquals(p,profile)))throw new InvalidOperationException("Профиль изменился. Откройте список заново.");}
    public static ProfileMutationPlan Favorite(AppState target,VpnProfile profile,bool value){Validate(target,profile);return new ProfileMutationPlan(target,profile,false,value);}
    public static ProfileMutationPlan Delete(AppState target,VpnProfile profile){Validate(target,profile);return new ProfileMutationPlan(target,profile,true,false);}
    public void Apply(Func<bool> canApply,Func<IEnumerable<string>> ownedProfileIds,Action<AppState> persist)
    {
        if(canApply==null||ownedProfileIds==null||persist==null)throw new ArgumentNullException("profile mutation commit");
        void Check(){if(remove&&MultiServerSettings.ProfileReferences(target).Contains(profile.Id,StringComparer.Ordinal))throw new InvalidOperationException("Профиль назначен приложениям или сайтам. Сначала измените назначения в маршрутизации.");if(!canApply()||FullStateRestorePlan.Fingerprint(target)!=baseline||!target.Profiles.Any(p=>ReferenceEquals(p,profile))||target.Profiles.Count(p=>p!=null&&p.Id==profile.Id)!=1)throw new InvalidOperationException("Профиль или настройки изменились. Повторите действие.");if(remove&&ownedProfileIds().Contains(profile.Id,StringComparer.Ordinal))throw new InvalidOperationException("Сначала остановите подключение, игровой режим или локальный прокси, использующий этот профиль.");}
        Check();var candidate=FullTransfer.Copy(target);var edited=candidate.Profiles.Single(p=>p.Id==profile.Id);
        if(remove){candidate.Profiles.Remove(edited);if(candidate.SelectedProfileId==profile.Id)candidate.SelectedProfileId=ProfileCollectionService.Candidates(candidate).FirstOrDefault()?.Id;if(candidate.FallbackRootProfileId==profile.Id)candidate.FallbackRootProfileId=candidate.SelectedProfileId;if(candidate.LastWorkingProfileId==profile.Id)candidate.LastWorkingProfileId=null;if(candidate.LastConnectedProfileId==profile.Id)candidate.LastConnectedProfileId=null;}
        else edited.IsFavorite=favorite;
        var staged=StateStore.SnapshotForSave(candidate);string expected=FullStateRestorePlan.Fingerprint(staged);persist(staged);if(FullStateRestorePlan.Fingerprint(staged)!=expected)throw new InvalidOperationException("Не удалось подтвердить сохранённое изменение профиля.");Check();
        if(remove){target.Profiles.Remove(profile);target.SelectedProfileId=staged.SelectedProfileId;target.FallbackRootProfileId=staged.FallbackRootProfileId;target.LastWorkingProfileId=staged.LastWorkingProfileId;target.LastConnectedProfileId=staged.LastConnectedProfileId;}
        else profile.IsFavorite=favorite;
    }
}

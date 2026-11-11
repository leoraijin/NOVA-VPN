using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace NovaVpn;

// Frozen replacement of persisted DTO members; runtime resources stay owned by
// their existing controllers. No OS task, tunnel, proxy or engine is started.
public sealed class FullStateRestorePlan
{
    static readonly FieldInfo[] Members=typeof(AppState).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(f=>f.IsDefined(typeof(DataMemberAttribute),false)).ToArray();
    readonly string baseline;
    readonly AppState prepared;
    FullStateRestorePlan(string baseline,AppState prepared){this.baseline=baseline;this.prepared=prepared;}
    public static string Fingerprint(AppState state)
    {
        if(state==null)throw new ArgumentNullException(nameof(state));
        using(var stream=new MemoryStream())using(var sha=SHA256.Create()){
            new DataContractJsonSerializer(typeof(AppState),new DataContractJsonSerializerSettings{DateTimeFormat=new DateTimeFormat("o",System.Globalization.CultureInfo.InvariantCulture){DateTimeStyles=System.Globalization.DateTimeStyles.RoundtripKind}}).WriteObject(stream,state);
            return Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));
        }
    }
    public static FullStateRestorePlan Prepare(AppState target,AppState source)
    {
        if(target==null||source==null)throw new ArgumentNullException("restore state");
        if(source.SchemaVersion>new AppState().SchemaVersion)throw new NotSupportedException("Резервная копия создана более новой версией NOVA. Обновите клиент перед восстановлением.");
        if(Members.Any(f=>f.IsInitOnly||f.IsLiteral)||typeof(AppState).GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Any(p=>p.IsDefined(typeof(DataMemberAttribute),false)))throw new NotSupportedException("Схема состояния требует обновлённого обработчика восстановления.");
        string baseline=Fingerprint(target);var candidate=FullTransfer.Copy(source);
        // These flags describe this machine's authenticated Scheduler setup.
        candidate.StartWithWindows=target.StartWithWindows;candidate.StartConnectedWithWindows=target.StartConnectedWithWindows;
        candidate.AutoConnect=false;candidate.ApplicationLimitsEnabled=false;candidate.PauseUntilUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        candidate.EngineSelection=candidate.EngineSelection==1?1:0;
        ZapretRoutingTransitionService.SetActive(candidate,false);candidate.Zapret.Mode="VPN";
        candidate.Routing?.MigratedPolicy?.Validate();candidate.RoutingMigrationBackup?.MigratedPolicy?.Validate();
        return new FullStateRestorePlan(baseline,StateStore.SnapshotForSave(candidate));
    }
    public static FullStateRestorePlan PrepareSettingsOnly(AppState target,AppState imported)
    {
        if(target==null||imported==null)throw new ArgumentNullException("restore settings");
        if(imported.SchemaVersion>new AppState().SchemaVersion)throw new NotSupportedException("Настройки созданы более новой версией NOVA. Обновите клиент перед импортом.");
        string baseline=Fingerprint(target);var candidate=FullTransfer.Copy(target);
        PortableConfigService.ApplySettingsOnly(candidate,imported);
        candidate.Routing?.MigratedPolicy?.Validate();
        return new FullStateRestorePlan(baseline,StateStore.SnapshotForSave(candidate));
    }
    public static FullStateRestorePlan PrepareIdentitiesOnly(AppState target,IdentityState imported)
    {
        if(target==null||imported==null)throw new ArgumentNullException("restore identities");
        imported.Validate();string baseline=Fingerprint(target);var candidate=FullTransfer.Copy(target);
        candidate.SubscriptionIdentities=imported;
        // Storage normalization works on a second deep copy, never the dialog result.
        var frozen=StateStore.SnapshotForSave(candidate);frozen.SubscriptionIdentities.Validate();
        return new FullStateRestorePlan(baseline,frozen);
    }
    public void Apply(AppState target,Func<bool> canRestore,Action<AppState> persist)
    {
        if(target==null||canRestore==null||persist==null)throw new ArgumentNullException("restore commit");
        void Check(){if(!canRestore()||target.Routing?.Gaming!=null||ZapretRoutingTransitionService.HasActiveRouting(target))throw new InvalidOperationException("Завершите подключения и активные операции перед восстановлением.");if(Fingerprint(target)!=baseline)throw new InvalidOperationException("Настройки изменились после подготовки копии. Повторите восстановление.");}
        Check();var staged=FullTransfer.Copy(prepared);string expected=Fingerprint(staged);persist(staged);
        if(Fingerprint(staged)!=expected)throw new InvalidOperationException("Не удалось подтвердить сохранённую копию. Перечитайте настройки перед продолжением.");Check();
        // Do not give the live model aliases retained by the persistence callback.
        var committed=FullTransfer.Copy(staged);foreach(var member in Members)member.SetValue(target,member.GetValue(committed));
    }
}
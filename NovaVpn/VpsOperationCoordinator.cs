using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Store lease and serialized operation stay held across the exchange.
public sealed class VpsOperationCoordinator
{
    readonly VpsOperationStore store;
    readonly SemaphoreSlim operations=new SemaphoreSlim(1,1);
    readonly Func<VpsSshTarget,VpsAgentFrame,CancellationToken,Task<VpsAgentResponse>> exchange;
    public VpsOperationCoordinator(VpsOperationStore store,VpsSshAgentRunner runner,string identityFile,string runtime)
        :this(store,(target,frame,token)=>runner.ExecuteAsync(target,frame,identityFile,runtime,token)) {if(runner==null)throw new ArgumentNullException(nameof(runner));}
    internal VpsOperationCoordinator(VpsOperationStore store,Func<VpsSshTarget,VpsAgentFrame,CancellationToken,Task<VpsAgentResponse>> exchange)
    {this.store=store??throw new ArgumentNullException(nameof(store));this.exchange=exchange??throw new ArgumentNullException(nameof(exchange));}
    public Task<VpsAgentResponse> DeployAsync(VpsSshTarget target,VpsDeploymentDraft draft,CancellationToken cancellation)
        =>DeployAsync(target,draft,()=>true,cancellation);
    public async Task<VpsAgentResponse> DeployAsync(VpsSshTarget target,VpsDeploymentDraft draft,Func<bool> canChange,CancellationToken cancellation)
    {
        if(canChange==null)throw new ArgumentNullException(nameof(canChange));
        await operations.WaitAsync(cancellation);
        try{
            cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Подключение изменилось. Пакет не отправлен.");
            var frame=await Task.Run(()=>VpsAgentFrame.Deployment(target,draft),cancellation);
            cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Подключение изменилось. Пакет не отправлен.");
            store.Prepare(target,draft,frame);cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Подключение изменилось. Подготовленный пакет не отправлен.");
            return await DispatchPreparedAsync(target,frame,cancellation);
        }finally{operations.Release();}
    }
    public async Task<VpsAgentResponse> ReinstallAsync(VpsSshTarget target,VpsDeploymentDraft draft,Func<bool> canChange,CancellationToken cancellation)
    {
        if(canChange==null)throw new ArgumentNullException(nameof(canChange));
        await operations.WaitAsync(cancellation);
        try{
            cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Перед повторной установкой отключите использующие VPS подключения.");
            var frame=await Task.Run(()=>VpsAgentFrame.Reinstallation(target,draft),cancellation);
            cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Подключение изменилось. Пакет не отправлен.");
            var status=(await exchange(target,VpsAgentFrame.Status(target,draft.InstallationId),cancellation))?.ServerStatus;
            cancellation.ThrowIfCancellationRequested();
            if(status==null||!canChange())throw new InvalidOperationException("Состояние VPS или подключение изменилось. Пакет не отправлен.");
            store.PrepareReinstallation(target,draft,frame,status);cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Подключение изменилось. Подготовленный пакет не отправлен.");
            return await DispatchPreparedAsync(target,frame,cancellation);
        }finally{operations.Release();}
    }
    async Task<VpsAgentResponse> DispatchPreparedAsync(VpsSshTarget target,VpsAgentFrame frame,CancellationToken cancellation)
    {
        store.MarkDispatched(frame.OperationId);
        try{
            var response=await exchange(target,frame,cancellation);
            if(response!=null&&response.IsTerminalReceipt)store.AcceptReceipt(response);
            else store.MarkUncertain(frame.OperationId);
            return response;
        }
        catch{
            // Never erase pending credentials/intent even if recording uncertainty fails.
            try{var pending=store.Read();if(pending!=null&&!pending.IsTerminal)store.MarkUncertain(frame.OperationId);}catch{}
            throw;
        }
    }
    public async Task<VpsAgentResponse> RollbackAsync(CancellationToken cancellation)
    {
        await operations.WaitAsync(cancellation);
        try{
            cancellation.ThrowIfCancellationRequested();
            var known=store.Read()??throw new InvalidOperationException("Запись VPS отсутствует.");
            if(!known.IsTerminal)throw new InvalidOperationException("Сначала восстановите незавершённую операцию VPS.");
            var status=(await exchange(known.Target,VpsAgentFrame.Status(known.Target,known.InstallationId),cancellation))?.ServerStatus;
            cancellation.ThrowIfCancellationRequested();
            if(status==null)throw new InvalidOperationException("Состояние VPS не подтверждено.");
            var frame=VpsAgentFrame.Rollback(known.Target,known.InstallationId,Guid.NewGuid().ToString("N"),status.Revision,status.ServiceActive,status.ServiceEnabled);
            store.PrepareRollback(status,frame);cancellation.ThrowIfCancellationRequested();
            return await DispatchPreparedAsync(known.Target,frame,cancellation);
        }finally{operations.Release();}
    }
    public async Task<VpsAgentResponse> RemoveAsync(Func<bool> canRemove,CancellationToken cancellation)
    {
        if(canRemove==null)throw new ArgumentNullException(nameof(canRemove));
        await operations.WaitAsync(cancellation);
        try{
            cancellation.ThrowIfCancellationRequested();
            if(!canRemove())throw new InvalidOperationException("Перед удалением отключите использующие VPS подключения.");
            var known=store.Read()??throw new InvalidOperationException("Запись VPS отсутствует.");
            if(!known.IsTerminal||known.Phase=="removed")throw new InvalidOperationException("Установка уже удалена или требуется восстановление операции.");
            var status=(await exchange(known.Target,VpsAgentFrame.Status(known.Target,known.InstallationId),cancellation))?.ServerStatus;
            cancellation.ThrowIfCancellationRequested();
            if(status==null||!canRemove())throw new InvalidOperationException("Состояние VPS или подключение изменилось. Удаление не отправлено.");
            var frame=VpsAgentFrame.Removal(known.Target,known.InstallationId,Guid.NewGuid().ToString("N"),status.Revision);
            store.PrepareRemoval(status,frame);cancellation.ThrowIfCancellationRequested();
            if(!canRemove())throw new InvalidOperationException("Подключение изменилось. Подготовленное удаление не отправлено.");
            return await DispatchPreparedAsync(known.Target,frame,cancellation);
        }finally{operations.Release();}
    }
    public async Task<VpsOperationRecord> CancelPreparedAsync(CancellationToken cancellation)
    {
        await operations.WaitAsync(cancellation);
        try{
            cancellation.ThrowIfCancellationRequested();
            var known=store.Read()??throw new InvalidOperationException("Запись VPS отсутствует.");
            return store.CancelPrepared(known.OperationId);
        }finally{operations.Release();}
    }
    public async Task<VpsAgentResponse> InspectAsync(CancellationToken cancellation)
    {
        await operations.WaitAsync(cancellation);
        try{
            var known=store.Read();if(known==null)throw new InvalidOperationException("Запись VPS отсутствует.");
            return await exchange(known.Target,VpsAgentFrame.Status(known.Target,known.InstallationId),cancellation);
        }finally{operations.Release();}
    }
    // Detached review result only; callers must revalidate before persisting or activating.
    public Task<VpnProfile> VerifyProfileAsync(Func<VpnProfile> currentProfile,VpsOperationRecord replaced,string corePath,string runtime,CancellationToken cancellation)
        =>VerifyProfileCoreAsync(currentProfile,replaced,corePath,runtime,cancellation,ProbeOffThreadAsync);
    // Runs verification and persistence in one coordinator operation. Never connects
    // a tunnel or changes the selected/fallback/last-working profile IDs.
    public Task<VpnProfile> VerifyAndSaveProfileAsync(AppState state,VpsOperationRecord replaced,string corePath,string runtime,Func<bool> canChange,Action<AppState> persist,CancellationToken cancellation)
        =>VerifyAndSaveProfileCoreAsync(state,replaced,corePath,runtime,canChange,persist,cancellation,ProbeOffThreadAsync);
    static Task<ServerMapHealth> ProbeOffThreadAsync(VpnProfile profile,string corePath,string runtime,CancellationToken cancellation)
        =>Task.Run(()=>ServerMapHealthService.TestAsync(profile,corePath,runtime,cancellation),cancellation);
    internal async Task<VpnProfile> VerifyAndSaveProfileCoreAsync(AppState state,VpsOperationRecord replaced,string corePath,string runtime,Func<bool> canChange,Action<AppState> persist,CancellationToken cancellation,Func<VpnProfile,string,string,CancellationToken,Task<ServerMapHealth>> probe)
    {
        if(state==null||canChange==null||persist==null)throw new ArgumentNullException("VPS profile persistence");
        if(!canChange())throw new InvalidOperationException("Профиль используется. Сохранение VPS отложено.");
        string profileId=(store.Read()??throw new InvalidOperationException("Запись VPS отсутствует.")).ProfileId;
        Func<VpnProfile> selected=()=>{
            if(state.Profiles==null||state.Profiles.Any(p=>p==null)||state.Profiles.Count(p=>p.Id==profileId)>1)throw new InvalidOperationException("Список профилей содержит конфликт идентификаторов.");
            return state.Profiles.SingleOrDefault(p=>p.Id==profileId);
        };
        VpnProfile saved=null;
        await VerifyProfileCoreAsync(selected,replaced,corePath,runtime,cancellation,probe,profile=>{
            cancellation.ThrowIfCancellationRequested();
            if(!canChange())throw new InvalidOperationException("Подключение изменилось во время проверки. Профиль не применён.");
            var previous=selected();
            string expectedLocal=previous==null?null:ProfileSnapshot(previous);
            // Stage a complete detached state. A failed write cannot expose the
            // new key in the live list, nor replace unrelated profile objects.
            AppState staged;
            using(var stream=new MemoryStream()){
                var serializer=new DataContractSerializer(typeof(AppState));serializer.WriteObject(stream,state);stream.Position=0;
                staged=(AppState)serializer.ReadObject(stream);
            }
            var replacement=CopyProfile(profile);
            int index=previous==null?-1:state.Profiles.IndexOf(previous);
            if(index<0)staged.Profiles.Add(CopyProfile(replacement));else staged.Profiles[index]=CopyProfile(replacement);
            cancellation.ThrowIfCancellationRequested();
            if(!canChange()||!ReferenceEquals(previous,selected())||expectedLocal!=(previous==null?null:ProfileSnapshot(previous))||!ProfileHealthEvidence.IsCurrent(replacement,DateTime.UtcNow,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(corePath)))throw new InvalidOperationException("Настройки, подключение или результат проверки изменились перед сохранением.");
            cancellation.ThrowIfCancellationRequested();
            persist(staged);
            // No await/user interaction between final validation, write and swap.
            if(index<0)state.Profiles.Add(replacement);else state.Profiles[index]=replacement;
            saved=replacement;
        });
        return saved;
    }
    internal async Task<VpnProfile> VerifyProfileCoreAsync(Func<VpnProfile> currentProfile,VpsOperationRecord replaced,string corePath,string runtime,CancellationToken cancellation,Func<VpnProfile,string,string,CancellationToken,Task<ServerMapHealth>> probe,Action<VpnProfile> verified=null)
    {
        if(currentProfile==null||probe==null)throw new ArgumentNullException("VPS verification");
        await operations.WaitAsync(cancellation);
        try{
            cancellation.ThrowIfCancellationRequested();
            var known=store.Read()??throw new InvalidOperationException("Запись VPS отсутствует.");
            var frame=VpsAgentFrame.Status(known.Target,known.InstallationId);
            var before=(await exchange(known.Target,frame,cancellation))?.ServerStatus;
            var candidate=store.FindCommittedCandidate(before)??throw new InvalidOperationException("Текущая конфигурация VPS не подтверждена.");
            if(!before.ServiceActive)throw new InvalidOperationException("Сервис VPS не запущен.");
            var initial=currentProfile();
            var prepared=VpsProfilePreparation.Prepare(candidate,before,initial,replaced);
            string baseline=ProfileSnapshot(prepared);
            var health=await probe(prepared,corePath,runtime,cancellation);
            cancellation.ThrowIfCancellationRequested();
            if(health==null||!health.Success||!ProfileHealthEvidence.IsCurrent(prepared,DateTime.UtcNow,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(corePath)))throw new InvalidOperationException("HTTPS через профиль VPS не подтверждён. Профиль не применён.");
            var after=(await exchange(known.Target,frame,cancellation))?.ServerStatus;
            cancellation.ThrowIfCancellationRequested();
            var current=store.FindCommittedCandidate(after);
            if(current==null||current.OperationId!=candidate.OperationId||after.Revision!=before.Revision||after.OperationId!=before.OperationId||after.RequestSha256!=before.RequestSha256||!after.ServiceActive||after.ServiceEnabled!=before.ServiceEnabled)throw new InvalidOperationException("Состояние VPS изменилось во время проверки. Повторите проверку.");
            var local=currentProfile();
            if((initial==null)!=(local==null))throw new InvalidOperationException("Список профилей изменился во время проверки.");
            var refreshed=VpsProfilePreparation.Prepare(current,after,local??prepared,local==null?null:replaced);
            if(baseline!=ProfileSnapshot(refreshed)||baseline!=ProfileSnapshot(VpsProfilePreparation.Prepare(candidate,before,prepared)))throw new InvalidOperationException("Локальные настройки профиля изменились во время проверки. Повторите проверку.");
            if(!ProfileHealthEvidence.IsCurrent(prepared,DateTime.UtcNow,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(corePath)))throw new InvalidOperationException("Сеть, ядро или результат проверки изменились.");
            verified?.Invoke(prepared);
            return prepared;
        }finally{operations.Release();}
    }
    static VpnProfile CopyProfile(VpnProfile profile)
    {
        using(var stream=new MemoryStream()){
            var serializer=new DataContractSerializer(typeof(VpnProfile));serializer.WriteObject(stream,profile);stream.Position=0;
            return (VpnProfile)serializer.ReadObject(stream);
        }
    }
    static string ProfileSnapshot(VpnProfile profile)
    {
        using(var stream=new MemoryStream()){
            new DataContractSerializer(typeof(VpnProfile)).WriteObject(stream,profile);
            return Convert.ToBase64String(stream.ToArray());
        }
    }
    public async Task<VpsAgentResponse> ReconcileAsync(CancellationToken cancellation)
    {await operations.WaitAsync(cancellation);try{return await ReconcileCoreAsync(cancellation);}finally{operations.Release();}}
    async Task<VpsAgentResponse> ReconcileCoreAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var pending=store.Read();
        if(pending==null)throw new InvalidOperationException("Незавершённая запись VPS отсутствует.");
        if((pending.EffectiveAction=="remove"||pending.EffectiveAction=="reinstall")&&(pending.Phase=="prepared"||pending.Phase=="not-sent"))throw new InvalidOperationException("Операция не отправлялась. Отмените подготовку локально.");
        var frame=pending.EffectiveAction=="reinstall"?VpsAgentFrame.ReinstallationRecovery(pending.Target,pending.InstallationId,pending.OperationId,pending.ExpectedRevision,pending.RequestSha256):pending.EffectiveAction=="remove"?VpsAgentFrame.RemovalRecovery(pending.Target,pending.InstallationId,pending.OperationId,pending.ExpectedRevision,pending.RequestSha256):VpsAgentFrame.Recovery(pending.Target,pending.InstallationId,pending.OperationId,pending.ExpectedRevision,pending.RequestSha256);
        var response=await exchange(pending.Target,frame,cancellation);
        if(response!=null&&response.IsTerminalReceipt)store.AcceptReceipt(response);
        // idle/absent or an old unrelated receipt cannot discard a pending key.
        return response;
    }
}

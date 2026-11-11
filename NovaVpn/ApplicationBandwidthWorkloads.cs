using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

// One registry across every captured entry, including retired app identities.
// Live policy commits retain budgets; ordinary uncaptured entries cannot be
// promoted to enforcing entries by changing settings alone.
public sealed class ApplicationBandwidthWorkloads : IDisposable
{
    readonly LocalProxyManager manager;
    readonly SemaphoreSlim mutation=new SemaphoreSlim(1,1);
    readonly CancellationTokenSource stop=new CancellationTokenSource();
    readonly Dictionary<string,CancellationTokenSource> links=new Dictionary<string,CancellationTokenSource>(StringComparer.Ordinal);
    ApplicationBandwidthPolicySnapshot policy;
    ApplicationBandwidthController controller;
    bool disposed;
    public ApplicationBandwidthWorkloads(LocalProxyManager manager){this.manager=manager??throw new ArgumentNullException(nameof(manager));}
    static bool Same(ApplicationBandwidthPolicySnapshot a,ApplicationBandwidthPolicySnapshot b)
    {
        if(a==null||b==null||a.Enabled!=b.Enabled||a.Policies.Count!=b.Policies.Count)return false;
        var left=a.Policies.OrderBy(p=>p.Identity.Key,StringComparer.OrdinalIgnoreCase).ToArray();var right=b.Policies.OrderBy(p=>p.Identity.Key,StringComparer.OrdinalIgnoreCase).ToArray();
        for(int i=0;i<left.Length;i++)if(!String.Equals(left[i].Identity.Key,right[i].Identity.Key,StringComparison.OrdinalIgnoreCase)||left[i].UploadBytesPerSecond!=right[i].UploadBytesPerSecond||left[i].DownloadBytesPerSecond!=right[i].DownloadBytesPerSecond||left[i].BurstBytes!=right[i].BurstBytes)return false;
        return true;
    }
    void Collect(){foreach(string id in links.Keys.Where(id=>!manager.Snapshot(id).Exists).ToArray()){links[id].Dispose();links.Remove(id);}}
    public async Task StartAsync(AppState state,LocalProxyEndpoint endpoint,VpnProfile profile,string corePath,CancellationToken token)
    {
        var next=ApplicationBandwidthPolicySnapshot.Freeze(state);
        var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};endpoint=json.Deserialize<LocalProxyEndpoint>(json.Serialize(endpoint));profile=json.Deserialize<VpnProfile>(json.Serialize(profile));corePath=Path.GetFullPath(corePath);
        var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);bool retained=false;
        try{
            await mutation.WaitAsync(linked.Token).ConfigureAwait(false);
            try{
                if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthWorkloads));
                linked.Token.ThrowIfCancellationRequested();Collect();
                if(!Same(next,ApplicationBandwidthPolicySnapshot.Freeze(state)))throw new InvalidOperationException("Ограничения изменились во время ожидания запуска. Повторите запуск.");
                if(next.Enabled){using(var user=WindowsIdentity.GetCurrent())if(!next.Policies.Any(p=>p.Identity.UserSid==user.User.Value))throw new InvalidOperationException("Правила привязаны к другому пользователю Windows. Перепривяжите нужные приложения в редакторе лимитов.");}
                if(!Same(policy,next)){
                    if(manager.HasEntries)throw new InvalidOperationException("Остановите все локальные прокси перед применением изменённых ограничений приложений.");
                    var replacement=next.Enabled?next.CreateController():null;
                    var previous=controller;controller=replacement;policy=next;previous?.Dispose();
                }
                if(endpoint!=null&&manager.Snapshot(endpoint.Id).Exists)throw new InvalidOperationException("Этот вход уже запущен или ожидает подтверждения остановки.");
                try{
                    if(next.Enabled)await manager.StartShapedSocksAsync(endpoint,profile,corePath,controller,(who,host,port)=>true,linked.Token).ConfigureAwait(false);
                    else await manager.StartAsync(endpoint,profile,corePath,linked.Token).ConfigureAwait(false);
                }finally{
                    // Keep cancellation linked for the entire owned run, including
                    // an uncertain failed-start cleanup, not just startup awaits.
                    if(endpoint!=null&&manager.Snapshot(endpoint.Id).Exists){links.Add(endpoint.Id,linked);retained=true;}
                }
            }finally{mutation.Release();}
        }finally{if(!retained)linked.Dispose();}
    }
    public void SavePolicy(AppState state,string baseline,ApplicationBandwidthPolicyEdit edit,Action<AppState> persist,CancellationToken token)
    {
        using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token)){
            linked.Token.ThrowIfCancellationRequested();
            if(!mutation.Wait(0))throw new InvalidOperationException("Завершается операция локального прокси. Повторите сохранение после её завершения.");
            try{SavePolicyCore(state,baseline,edit,persist,linked.Token);}finally{mutation.Release();}
        }
    }
    public async Task SavePolicyAsync(AppState state,string baseline,ApplicationBandwidthPolicyEdit edit,Action<AppState> persist,CancellationToken token)
    {
        using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token)){
            await mutation.WaitAsync(linked.Token).ConfigureAwait(false);
            try{SavePolicyCore(state,baseline,edit,persist,linked.Token);}finally{mutation.Release();}
        }
    }
    void SavePolicyCore(AppState state,string baseline,ApplicationBandwidthPolicyEdit edit,Action<AppState> persist,CancellationToken token)
    {
        if(state==null||edit==null||persist==null)throw new ArgumentNullException("policy save");
        if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthWorkloads));
        token.ThrowIfCancellationRequested();Collect();
        bool active=manager.HasEntries;
        if(baseline!=ApplicationBandwidthPolicyEdit.Fingerprint(state))throw new InvalidOperationException("Правила изменились, пока редактор был открыт. Откройте его заново.");
        var staged=FullTransfer.Copy(state);edit.WriteTo(staged);string expected=ApplicationBandwidthPolicyEdit.Fingerprint(staged);
        var next=ApplicationBandwidthPolicySnapshot.Freeze(staged);
        bool updateCapture=active&&(next.Enabled||policy?.Enabled==true);
        if(updateCapture&&(controller==null||policy==null||!Same(policy,ApplicationBandwidthPolicySnapshot.Freeze(state))||links.Count!=manager.EntryCount||links.Keys.Any(id=>!manager.Snapshot(id).ApplicationLimits)))throw new InvalidOperationException("Для включения лимитов сначала остановите входы, запущенные без них или ещё не завершившие остановку.");
        if(updateCapture&&next.Enabled){using(var user=WindowsIdentity.GetCurrent())if(!next.Policies.Any(p=>p.Identity.UserSid==user.User.Value))throw new InvalidOperationException("Нет правил для текущего пользователя Windows. Перепривяжите приложение или выключите лимиты.");}
        bool durableReturned=false;
        void CommitSaved(){
            token.ThrowIfCancellationRequested();persist(staged);durableReturned=true;
            // A successful durable return commits even if cancellation arrives late.
            if(expected!=ApplicationBandwidthPolicyEdit.Fingerprint(staged)||baseline!=ApplicationBandwidthPolicyEdit.Fingerprint(state)||manager.HasEntries!=active)throw new InvalidOperationException("Не удалось подтвердить сохранённые правила. Перечитайте настройки перед повторным изменением.");
            edit.WriteTo(state);
        }
        try{
            if(updateCapture){controller.CommitPolicy(policy,next,CommitSaved);policy=next;}
            else CommitSaved();
        }catch{if(active&&durableReturned)foreach(var link in links.Values)link.Cancel();throw;}
    }

    public async Task<Dictionary<string,bool>> ReconcileAsync(AppState state)
    {
        await mutation.WaitAsync().ConfigureAwait(false);
        try{
            var result=new Dictionary<string,bool>(StringComparer.Ordinal);
            if(disposed)return result;Collect();
            bool unchanged=false;
            try{unchanged=Same(policy,ApplicationBandwidthPolicySnapshot.Freeze(state));}
            catch(Exception error)when(error is ArgumentException||error is InvalidDataException||error is NotSupportedException){}
            if(unchanged)return result;
            foreach(string id in links.Keys.ToArray()){
                try{await manager.StopAsync(id).ConfigureAwait(false);result[id]=!manager.Snapshot(id).Exists;}catch(Exception){result[id]=false;}
            }
            Collect();return result;
        }finally{mutation.Release();}
    }
    // Caller owns manager shutdown; disposing cancels starts/budgets rather than
    // promote a failed shaped start to an unlimited one.
    public void Dispose(){stop.Cancel();mutation.Wait();try{if(disposed)return;disposed=true;controller?.Dispose();controller=null;foreach(var link in links.Values)link.Dispose();links.Clear();}finally{mutation.Release();}}
}

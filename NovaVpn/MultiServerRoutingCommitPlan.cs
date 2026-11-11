using System;using System.IO;
namespace NovaVpn;

// Persist a private routing candidate before publishing it to the current window state.
public sealed class MultiServerRoutingCommitPlan
{
 readonly AppState target;readonly string baseline;readonly AppState prepared;readonly AppState previous;
 readonly bool publishesZapretRouting;
 MultiServerRoutingCommitPlan(AppState target,RoutingSettings routing,string connectedId=null,bool restore=false,bool forget=false){this.target=target;baseline=FullStateRestorePlan.Fingerprint(target);previous=FullTransfer.Copy(target);prepared=FullTransfer.Copy(previous);prepared.Routing=RoutingOverlayService.Clone(routing);prepared.Routing.MultiServer?.Validate();
  publishesZapretRouting = restore || !forget && prepared.Routing.MultiServer != null;
  if(restore) {
   var history=MultiServerRoutingHistory.PreviewDisable(target);
   var restored=ZapretRoutingTransitionService.PrepareOrdinaryRouting(target,history.SingleServerRouting);
   prepared.Routing=restored.Routing;prepared.Zapret=restored.Zapret;
   prepared.SelectedProfileId=history.SelectedProfileId;prepared.FallbackRootProfileId=history.FallbackRootProfileId;prepared.LastConnectedProfileId=history.SelectedProfileId;prepared.MultiServerRoutingHistory=history;
  } else if(forget) {
   if(target.Routing?.MultiServer!=null)throw new InvalidOperationException("Сначала отключите назначения.");prepared.MultiServerRoutingHistory=null;
  } else if(prepared.Routing.MultiServer!=null) {
   var assigned=ZapretRoutingTransitionService.PrepareAssignedRouting(target,prepared.Routing);
   prepared.Routing=assigned.Routing;prepared.Zapret=assigned.Zapret;
   MultiServerConfigBuilder.Build(prepared.Routing,prepared.Profiles,Path.Combine(StateStore.DirectoryPath,"multi-server-dns.db"));
   prepared.MultiServerRoutingHistory=MultiServerRoutingHistory.CaptureForAssignments(target,prepared.Routing);
   prepared.SelectedProfileId=prepared.Routing.MultiServer.DefaultProfileId;prepared.FallbackRootProfileId=prepared.SelectedProfileId;
  }
  if(connectedId!=null){if(connectedId!=prepared.SelectedProfileId)throw new InvalidOperationException("Активный сервер не соответствует назначению.");prepared.LastConnectedProfileId=connectedId;}
  prepared=StateStore.SnapshotForSave(prepared);
 }
 public static MultiServerRoutingCommitPlan Prepare(AppState target,RoutingSettings routing){if(target==null||routing==null)throw new ArgumentNullException();return new MultiServerRoutingCommitPlan(target,routing);}
 public static MultiServerRoutingCommitPlan PrepareConnected(AppState target,RoutingSettings routing,string connectedId){if(target==null||routing==null||string.IsNullOrEmpty(connectedId))throw new ArgumentNullException();return new MultiServerRoutingCommitPlan(target,routing,connectedId);}
 public static MultiServerRoutingCommitPlan PrepareDisable(AppState target,string connectedId=null){if(target==null)throw new ArgumentNullException();return new MultiServerRoutingCommitPlan(target,target.Routing,connectedId,true);}
 public static MultiServerRoutingCommitPlan PrepareForgetHistory(AppState target){if(target==null)throw new ArgumentNullException();return new MultiServerRoutingCommitPlan(target,target.Routing,null,false,true);}
 public void Apply(Func<bool> canApply,Action<AppState> persist){
  if(canApply==null||persist==null)throw new ArgumentNullException();
  void Check(){if(!canApply()||FullStateRestorePlan.Fingerprint(target)!=baseline)throw new InvalidOperationException("Настройки изменились. Откройте назначения заново.");}
  Check();var staged=FullTransfer.Copy(prepared);string expected=FullStateRestorePlan.Fingerprint(staged);
  try {
   persist(staged);
   if(FullStateRestorePlan.Fingerprint(staged)!=expected)throw new InvalidOperationException("Не удалось подтвердить сохранение назначений.");
   Check();
  } catch(Exception error) {
   // A persistence callback may fail after writing. Restore only while this exact baseline is still owned.
   if(canApply()&&FullStateRestorePlan.Fingerprint(target)==baseline)try {
    var rollback=FullTransfer.Copy(previous);string previousHash=FullStateRestorePlan.Fingerprint(rollback);persist(rollback);
    if(FullStateRestorePlan.Fingerprint(rollback)!=previousHash)throw new IOException("Не удалось подтвердить восстановление настроек.");
   } catch(Exception rollbackError) {throw new AggregateException("Не удалось сохранить изменение и подтвердить восстановление прежних настроек.",error,rollbackError);}
   throw;
  }
  if(publishesZapretRouting)target.Zapret=staged.Zapret?.Clone();
  target.SchemaVersion=staged.SchemaVersion;target.Routing=RoutingOverlayService.Clone(staged.Routing);target.SelectedProfileId=staged.SelectedProfileId;target.FallbackRootProfileId=staged.FallbackRootProfileId;target.LastConnectedProfileId=staged.LastConnectedProfileId;target.MultiServerRoutingHistory=staged.MultiServerRoutingHistory?.Copy();
 }
}
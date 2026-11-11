using System;
using System.IO;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private async System.Threading.Tasks.Task OpenVpsAsync()
    {
        if(collectionEditorBusy||subscriptionReviewBusy||configuredFallbackBusy||profileSwitchInProgress||startupConnecting){Toast("Дождитесь завершения текущей операции.",Warning);return;}
        collectionEditorBusy=true;
        try{
            Func<bool> canChange=()=>!snapshotMode&&!closing&&core.Status==CoreStatus.Disconnected&&!connectionRequested&&!configuredFallbackBusy&&!profileSwitchInProgress&&!startupConnecting&&gamingSession==null&&gamingCancellation==null&&state.Zapret?.RoutingChangesApplied!=true&&!state.Profiles.Exists(p=>WorkloadProfileInUse(p.Id));
            var window=new VpsManagementWindow(state,core.ProbeCorePath,Path.Combine(StateStore.DirectoryPath,"vps-managed"),Path.Combine(StateStore.DirectoryPath,"runtime","vps"),state.Visual,snapshotMode,mapHealthCancellation.Token,canChange,StateStore.Save){Owner=this};
            window.ShowDialog();if(window.ProfilesChanged){InvalidateGamingResults();await ReconcileWorkloadsAsync();Navigate("servers");}
        }catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Danger);}
        finally{collectionEditorBusy=false;}
    }
}

using System;
namespace NovaVpn;

public static class SubscriptionQuotaNotifier
{
    // A true sender result acknowledges API submission, not human receipt.
    public static int CheckAndSave(AppState state,DateTime now,Func<string,bool> send,Action<AppState> persist,Func<bool> canApply)
    {
        if(state==null||send==null||persist==null||canApply==null)throw new ArgumentNullException("quota notification");
        if(!canApply())return 0;
        int changed=0,submitted=0;
        var commit=SubscriptionCommitPlan.PrepareBatch(state,candidate=>{
            foreach(var record in candidate.Subscriptions){
                string key;string message=SubscriptionPresentation.Alert(record,now,out key);
                if(String.IsNullOrWhiteSpace(message)){if(!String.IsNullOrEmpty(record.LastNotifiedQuotaState)){record.LastNotifiedQuotaState="";changed++;}continue;}
                if(record.LastNotifiedQuotaState==key||!candidate.ShowNotifications)continue;
                if(!canApply())throw new InvalidOperationException("Состояние клиента изменилось во время напоминания.");
                bool accepted;try{accepted=send(record.DisplayName+": "+message);}catch(Exception){accepted=false;}
                if(accepted){record.LastNotifiedQuotaState=key;changed++;submitted++;}
            }
        },true);
        if(changed!=0)commit.Apply(state,canApply,persist);
        return submitted;
    }
}

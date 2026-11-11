using System;
using System.Threading.Tasks;

namespace NovaVpn;

// Coalesce requests during a pending pass, but always drain the latest policy.
// The first caller's synchronization context is retained for UI-owned state reads.
public sealed class WorkloadReconciliationQueue
{
    readonly object sync = new object();
    TaskCompletionSource<bool> running;
    Func<Task> pending;
    public Task RequestAsync(Func<Task> reconcile)
    {
        if(reconcile==null)throw new ArgumentNullException(nameof(reconcile));
        TaskCompletionSource<bool> operation;
        lock(sync) {
            pending=reconcile;
            if(running!=null)return running.Task;
            operation=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            running=operation;
        }
        _=DrainAsync(operation);
        return operation.Task;
    }
    async Task DrainAsync(TaskCompletionSource<bool> operation)
    {
        try {
            while(true) {
                Func<Task> action;
                lock(sync) {
                    action=pending;pending=null;
                    if(action==null) {
                        if(ReferenceEquals(running,operation))running=null;
                    }
                }
                if(action==null){operation.TrySetResult(true);return;}
                await action();
            }
        }
        catch(Exception error) {
            lock(sync) {if(ReferenceEquals(running,operation)){running=null;pending=null;}}
            operation.TrySetException(error);
        }
    }
}

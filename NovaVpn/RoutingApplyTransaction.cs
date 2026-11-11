using System;using System.Threading.Tasks;
namespace NovaVpn;

public sealed class RoutingApplyResult
{
 public bool Applied {get;internal set;}
 public bool Cancelled {get;internal set;}
 public bool PreviousRestored {get;internal set;}
 public Exception Error {get;internal set;}
 public Exception RestoreError {get;internal set;}
 public Exception PersistenceRollbackError {get;internal set;}
}

// The caller owns runtime identity and cancellation; this coordinator never restarts
// after ownership is lost, nor publishes settings before successful activation.
public static class RoutingApplyTransaction
{
 public static Task<RoutingApplyResult> ApplyAsync(AppState state, Func<bool> isCurrent, Func<Task> validate,
     Func<Task> activate, Action commit, Action<AppState> persist, Func<Task> restore)
 {
  if (state == null || commit == null || persist == null) throw new ArgumentNullException();
  AppState beforeCommit = null;
  return ApplyAsync(isCurrent, validate, activate, () =>
  {
   // Validation and native activation may await while other client settings change.
   beforeCommit = FullTransfer.Copy(state);
   commit();
  }, () =>
  {
   if (beforeCommit != null) persist(FullTransfer.Copy(beforeCommit));
  }, restore);
 }

 public static async Task<RoutingApplyResult> ApplyAsync(Func<bool> isCurrent,Func<Task> validate,
     Func<Task> activate,Action commit,Action rollbackPersistence,Func<Task> restore)
 {
  if(isCurrent==null||validate==null||activate==null||commit==null||rollbackPersistence==null||restore==null)throw new ArgumentNullException();
  bool attempted=false,committing=false;
  try {
   if(!isCurrent())return new RoutingApplyResult{Cancelled=true};
   await validate();
   if(!isCurrent())return new RoutingApplyResult{Cancelled=true};
   attempted=true;await activate();
   if(!isCurrent())return new RoutingApplyResult{Cancelled=true};
   committing=true;commit();
   return new RoutingApplyResult{Applied=true};
  } catch(Exception error) {
   var result=new RoutingApplyResult{Error=error};
   if(!isCurrent()){result.Cancelled=true;return result;}
   if(committing)try{rollbackPersistence();}catch(Exception rollback){result.PersistenceRollbackError=rollback;}
   if(!isCurrent()){result.Cancelled=true;return result;}
   if(attempted)try{await restore();result.PreviousRestored=isCurrent();result.Cancelled=!result.PreviousRestored;}catch(Exception rollback){result.RestoreError=rollback;}
   return result;
  }
 }
}
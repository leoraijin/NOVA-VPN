using System;using System.Collections.Generic;using System.IO;using System.Threading.Tasks;using NovaVpn;
class RoutingApplyTransactionTests {
 static int checks;static void Check(bool ok){if(!ok)throw new Exception("Apply assertion "+(checks+1));checks++;}
 static Task Done()=>Task.CompletedTask;
 static async Task Run(){
  var order=new List<string>();bool current=true;
  Func<Task> validate=()=>{order.Add("validate");return Done();};Func<Task> start=()=>{order.Add("start");return Done();};Func<Task> restore=()=>{order.Add("restore");return Done();};Action save=()=>order.Add("save");Action undo=()=>order.Add("undo-save");
  var result=await RoutingApplyTransaction.ApplyAsync(()=>current,validate,start,save,undo,restore);Check(result.Applied);Check(string.Join(",",order)=="validate,start,save");
  order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>true,()=>throw new IOException("validate"),start,save,undo,restore);Check(!result.Applied&&result.Error!=null);Check(order.Count==0);
  order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>true,validate,()=>{order.Add("start");throw new IOException("start");},save,undo,restore);Check(result.PreviousRestored);Check(string.Join(",",order)=="validate,start,restore");
  order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>true,validate,start,()=>{order.Add("save");throw new IOException("save");},undo,restore);Check(result.PreviousRestored);Check(string.Join(",",order)=="validate,start,save,undo-save,restore");
  order.Clear();current=false;result=await RoutingApplyTransaction.ApplyAsync(()=>current,validate,start,save,undo,restore);Check(result.Cancelled);Check(order.Count==0);
  current=true;order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>current,()=>{current=false;return Done();},start,save,undo,restore);Check(result.Cancelled);Check(order.Count==0);
  current=true;order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>current,validate,()=>{current=false;throw new IOException("user stop during start");},save,undo,restore);Check(result.Cancelled);Check(string.Join(",",order)=="validate");
  order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>true,validate,()=>throw new IOException("start"),save,undo,()=>throw new IOException("restore"));Check(result.RestoreError!=null&&!result.PreviousRestored);Check(result.Error!=null);
  order.Clear();result=await RoutingApplyTransaction.ApplyAsync(()=>true,validate,start,()=>throw new IOException("save"),()=>throw new IOException("disk rollback"),restore);Check(result.PersistenceRollbackError!=null);Check(result.PreviousRestored);
  var pending=new TaskCompletionSource<bool>();current=true;order.Clear();var operation=RoutingApplyTransaction.ApplyAsync(()=>current,validate,()=>pending.Task,save,undo,restore);Check(!operation.IsCompleted);current=false;pending.SetResult(true);result=await operation;Check(result.Cancelled);Check(!order.Contains("save")&&!order.Contains("restore"));
 }
 static async Task CatalogRaces(){
  var settings=new MultiServerSettings{DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{
   new MultiServerRuleRecord{Domain="chat.example.test",Target="Profile",ProfileId="us"}}};
  var catalog=new[]{new VpnProfile{Id="fr",Server="old-fr.example.test"},new VpnProfile{Id="us",Server="old-us.example.test"},
   new VpnProfile{Id="unused",Server="old-unused.example.test"}};
  var validated=MultiServerConfigBuilder.CaptureProfiles(catalog);
  var method=typeof(MultiServerConfigBuilder).GetMethod("RequireCurrentCatalog",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
  var require=(Action<MultiServerSettings,IEnumerable<VpnProfile>,IEnumerable<VpnProfile>>)method.CreateDelegate(
   typeof(Action<MultiServerSettings,IEnumerable<VpnProfile>,IEnumerable<VpnProfile>>));
  catalog[0].Name="Cosmetic rename";catalog[2].Server="changed-unused.example.test";
  require(settings,validated,catalog);Check(true);

  var order=new List<string>();
  Func<Task> restore=()=>{order.Add("restore");return Done();};
  Action commit=()=>order.Add("commit");Action undo=()=>order.Add("undo-save");
  var result=await RoutingApplyTransaction.ApplyAsync(()=>true,()=>{
   catalog[1].RetiredFromSubscription=true;require(settings,validated,catalog);return Done();
  },()=>{order.Add("start");return Done();},commit,undo,restore);
  Check(result.Error is InvalidOperationException&&!result.Cancelled&&!result.Applied);Check(order.Count==0);

  catalog[1].RetiredFromSubscription=false;require(settings,validated,catalog);
  result=await RoutingApplyTransaction.ApplyAsync(()=>true,()=>Done(),()=>{
   order.Add("start");catalog[1].Server="rotated-us.example.test";require(settings,validated,catalog);return Done();
  },commit,undo,restore);
  Check(result.PreviousRestored&&!result.Cancelled&&!result.Applied);
  Check(string.Join(",",order)=="start,restore");
  var missingDefault=MultiServerConfigBuilder.CaptureProfiles(new[]{catalog[1]});
  bool returnedRejected=false;
  try{require(settings,missingDefault,catalog);}catch(InvalidOperationException){returnedRejected=true;}
  Check(returnedRejected);
 }
 static void Main(){Run().GetAwaiter().GetResult();CatalogRaces().GetAwaiter().GetResult();Console.WriteLine("PASS "+checks+" routing apply ordering/failure/cancellation assertions");}
}
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NovaVpn;

static class MultiExitHealthTests
{
    static int checks;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
    static Dictionary<string,object>[] Items(object value)=>((IEnumerable)value).Cast<object>().Cast<Dictionary<string,object>>().ToArray();
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static void Reject(Action action,string text){bool refused=false;try{action();}catch(Exception){refused=true;}Check(refused,text);}
    static VpnProfile Profile(string id)=>new VpnProfile{Id=id,Name=id.ToUpperInvariant(),Protocol="socks",Server="127.0.0.1",Port=10001,PortableOutboundJson="{\"type\":\"socks\",\"server\":\"127.0.0.1\",\"server_port\":10001,\"version\":\"5\"}"};
    static MultiServerSettings Policy(bool missing=false)=>new MultiServerSettings{Revision=2,DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{
        new MultiServerRuleRecord{ApplicationPath=@"C:\Synthetic\ChatGPT.exe",Target="Profile",ProfileId="us"},
        new MultiServerRuleRecord{Domain="blocked.example.test",Target="Block"},
        new MultiServerRuleRecord{Domain="direct.example.test",Target="Direct"},
        new MultiServerRuleRecord{Domain="optional.example.test",Target=missing?"Profile":"Direct",ProfileId=missing?"gone":null}}};
    static string Config(MultiServerSettings settings,VpnProfile[] profiles,string directory)=>MultiServerConfigBuilder.Build(new RoutingSettings{MultiServer=settings},profiles,Path.Combine(directory,"synthetic-dns.db"));
    static string Apply(ActiveCoreProbe probe,string config,MultiServerSettings settings,VpnProfile[] profiles)=>probe.ApplyMulti(config,settings,profiles);
    static async Task Contract(string output)
    {
        var fr=Profile("fr");var us=Profile("us");var profiles=new[]{us,fr};var policy=Policy();string original=Config(policy,profiles,output);
        using(var probe=new ActiveCoreProbe()) {
            string prepared=Apply(probe,original,policy,profiles);var before=Json.Deserialize<Dictionary<string,object>>(original);var after=Json.Deserialize<Dictionary<string,object>>(prepared);
            var listener=Items(after["inbounds"]).Last();var users=Items(listener["users"]);
            Check(Equals(listener["type"],"http")&&Equals(listener["listen"],"127.0.0.1")&&users.Length==2,"one authenticated HTTP listener, two users");
            Check(users.Select(u=>u["username"]).Distinct().Count()==2&&users.Select(u=>u["password"]).Distinct().Count()==2,"credentials differ");
            var route=(Dictionary<string,object>)after["route"];var rules=Items(route["rules"]);
            for(int i=0;i<2;i++) {
                string tag="nova-exit-"+i;var resolve=rules[i*2];var send=rules[i*2+1];
                Check(Equals(resolve["server"],"dns-"+tag)&&Equals(resolve["action"],"resolve")&&Equals(resolve["strategy"],"ipv4_only")&&Equals(resolve["disable_cache"],true),"each health DNS uses own resolver, v4/no cache");
                Check(Equals(send["action"],"route")&&Equals(send["outbound"],tag),"each route uses exact exit");
                Check(((IEnumerable)resolve["auth_user"]).Cast<object>().Single().Equals(users[i]["username"])&&Json.Serialize(resolve["auth_user"])==Json.Serialize(send["auth_user"]),"resolve and route share same auth identity");
            }
            Check(Equals(rules[4]["action"],"reject")&&rules[4].Count==2,"unmatched health inbound rejects before ordinary routes");
            after["inbounds"]=Items(after["inbounds"]).Take(Items(before["inbounds"]).Length).ToArray();route["rules"]=rules.Skip(5).ToArray();
            Check(Json.Serialize(before)==Json.Serialize(after),"ordinary rules, TUN, DNS and outbounds preserved");
            Check(probe.MultiTargets.Select(t=>t.ProfileId).SequenceEqual(new[]{"fr","us"}),"targets reference exact sorted IDs");
            var fingerprint=probe.MultiTargets[0].ProfileFingerprint;fr.Password="catalog changed";
            Check(probe.MultiTargets[0].ProfileFingerprint==fingerprint&&fingerprint!=ExitProbeTarget.Fingerprint(fr),"captured fingerprint immutable after catalog mutation");fr.Password=null;
            Reject(()=>((IList<ExitProbeTarget>)probe.MultiTargets).Clear(),"target collection immutable");
            Reject(()=>probe.ReadStatusAsync(new Uri("https://example.test/"),CancellationToken.None).GetAwaiter().GetResult(),"legacy channel cannot certify group");
            Reject(()=>Apply(probe,original,policy,profiles),"prepared channel cannot be rebound");
            Reject(()=>probe.Apply(original),"multi channel cannot be repurposed by legacy apply");
            File.WriteAllText(Path.Combine(output,"synthetic-multi-health.json"),prepared);
        }
        var missingPolicy=Policy(true);string missing=Config(missingPolicy,profiles,output);
        using(var probe=new ActiveCoreProbe()) {
            Apply(probe,missing,missingPolicy,profiles);
            Check(probe.MultiTargets.Single(t=>t.ProfileId=="gone").Phase==ExitProbePhase.Unavailable,"missing reference remains unavailable");
            Reject(()=>probe.ReadExitStatusAsync("gone",new Uri("https://example.test/"),CancellationToken.None).GetAwaiter().GetResult(),"missing profile has no fallback channel");
            using(var canceled=new CancellationTokenSource()) {
                canceled.Cancel();bool refused=false;try{await probe.ReadExitStatusAsync("fr",new Uri("https://example.test/"),canceled.Token);}catch(Exception){refused=true;}
                Check(refused,"canceled actual channel cannot succeed");
            }
            probe.Dispose();Reject(()=>probe.ReadExitStatusAsync("fr",new Uri("https://example.test/"),CancellationToken.None).GetAwaiter().GetResult(),"disposed channel refuses access");
        }
        foreach(string mutation in new[]{"dns","outbound","duplicate","missing-tag","insecure","collision"}) {
            var value=Json.Deserialize<Dictionary<string,object>>(original);
            if(mutation=="dns")Items(((Dictionary<string,object>)value["dns"])["servers"]).Single(d=>Equals(d["tag"],"dns-nova-exit-0"))["detour"]="nova-exit-1";
            if(mutation=="insecure")((Dictionary<string,object>)Items(((Dictionary<string,object>)value["dns"])["servers"]).Single(d=>Equals(d["tag"],"dns-nova-exit-0"))["tls"])["insecure"]=true;
            if(mutation=="outbound")Items(value["outbounds"]).Single(o=>Equals(o["tag"],"nova-exit-0"))["server_port"]=12345;
            if(mutation=="duplicate")value["outbounds"]=Items(value["outbounds"]).Concat(Items(value["outbounds"]).Take(1)).ToArray();
            if(mutation=="missing-tag")Items(value["outbounds"]).Single(o=>Equals(o["tag"],"nova-exit-0"))["tag"]="nova-exit-99";
            if(mutation=="collision")Items(value["inbounds"]).First()["tag"]="nova-active-check";
            using(var probe=new ActiveCoreProbe())Reject(()=>Apply(probe,Json.Serialize(value),policy,profiles),"unsafe binding refused: "+mutation);
        }
        us.RetiredFromSubscription=true;
        using(var probe=new ActiveCoreProbe()) {
            Apply(probe,Config(policy,profiles,output),policy,profiles);
            Check(probe.MultiTargets.Single(t=>t.ProfileId=="us").Phase==ExitProbePhase.Unavailable,"retired assignment not checked via another server");
        }
        us.RetiredFromSubscription=false;
    }
    static async Task Runner()
    {
        var fr=Profile("fr");var us=Profile("us");
        var targets=new[]{new ExitProbeTarget("fr","FR",ExitProbeTarget.Fingerprint(fr),ExitProbePhase.Queued),new ExitProbeTarget("us","US",ExitProbeTarget.Fingerprint(us),ExitProbePhase.Queued)};
        var calls=new List<string>();var sync=new object();
        var results=await MultiExitHealthRunner.RunAsync(targets,(id,uri,token)=>{lock(sync)calls.Add(id);return Task.FromResult(id=="fr"?204:503);},CancellationToken.None);
        Check(results[0].Phase==ExitProbePhase.Verified&&results[1].Phase==ExitProbePhase.Failed,"exit success independent of another failure");
        Check(calls.Count(id=>id=="fr")==1&&calls.Count(id=>id=="us")==2,"two bounded targets only for failed exit");
        Check(results[0].IsFreshFor(fr,DateTime.UtcNow)&&!results[0].IsFreshFor(us,DateTime.UtcNow),"proof cannot certify another profile");
        Check(results[0].Label(results[0].CheckedUtc.AddMinutes(4))=="Нужна свежая проверка"&&!results[0].HasRecentSuccess(results[0].CheckedUtc.AddSeconds(-1)),"expired/future UI proof is not green");
        var snapshot=new MultiExitHealthSnapshot(results,"network","core");
        Check(!snapshot.AllVerified&&snapshot.RecoveryHealthy==null&&!snapshot.IsFreshFor(new[]{fr,us},"network","core",DateTime.UtcNow),"partial success never certifies full policy");
        var unavailable=new[]{new ExitProbeTarget("missing","gone",null,ExitProbePhase.Unavailable),new ExitProbeTarget("unsupported","unknown","fingerprint",ExitProbePhase.Unsupported)};
        var absent=await MultiExitHealthRunner.RunAsync(unavailable,(id,uri,token)=>{throw new Exception("must not create a request");},CancellationToken.None);
        Check(absent[0].Phase==ExitProbePhase.Unavailable&&absent[1].Phase==ExitProbePhase.Unsupported,"unavailable/unsupported do not generate network traffic");
        Check(new MultiExitHealthSnapshot(absent,"network","core").RecoveryHealthy==null,"missing or unsupported exits do not request endless whole-tunnel recovery");
        var failed=await MultiExitHealthRunner.RunAsync(targets,(id,uri,token)=>Task.FromResult(503),CancellationToken.None);
        Check(new MultiExitHealthSnapshot(failed,"network","core").RecoveryHealthy==false,"all supported exits failing may request recovery of same policy");
        var retiredAndFailed = new MultiExitHealthSnapshot(new[] { absent[0], failed[0] }, "network", "core");
        Check(retiredAndFailed.RecoveryHealthy == false, "retired assignment cannot mask a failure of the remaining exit");
        Check(new MultiExitHealthSnapshot(new[] { absent[0], results[0] }, "network", "core").RecoveryHealthy == null,
            "working exit remains connected when another assignment retired");
        Check(new MultiExitHealthSnapshot(new[] { absent[0], absent[1], failed[0] }, "network", "core").RecoveryHealthy == null,
            "unverified unsupported exit prevents whole-tunnel recovery");
        Check(new MultiExitHealthSnapshot(new[] { absent[0] }, "network", "core").RecoveryHealthy == null,
            "a policy with no outbound does not request recovery");
        var aggregate=new ConnectionVerification{Completed=false};
        typeof(ConnectionVerification).GetProperty("MultiExits").GetSetMethod(true).Invoke(aggregate,new object[]{new MultiExitHealthSnapshot(failed,"network","core")});
        Check(aggregate.RecoveryHealthy==null,"incomplete group cannot request tunnel recovery");
        aggregate.Completed=true;Check(aggregate.RecoveryHealthy==false,"complete all-failed group reaches the recovery predicate");
        typeof(ConnectionVerification).GetProperty("MultiExits").GetSetMethod(true).Invoke(aggregate,new object[]{new MultiExitHealthSnapshot(results,"network","core")});
        Check(aggregate.RecoveryHealthy==null,"actual aggregate recovery predicate preserves other working exits");
        var success=await MultiExitHealthRunner.RunAsync(targets,(id,uri,token)=>Task.FromResult(204),CancellationToken.None);
        snapshot=new MultiExitHealthSnapshot(success,"network","core");
        Check(snapshot.AllVerified&&snapshot.IsFreshFor(new[]{fr,us},"network","core",DateTime.UtcNow),"all exact exits certify whole set");
        Check(!snapshot.IsFreshFor(new[]{fr,us},"other","core",DateTime.UtcNow)&&!snapshot.IsFreshFor(new[]{fr,us},"network","other",DateTime.UtcNow),"network/core changes invalidate group");
        Check(!snapshot.IsFreshFor(new[]{fr,us},"network","core",DateTime.UtcNow.AddMinutes(4)),"old results expire");
        us.Password="changed";Check(!snapshot.IsFreshFor(new[]{fr,us},"network","core",DateTime.UtcNow),"catalog edit invalidates group proof");us.Password=null;
        us.RetiredFromSubscription=true;Check(!snapshot.IsFreshFor(new[]{fr,us},"network","core",DateTime.UtcNow),"retired catalog entry invalidates current proof");us.RetiredFromSubscription=false;
        Check(!snapshot.IsFreshFor(new[]{fr},"network","core",DateTime.UtcNow),"missing catalog entry cannot borrow another result");
        int active=0,maximum=0;var ten=Enumerable.Range(0,10).Select(i=>new ExitProbeTarget("id"+i,"same","fingerprint",ExitProbePhase.Queued)).ToArray();
        await MultiExitHealthRunner.RunAsync(ten,async(id,uri,token)=>{int current=Interlocked.Increment(ref active);lock(sync)maximum=Math.Max(maximum,current);try{await Task.Delay(20,token);return 204;}finally{Interlocked.Decrement(ref active);}},CancellationToken.None);
        Check(maximum==4&&active==0,"at most four simultaneous checks");
        using(var cts=new CancellationTokenSource()) {
            var entered=new TaskCompletionSource<bool>();
            var run=MultiExitHealthRunner.RunAsync(ten,async(id,uri,token)=>{entered.TrySetResult(true);await Task.Delay(10000,token);return 204;},cts.Token);
            await entered.Task;cts.Cancel();bool canceled=false;try{await run;}catch(OperationCanceledException){canceled=true;}
            Check(canceled,"cancelled group never publishes successful late results");
        }
        Reject(()=>((IList<ExitProbeResult>)results).Clear(),"result collection immutable");
    }
    static async Task Lifecycle(string output)
    {
        var fr=Profile("fr");var us=Profile("us");var policy=Policy();var profiles=new[]{fr,us};
        using(var child=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--idle-fixture"){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true}))
        using(var manager=new CoreManager())
        using(var probe=new ActiveCoreProbe()) {
            try {
                probe.ApplyMulti(Config(policy,profiles,output),policy,profiles);
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                Action<string,object> field=(name,value)=>typeof(CoreManager).GetField(name,flags).SetValue(manager,value);
                field("process",child);field("activeProbe",probe);field("connectedProfile",fr);field("connectedProfileKey",SubscriptionManager.ConfigKey(fr));
                typeof(CoreManager).GetProperty("Status").GetSetMethod(true).Invoke(manager,new object[]{CoreStatus.Connected});
                typeof(CoreManager).GetProperty("LastConnectedRouting").GetSetMethod(true).Invoke(manager,new object[]{new RoutingSettings{MultiServer=policy.Copy()}});
                field("multiExitVerificationReader",new Func<string,Uri,CancellationToken,Task<int>>((id,uri,token)=>Task.FromResult(id=="fr"?204:503)));
                var partial=await manager.VerifyConnectionAsync();
                Check(partial.Completed&&partial.MultiExits!=null&&partial.MultiExits.Results[0].Phase==ExitProbePhase.Verified&&partial.MultiExits.Results[1].Phase==ExitProbePhase.Failed,"actual manager publishes separate outcomes from captured channel");
                Check(ReferenceEquals(manager.Verification,partial)&&!partial.InternetOk&&!partial.DnsOk&&partial.ProfileProbe==null,"legacy primary proof cannot certify aggregate");
                Check(manager.LastConnectedRouting.MultiServer.DefaultProfileId=="fr"&&manager.LastConnectedRouting.MultiServer.Rules[0].ProfileId=="us","failed exit leaves all assignments unchanged");
                int published=0;manager.VerificationChanged+=v=>published++;
                var entered=new TaskCompletionSource<bool>();var release=new TaskCompletionSource<int>();
                field("multiExitVerificationReader",new Func<string,Uri,CancellationToken,Task<int>>((id,uri,token)=>{entered.TrySetResult(true);return release.Task;}));
                var old=manager.VerifyConnectionAsync();await entered.Task;
                manager.InvalidateNetworkVerification();
                field("multiExitVerificationReader",new Func<string,Uri,CancellationToken,Task<int>>((id,uri,token)=>Task.FromResult(204)));
                var current=await manager.VerifyConnectionAsync();
                Check(current.Completed&&current.MultiExits.AllVerified&&ReferenceEquals(current,manager.Verification),"new network operation can publish current group");
                release.TrySetResult(204);var stale=await old;
                Check(!stale.Completed&&ReferenceEquals(current,manager.Verification)&&published==1,"late cancelled old group cannot replace new proof or publish event");
                manager.InvalidateNetworkVerification();
                Check(!manager.Verification.Completed&&manager.Verification.MultiExits==null,"network invalidation clears every exit result");
            } finally {
                // Detach only this synthetic runtime, then gracefully stop this retained stdin fixture.
                typeof(CoreManager).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(manager,null);
                child.StandardInput.WriteLine("exit");child.StandardInput.Flush();
                Check(child.WaitForExit(5000),"owned idle fixture exited normally");
            }
        }
    }
    public static int Main(string[] args) {
        if(args.Length==1&&args[0]=="--idle-fixture"){Console.ReadLine();return 0;}
        try{string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);Contract(output).GetAwaiter().GetResult();Runner().GetAwaiter().GetResult();Lifecycle(output).GetAwaiter().GetResult();
            Console.WriteLine("MultiExitHealthTests: "+checks+" assertions passed; synthetic contract/runner checks only, no TUN or external requests.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}

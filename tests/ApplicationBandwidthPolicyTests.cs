using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NovaVpn;
class ApplicationBandwidthPolicyTests
{
    static int checks;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    static void Reject(Action action,string message){bool refused=false;try{action();}catch(ArgumentException){refused=true;}catch(InvalidDataException){refused=true;}catch(InvalidOperationException){refused=true;}Check(refused,message);}
    static ApplicationBandwidthRule Rule(string sid,string path)=>new ApplicationBandwidthRule{UserSid=sid,ExecutablePath=path,UploadBytesPerSecond=2048,DownloadBytesPerSecond=4096,BurstBytes=1024};
    static byte[] Encode(AppState state){using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(AppState)).WriteObject(stream,state);return stream.ToArray();}}
    static AppState Decode(byte[] bytes){using(var stream=new MemoryStream(bytes))return (AppState)new DataContractJsonSerializer(typeof(AppState)).ReadObject(stream);}
    static int Main(){try{Run().GetAwaiter().GetResult();Console.WriteLine("PASS persistent bandwidth policy: "+checks+" checks; DataContract, own current-user DPAPI fixture, encrypted transfer and settings-only redaction; no production state writes");return 0;}catch(Exception e){Console.Error.WriteLine("Completed checks="+checks);Console.Error.WriteLine(e);return 1;}}
    static async Task Run(){using(var user=WindowsIdentity.GetCurrent())using(var self=Process.GetCurrentProcess()){
        string sid=user.User.Value,path=self.MainModule.FileName;var state=new AppState{ApplicationLimitsEnabled=true,ApplicationBandwidthRules=new List<ApplicationBandwidthRule>{Rule(sid,path)}};
        StateStore.NormalizeForSerialization(state);Check(state.SchemaVersion==17,"Current schema retains explicit application policy");
        byte[] bytes=Encode(state),entropy=Encoding.UTF8.GetBytes("NOVA-own-bandwidth-policy-test");byte[] encrypted=ProtectedData.Protect(bytes,entropy,DataProtectionScope.CurrentUser);Check(!encrypted.SequenceEqual(bytes),"Own fixture state protected by actual current-user DPAPI");var restored=Decode(ProtectedData.Unprotect(encrypted,entropy,DataProtectionScope.CurrentUser));
        Check(restored.ApplicationLimitsEnabled&&restored.ApplicationBandwidthRules.Count==1&&restored.ApplicationBandwidthRules[0].UserSid==sid&&restored.ApplicationBandwidthRules[0].ExecutablePath==path&&restored.ApplicationBandwidthRules[0].BurstBytes==1024,"DPAPI/DataContract roundtrip retains identity/rates/burst");
        var policy=ApplicationBandwidthPolicySnapshot.Freeze(restored);Check(policy.Enabled&&policy.Policies.Count==1&&policy.Policies[0].UploadBytesPerSecond==2048&&policy.Policies[0].DownloadBytesPerSecond==4096,"Frozen policy retains both directions");
        restored.ApplicationBandwidthRules[0].UploadBytesPerSecond=1;restored.ApplicationBandwidthRules[0].ExecutablePath=Path.Combine(Path.GetDirectoryName(path),"changed.exe");restored.ApplicationBandwidthRules.Clear();restored.ApplicationLimitsEnabled=false;
        Check(policy.Policies[0].UploadBytesPerSecond==2048&&policy.Policies[0].Identity.ExecutablePath==path,"Mutation of saved rows cannot change a frozen policy");
        using(var controller=policy.CreateController()){
            var first=controller.Get(new ApplicationBandwidthIdentity(sid,path));Check(Object.ReferenceEquals(first,controller.Get(new ApplicationBandwidthIdentity(sid,path.ToUpperInvariant()))),"Frozen rules create same shared SID/path budget");
            var watch=Stopwatch.StartNew();using(var output=new MemoryStream())using(var input=new MemoryStream(Enumerable.Repeat((byte)73,2048).ToArray())){long count=await first.CopyPayloadAsync(BandwidthDirection.Upload,input,output,CancellationToken.None);Check(count==2048&&output.ToArray().All(b=>b==73)&&watch.ElapsedMilliseconds>=850,"Persisted upload rule actually paces byte-preserving payload");}
            Check(!Object.ReferenceEquals(first,controller.Get(new ApplicationBandwidthIdentity(sid=="S-1-5-18"?"S-1-5-19":"S-1-5-18",path))),"Foreign SID is never rebound to the current user");
        }
        var copy=FullTransfer.Copy(state);Check(copy.ApplicationLimitsEnabled&&copy.ApplicationBandwidthRules[0].UserSid==sid,"Detached local state copy retains enable intent");
        string package=FullTransfer.Export(state,"fixture-password");var imported=FullTransfer.Import(package,"fixture-password");Check(!imported.ApplicationLimitsEnabled&&imported.ApplicationBandwidthRules.Single().UserSid==sid&&imported.ApplicationBandwidthRules[0].DownloadBytesPerSecond==4096,"Full encrypted transfer retains exact rules without autoactivation/rebinding");
        imported.ApplicationLimitsEnabled=true;Check(ApplicationBandwidthPolicySnapshot.Freeze(imported).Policies[0].Identity.ExecutablePath==path,"Imported rules can be explicitly reviewed and enabled");
        string settings=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bandwidth-settings-"+Guid.NewGuid().ToString("N")+".json");try{PortableConfigService.ExportSettingsOnly(state,settings);var clean=PortableConfigService.ImportSettingsOnly(settings);Check(!clean.ApplicationLimitsEnabled&&clean.ApplicationBandwidthRules.Count==0,"Settings-only transfer excludes machine-bound rule policy");string text=File.ReadAllText(settings);Check(!text.Contains(sid)&&!text.Contains(Path.GetFileName(path)),"Settings-only file exposes no policy SID/image name");Check(state.ApplicationLimitsEnabled&&state.ApplicationBandwidthRules.Count==1,"Redaction leaves original saved policy unchanged");}finally{if(File.Exists(settings))File.Delete(settings);if(File.Exists(settings+".tmp"))File.Delete(settings+".tmp");}
        var legacy=Decode(Encoding.UTF8.GetBytes("{\"SchemaVersion\":15}"));StateStore.NormalizeForSerialization(legacy);Check(!legacy.ApplicationLimitsEnabled&&legacy.ApplicationBandwidthRules.Count==0&&legacy.SchemaVersion==17,"Old settings migrate disabled with empty rules");
        var disabled=ApplicationBandwidthPolicySnapshot.Freeze(legacy);Check(!disabled.Enabled&&disabled.Policies.Count==0,"Disabled policy is explicit");Reject(()=>disabled.CreateController(),"Disabled policy cannot accidentally create an enforcing registry");
        foreach(string mode in new[]{"null-list","empty","too-many","null-row","duplicate","negative-up","negative-down","rate-max","zero","burst-zero","burst-max","bad-sid","relative","not-exe","missing-burst"}){
            var invalid=new AppState{ApplicationLimitsEnabled=true,ApplicationBandwidthRules=new List<ApplicationBandwidthRule>{Rule(sid,path)}};var row=invalid.ApplicationBandwidthRules[0];
            switch(mode){case "null-list":invalid.ApplicationBandwidthRules=null;break;case "empty":invalid.ApplicationBandwidthRules.Clear();break;case "too-many":invalid.ApplicationBandwidthRules=Enumerable.Range(0,1025).Select(i=>Rule(sid,Path.Combine(Path.GetDirectoryName(path),"app"+i+".exe"))).ToList();break;case "null-row":invalid.ApplicationBandwidthRules.Add(null);break;case "duplicate":invalid.ApplicationBandwidthRules.Add(Rule(sid,path.ToUpperInvariant()));break;case "negative-up":row.UploadBytesPerSecond=-1;break;case "negative-down":row.DownloadBytesPerSecond=-1;break;case "rate-max":row.DownloadBytesPerSecond=ApplicationBandwidthBudget.MaximumBytesPerSecond+1;break;case "zero":row.UploadBytesPerSecond=row.DownloadBytesPerSecond=0;break;case "burst-zero":row.BurstBytes=0;break;case "burst-max":row.BurstBytes=ApplicationBandwidthBudget.MaximumBurstBytes+1;break;case "bad-sid":row.UserSid="not-a-sid";break;case "relative":row.ExecutablePath="relative.exe";break;case "not-exe":row.ExecutablePath=Path.ChangeExtension(path,"dll");break;case "missing-burst":invalid=Decode(Encoding.UTF8.GetBytes("{\"SchemaVersion\":16,\"ApplicationLimitsEnabled\":true,\"ApplicationBandwidthRules\":[{\"UserSid\":\""+sid+"\",\"ExecutablePath\":\"D:\\\\apps\\\\test.exe\",\"UploadBytesPerSecond\":1}]}"));break;}
            StateStore.NormalizeForSerialization(invalid);Check(invalid.ApplicationLimitsEnabled,"Normalization does not silently disable malformed policy: "+mode);Reject(()=>ApplicationBandwidthPolicySnapshot.Freeze(invalid),"Invalid enabled policy refused as a whole: "+mode);Reject(()=>FullTransfer.Export(invalid,"fixture-password"),"Invalid active policy cannot be exported as valid: "+mode);
        }
        for(int version=0;version<16;version++){
            var partial=Decode(Encoding.UTF8.GetBytes("{\"SchemaVersion\":"+version+",\"ApplicationLimitsEnabled\":true,\"ApplicationBandwidthRules\":null}"));
            StateStore.NormalizeForSerialization(partial);Check(partial.ApplicationLimitsEnabled&&partial.Routing!=null&&partial.SchemaVersion==17,"Partial legacy migration preserves active malformed policy intent: "+version);Reject(()=>ApplicationBandwidthPolicySnapshot.Freeze(partial),"Incomplete legacy rule set stays refused after migration: "+version);
        }
        var maximum=new AppState{ApplicationLimitsEnabled=true,ApplicationBandwidthRules=Enumerable.Range(0,1024).Select(i=>Rule(sid,Path.Combine(Path.GetDirectoryName(path),"app"+i+".exe"))).ToList()};Check(ApplicationBandwidthPolicySnapshot.Freeze(maximum).Policies.Count==1024,"Bounded maximum rule set accepted");
        var literal=new AppState{ApplicationLimitsEnabled=true,ApplicationBandwidthRules=new List<ApplicationBandwidthRule>{Rule(sid,Path.Combine(Path.GetDirectoryName(path),"%TEMP%-literal.exe"))}};Check(ApplicationBandwidthPolicySnapshot.Freeze(literal).Policies[0].Identity.ExecutablePath.EndsWith("%TEMP%-literal.exe"),"Percent-containing image path remains literal");
    }}
}

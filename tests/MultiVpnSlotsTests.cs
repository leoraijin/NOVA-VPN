using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Web.Script.Serialization;
using NovaVpn;
class MultiVpnSlotsTests {
    static int checks;
    static void Check(bool ok,string why) { if(!ok) throw new Exception(why); checks++; }
    const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Set(MultiServerSettings s,int n,string id) => typeof(MultiServerSettings).GetMethod("SetSlot",Hidden).Invoke(s,new object[]{n,id});
    static T Roundtrip<T>(T value) { using(var s=new MemoryStream()) { var c=new DataContractJsonSerializer(typeof(T));c.WriteObject(s,value);s.Position=0;return (T)c.ReadObject(s); } }
    static bool Refuses(Action action) { try { action();return false; } catch {return true;} }
    static VpnProfile Profile(int n) => new VpnProfile {Id="p"+n,Name="VPN-"+n,Protocol="vless",Server="vpn"+n+".example.test",Port=443,UserId="11111111-1111-4111-8111-111111111111",Transport="tcp",Security="tls",Sni="vpn"+n+".example.test"};
    static int Exits(string json) {var c=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);return ((IEnumerable)c["outbounds"]).Cast<Dictionary<string,object>>().Count(o=>Equals(o["type"],"vless"));}
    static void Main(string[] args) {
        var s=new MultiServerSettings {DefaultProfileId="p1",Revision=1};
        for(int n=2;n<=10;n++) {
            Set(s,n,"p"+n);
            s.Rules.Add(new MultiServerRuleRecord {Domain="vpn"+n+".example.org",Target="Profile",ProfileId="p"+n});
            s.Rules.Add(new MultiServerRuleRecord {ApplicationName="app"+n+".exe",Target="Profile",ProfileId="p"+n});
        }
        s=Roundtrip(s);Check(s.Slots.Count==9,"slot roundtrip");
        var all=Enumerable.Range(1,10).Select(Profile).ToArray();
        var routes=new RoutingSettings {MultiServer=s};
        Check(Exits(MultiServerConfigBuilder.Build(routes,all,Path.Combine(args[0],"ten.db")))==10,"ten compiled exits");
        var adapted=(string)typeof(RealityConfigBuilder).GetMethod("AdaptMultiServer",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,new object[]{MultiServerConfigBuilder.Build(routes,all,Path.Combine(args[0],"reality-ten.db"))});
        File.WriteAllText(Path.Combine(args[0],"ten-config.json"),MultiServerConfigBuilder.Build(routes,all,Path.Combine(args[0],"ten.db")));
        File.WriteAllText(Path.Combine(args[0],"reality-ten-config.json"),adapted);
        Check(Exits(adapted)==10,"ten Reality exits");
        var policy=s.Compile(all.Select(p=>p.Id));
        for(int n=2;n<=10;n++) {
            Check(policy.Decide(null,"vpn"+n+".example.org").Effective.ProfileId=="p"+n,"site exit");
            Check(policy.Decide(@"C:\Apps\app"+n+".exe",null).Effective.ProfileId=="p"+n,"app exit");
        }
        var paused=s.Copy();paused.SecondaryPaused=true;paused=Roundtrip(paused);
        Check(paused.Rules.Count==18 && paused.Slots.Count==9,"paused persistence");
        Check(Exits(MultiServerConfigBuilder.Build(new RoutingSettings {MultiServer=paused},new[]{all[0]},Path.Combine(args[0],"paused.db")))==1,"paused compiler only primary");
        var fallback=paused.Compile(new[]{"p1"});
        for(int n=2;n<=10;n++) {
            Check(fallback.Decide(null,"vpn"+n+".example.org").Effective.ProfileId=="p1","paused site");
            Check(fallback.Decide(@"C:\Apps\app"+n+".exe",null).Effective.ProfileId=="p1","paused app");
        }
        paused.SecondaryPaused=false;
        Check(paused.Compile(all.Select(p=>p.Id)).Decide(null,"vpn10.example.org").Effective.ProfileId=="p10","resume");
        var removed=s.Copy();Set(removed,5,null);
        Check(!removed.Slots.ContainsKey(5)&&removed.Slots[6]=="p6","stable numbers");
        Check(removed.Rules.Count==16 && removed.Rules.All(r=>r.ProfileId!="p5"),"remove own rules");
        Check(s.Rules.Count==18,"clone isolation");
        Set(removed,5,"new5");Check(removed.Rules.All(r=>r.ProfileId!="new5"),"new slot no stale rules");
        Check(Refuses(()=>Set(s.Copy(),11,"p11")),"limit");
        Check(Refuses(()=>Set(s.Copy(),5,"p8")),"duplicate");
        Check(Refuses(()=>Set(s.Copy(),5,"p1")),"primary collision");
        var draftType=typeof(AppState).Assembly.GetType("NovaVpn.RoutingCardsDraft");
        var draft=Activator.CreateInstance(draftType,Hidden,null,new object[]{routes},null);
        var groups=((IEnumerable)draftType.GetField("Groups",Hidden).GetValue(draft)).Cast<object>().ToArray();
        Check(groups.Length==24,"dynamic groups");
        var group=groups.Single(g=>(string)g.GetType().GetField("Key",Hidden).GetValue(g)=="RoutingSitesVpn10");
        ((List<string>)group.GetType().GetField("Values",Hidden).GetValue(group)).Add("new.example.org");
        var built=(RoutingSettings)draftType.GetMethod("Build",Hidden).Invoke(draft,null);
        Check(built.MultiServer.Rules.Any(r=>r.Domain=="new.example.org"&&r.ProfileId=="p10"),"dynamic group edits correct exit");
        Check(built.MultiServer.Rules.Count==19 && s.Rules.Count==18,"dynamic edit preserves all lists");
        var full=Path.Combine(args[0],"full.json");PortableConfigService.Export(new AppState{Routing=routes},full);
        Check(PortableConfigService.Import(full).Routing.MultiServer.Slots.Count==9,"full export");
        var settings=Path.Combine(args[0],"settings.json");PortableConfigService.ExportSettingsOnly(new AppState{Routing=routes},settings);
        Check(PortableConfigService.ImportSettingsOnly(settings).Routing.MultiServer.Slots.Count==9,"settings export");
        Console.WriteLine("PASS "+checks+" multi VPN slot assertions");
    }
}

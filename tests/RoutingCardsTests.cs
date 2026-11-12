using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using NovaVpn;
class RoutingCardsTests
{
    static int checks;
    static readonly Type DraftType = typeof(AppState).Assembly.GetType("NovaVpn.RoutingCardsDraft", true);
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static object Draft(RoutingSettings source) => Activator.CreateInstance(DraftType, Hidden, null, new object[] {source}, null);
    static object Group(object draft, string key) => ((IEnumerable)DraftType.GetField("Groups", Hidden).GetValue(draft)).Cast<object>()
        .Single(g => (string)g.GetType().GetField("Key", Hidden).GetValue(g) == key);
    static List<string> Values(object draft, string key) { var g = Group(draft,key); return (List<string>)g.GetType().GetField("Values",Hidden).GetValue(g); }
    static string Warning(object draft,string key) => (string)DraftType.GetMethod("WarningFor",Hidden).Invoke(draft,new[]{Group(draft,key)});
    static RoutingSettings Build(object draft) => (RoutingSettings)DraftType.GetMethod("Build",Hidden).Invoke(draft,null);
    static T Roundtrip<T>(T value) { using(var s=new MemoryStream()){var c=new DataContractJsonSerializer(typeof(T));c.WriteObject(s,value);s.Position=0;return (T)c.ReadObject(s);} }
    static VpnProfile Profile(string id) => new VpnProfile {Id=id,Name=id,Protocol="vless",Server=id+".example.test",Port=443,
        UserId="11111111-1111-4111-8111-111111111111",Transport="tcp",Security="tls",Sni=id+".example.test"};
    static Dictionary<string,object>[] Items(object o) => ((IEnumerable)o).Cast<object>().Cast<Dictionary<string,object>>().ToArray();
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        var source=new RoutingSettings {MultiServer=new MultiServerSettings {Revision=5,DefaultProfileId="fr",SecondaryProfileId="us"}};
        for(int i=0;i<13;i++)source.MultiServer.Rules.Add(new MultiServerRuleRecord{Domain="direct-"+i+".test",Target="Direct"});
        for(int i=0;i<72;i++)source.MultiServer.Rules.Add(new MultiServerRuleRecord{Domain="primary-"+i+".test",Target="Default"});
        source.MultiServer.Rules.Add(new MultiServerRuleRecord{Domain="wide.test",IncludeSubdomains=true,Target="Default"});
        source.MultiServer.Rules.Add(new MultiServerRuleRecord{ApplicationPath=@"C:\Apps\ChatGPT.exe",Target="Direct"});
        source.DirectProcesses.Add("steam.exe");source.ProxyProcesses.Add("Discord.exe");source.BlockDomains.Add("blocked.test");
        var before=RoutingMigrationService.Fingerprint(source);var draft=Draft(source);
        Check(Values(draft,"RoutingSitesDirect").Count==13,"direct rules invisible");
        Check(Values(draft,"RoutingSitesPrimary").Count==73,"primary rules invisible");
        Check(Values(draft,"RoutingAppsDirect").SequenceEqual(new[]{@"C:\Apps\ChatGPT.exe","steam.exe"}),"legacy direct apps hidden");
        Check(Values(draft,"RoutingAppsPrimary").Contains("Discord.exe"),"legacy primary apps hidden");
        Values(draft,"RoutingSitesSecondary").Add("https://DIRECT-0.test/a");
        Check(Warning(draft,"RoutingSitesSecondary").Contains("Сайты напрямую"),"normalized duplicate not reported");
        Values(draft,"RoutingSitesSecondary").Add("primary-0.test");
        Values(draft,"RoutingAppsSecondary").Add(@"c:\apps\CHATGPT.exe");
        Check(Warning(draft,"RoutingAppsSecondary").Contains("Приложения напрямую"),"path duplicate not reported");
        Values(draft,"RoutingAppsSecondary").Add("Discord.exe");
        Values(draft,"RoutingAppsPrimary").Add("new.exe");
        Values(draft,"RoutingSitesPrimary").Add("new.test");
        Values(draft,"RoutingSitesBlocked").Add("another-block.test");
        var next=Build(draft);
        Check(RoutingMigrationService.Fingerprint(source)==before,"editing changed original state");
        Check(next.MultiServer.Rules.Count(r=>r.Domain=="direct-0.test")==2,"duplicate removed from other card");
        Check(next.MultiServer.Rules.Count(r=>r.Domain=="primary-0.test")==2,"primary duplicate removed");
        Check(next.MultiServer.Rules.Any(r=>r.Domain=="wide.test"&&r.IncludeSubdomains),"subdomain scope lost");
        Check(next.DirectProcesses.SequenceEqual(source.DirectProcesses),"direct legacy names changed");
        Check(next.ProxyProcesses.SequenceEqual(new[]{"Discord.exe","new.exe"}),"primary legacy names lost");
        Check(next.BlockDomains.Contains("blocked.test"),"original block list changed");
        Check(next.MultiServer.Rules.Any(r=>r.ApplicationName=="Discord.exe"&&r.ProfileId=="us"),"secondary name absent");
        var policy=next.MultiServer.Compile(new[]{"fr","us"});
        Check(policy.Decide(null,"direct-0.test").Effective.Kind==ServerRouteKind.Direct,"direct duplicate precedence");
        Check(policy.Decide(null,"primary-0.test").Effective.ProfileId=="us","secondary duplicate precedence");
        Check(policy.Decide(@"C:\Apps\ChatGPT.exe",null).Effective.Kind==ServerRouteKind.Direct,"path duplicate precedence");
        Check(policy.Decide(@"C:\Apps\DISCORD.exe",null).Effective.ProfileId=="us","process name does not match path");
        Check(policy.Decide(null,"sub.wide.test").Effective.ProfileId=="fr","subdomain route changed");
        var restored=Roundtrip(next);
        Check(restored.MultiServer.Rules.Count==next.MultiServer.Rules.Count,"roundtrip lost duplicate");
        Check(restored.MultiServer.Rules.Any(r=>r.ApplicationName=="Discord.exe"),"roundtrip lost application name");
        var reopened=Draft(restored);
        Check(Values(reopened,"RoutingSitesDirect").Count==13,"reopen lost direct list");
        Check(Values(reopened,"RoutingSitesPrimary").Count==74,"reopen lost primary list");
        Check(Values(reopened,"RoutingAppsSecondary").Contains("Discord.exe"),"reopen lost VPN2 names");
        Values(reopened,"RoutingSitesSecondary").Remove("direct-0.test");
        var deleted=Build(reopened);
        Check(deleted.MultiServer.Rules.Count(r=>r.Domain=="direct-0.test")==1&&deleted.MultiServer.Rules.Single(r=>r.Domain=="direct-0.test").Target=="Direct","delete changed another card");
        var invalid=Draft(next);Values(invalid,"RoutingSitesSecondary").Add("bad domain!");
        bool refused=false;try{Build(invalid);}catch(TargetInvocationException){refused=true;}
        Check(refused&&Values(invalid,"RoutingSitesSecondary").Contains("bad domain!"),"invalid draft lost or accepted");
        Check(next.MultiServer.Rules.All(r=>r.Domain!="bad domain!"),"invalid edit reached saved state");
        var initial=typeof(MultiServerRoutingWindow).GetMethod("InitialRules",BindingFlags.Static|BindingFlags.NonPublic);
        var ordinary=new RoutingSettings();ordinary.DirectDomains.Add("same.test");ordinary.ProxyDomains.Add("same.test");
        ordinary.DirectProcessPaths.Add(@"C:\Apps\same.exe");ordinary.ProxyProcessPaths.Add(@"C:\Apps\same.exe");
        var migrated=(List<MultiServerRuleRecord>)initial.Invoke(null,new object[]{ordinary});
        Check(migrated.Count==4,"enabling VPN2 discarded duplicates");
        var json=MultiServerConfigBuilder.Build(next,new[]{Profile("fr"),Profile("us")},Path.Combine(args[0],"dns.db"));
        File.WriteAllText(Path.Combine(args[0],"synthetic-config.json"),json,new UTF8Encoding(false));
        var codec=new JavaScriptSerializer();var cfg=codec.Deserialize<Dictionary<string,object>>(json);
        var routes=Items(((Dictionary<string,object>)cfg["route"])["rules"]);
        Check(routes.Any(r=>r.ContainsKey("process_name")||r.ContainsKey("process_name_regex")),"compiled process name route missing");
        var selected=routes.Where(r=>r.ContainsKey("domain")&&((IEnumerable)r["domain"]).Cast<object>().Contains("direct-0.test")&&Equals(r["action"],"route")).ToArray();
        Check(selected.Length==1&&Equals(selected[0]["outbound"],"nova-direct"),"runtime duplicates or wrong direct route");
        selected=routes.Where(r=>r.ContainsKey("domain")&&((IEnumerable)r["domain"]).Cast<object>().Contains("primary-0.test")&&Equals(r["action"],"route")).ToArray();
        Check(selected.Length==1&&Equals(selected[0]["outbound"],"nova-exit-1"),"wrong secondary runtime route");
        var plan=CompiledRoutingPlan.CreateFor(Profile("fr"),next,VpnEngine.SingBox,new[]{Profile("fr"),Profile("us")});
        Check(plan.Explain("unassigned.test",@"C:\Apps\DISCORD.exe","tcp",443).ProfileId=="us","compiled name did not override legacy primary name");
        Check(plan.Explain("direct-0.test",@"C:\Apps\DISCORD.exe","tcp",443).Route=="direct","site priority over name changed");
        var directName=RoutingOverlayService.Clone(next);directName.DirectProcesses.Add("Discord.exe");
        plan=CompiledRoutingPlan.CreateFor(Profile("fr"),directName,VpnEngine.SingBox,new[]{Profile("fr"),Profile("us")});
        Check(plan.Explain("unassigned.test",@"C:\Apps\Discord.exe","tcp",443).Route=="direct","legacy direct duplicate name priority changed");
        var portableState=new AppState {Routing=next,Profiles=new List<VpnProfile>{Profile("fr"),Profile("us")}};
        var portablePath=Path.Combine(args[0],"synthetic-settings.json");PortableConfigService.ExportSettingsOnly(portableState,portablePath);
        var portable=PortableConfigService.ImportSettingsOnly(portablePath);
        Check(portable.Routing.MultiServer.Rules.Count==next.MultiServer.Rules.Count && portable.Routing.MultiServer.Rules.Any(r=>r.ApplicationName=="Discord.exe"),"settings export lost duplicate or name");
        var duplicateBlock=next.MultiServer.Copy();duplicateBlock.Rules.Add(new MultiServerRuleRecord{Domain="direct-0.test",Target="Block"});
        Check(duplicateBlock.Compile(new[]{"fr","us"}).Decide(null,"direct-0.test").Effective.Kind==ServerRouteKind.Block,"block duplicate precedence");
        Console.WriteLine("PASS "+checks+" routing-card preservation, duplicates, names, compilation and migration checks");
    }
}

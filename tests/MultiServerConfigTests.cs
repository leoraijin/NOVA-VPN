using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using NovaVpn;
class MultiServerConfigTests
{
    static int count;
    static void Check(bool ok){if(!ok)throw new Exception("Assertion "+(count+1));count++;}
    static bool Refuses(Action action){try{action();return false;}catch(Exception){return true;}}
    static Dictionary<string,object>[] Items(object value)=>((IEnumerable)value).Cast<object>().Cast<Dictionary<string,object>>().ToArray();
    static VpnProfile Profile(string id)=>new VpnProfile{Id=id,Name="same display name",Protocol="vless",Server=id+".example.test",Port=443,UserId="11111111-1111-4111-8111-111111111111",Transport="tcp",Security="tls",Sni=id+".example.test"};
    static void Main(string[] args)
    {
        var codec=new JavaScriptSerializer();var fr=Profile("fr");var us=Profile("us");var unused=Profile("unused");
        var settings=new MultiServerSettings{Revision=1,DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{
            new MultiServerRuleRecord{ApplicationPath=@"C:\Apps\ChatGPT.exe",Target="Profile",ProfileId="us"},
            new MultiServerRuleRecord{Domain="example.com",IncludeSubdomains=true,Target="Profile",ProfileId="fr"},
            new MultiServerRuleRecord{Domain="chat.example.com",Target="Direct"},
            new MultiServerRuleRecord{Domain="gone.example.net",Target="Profile",ProfileId="gone"}}};
        var routing=new RoutingSettings{MultiServer=settings,BlockDomains=new List<string>{"blocked.example.net"},DnsProvider="Quad9"};
        var path=Path.Combine(args[0],"private-fakeip.db");
        var json=MultiServerConfigBuilder.Build(routing,new[]{us,fr,unused},path);
        var config=codec.Deserialize<Dictionary<string,object>>(json);
        var outbounds=Items(config["outbounds"]);var dns=(Dictionary<string,object>)config["dns"];
        var servers=Items(dns["servers"]);var route=(Dictionary<string,object>)config["route"];var rules=Items(route["rules"]);
        Check(outbounds.Length==3);Check(outbounds.All(o=>!o.ContainsKey("server")||!Equals(o["server"],unused.Server)));
        Check(Items(config["inbounds"]).Length==1);
        Check(outbounds.Where(o=>!Equals(o["type"],"direct")).All(o=>Equals(o["domain_resolver"],"nova-bootstrap-dns")));
        int site=Array.FindIndex(rules,r=>r.ContainsKey("domain")&&((IEnumerable)r["domain"]).Cast<object>().Contains("example.com"));
        int app=Array.FindIndex(rules,r=>r.TryGetValue("process_path_regex",out var patterns) &&
            ((IEnumerable)patterns).Cast<object>().Contains(@"(?i)\AC:\\Apps\\ChatGPT\.exe\z"));
        Check(site<app);Check(Equals(rules[site]["action"],"resolve"));Check(Equals(rules[site+1]["action"],"route"));
        string tag=(string)rules[site+1]["outbound"];
        Check(Equals(rules[site]["server"],"dns-"+tag));
        Check(servers.Any(s=>Equals(s["tag"],"dns-"+tag)&&Equals(s["detour"],tag)&&Equals(s["server"],"9.9.9.9")));
        Check(rules.Any(r=>r.ContainsKey("process_path_regex")&&Equals(r["action"],"reject")&&Equals(r["invert"],true)));
        Check(rules.Any(r=>r.ContainsKey("domain")&&((IEnumerable)r["domain"]).Cast<object>().Contains("gone.example.net")&&Equals(r["action"],"reject")));
        Check(rules.Any(r=>r.ContainsKey("domain")&&((IEnumerable)r["domain"]).Cast<object>().Contains("blocked.example.net")&&Equals(r["action"],"reject")));
        Check(rules.Any(r=>r.ContainsKey("ip_version")&&Equals(r["ip_version"],6)&&Equals(r["action"],"reject")));
        Check(Equals(((Dictionary<string,object>)((Dictionary<string,object>)config["experimental"])["cache_file"])["path"],path));
        using(var active=new ActiveCoreProbe()) {
            var checkedConfig=codec.Deserialize<Dictionary<string,object>>(active.Apply(json,"nova-exit-0"));
            Check(Equals(Items(((Dictionary<string,object>)checkedConfig["route"])["rules"]).First()["outbound"],"nova-exit-0"));
        }
        var managed=TunnelNetworkPreflight.PrepareManaged(json,new[]{new TunnelNetworkPreflight.ConnectedPrefix("occupied4","172.19.0.2/30"),new TunnelNetworkPreflight.ConnectedPrefix("occupied6","fdfe:dcba:9876::2/126")},true);
        Check(managed.Addresses.Length==2&&managed.Addresses.All(v=>v!="172.19.0.1"&&v!="fdfe:dcba:9876::1"));
        var allocated=codec.Deserialize<Dictionary<string,object>>(managed.Json);
        Check(Items(((Dictionary<string,object>)allocated["route"])["rules"]).Any(r=>r.ContainsKey("ip_version")&&Equals(r["ip_version"],6)&&Equals(r["action"],"reject")));
        var readyType=typeof(CoreManager).Assembly.GetType("NovaVpn.TunnelRoutingReadiness",true);
        using(var ready=(IDisposable)Activator.CreateInstance(readyType,new object[]{true})) {
            var readyConfig=codec.Deserialize<Dictionary<string,object>>((string)readyType.GetMethod("Apply").Invoke(ready,new object[]{managed.Json,true}));
            var readyRules=Items(((Dictionary<string,object>)readyConfig["route"])["rules"]);
            var destinations=((IEnumerable)readyRules[0]["ip_cidr"]).Cast<string>().ToArray();
            Check(destinations.SequenceEqual(new[]{"192.0.2.254/32","2001:db8::ffff/128"}));
            Check(Equals(readyRules[1]["action"],"reject"));
            Check(readyRules.Skip(2).Any(r=>r.ContainsKey("ip_version")&&Equals(r["ip_version"],6)&&Equals(r["action"],"reject")));
        }
        Check(routing.MultiServer==settings&&settings.Rules[0].ProfileId=="us");
        Check(fr.Id=="fr"&&us.Id=="us");
        Check(Refuses(()=>MultiServerConfigBuilder.Build(routing,new[]{fr,fr},path)));
        var copy=RoutingOverlayService.Clone(routing);copy.MultiServer.RequiresRebinding=true;
        Check(Refuses(()=>MultiServerConfigBuilder.Build(copy,new[]{fr,us},path)));
        copy=RoutingOverlayService.Clone(routing);copy.ProxyDomains.Add("legacy.example.org");Check(Refuses(()=>MultiServerConfigBuilder.Build(copy,new[]{fr,us},path)));
        Check(Refuses(()=>MultiServerConfigBuilder.Build(routing,new[]{fr,Profile("us").WithRaw()},path)));
        copy=RoutingOverlayService.Clone(routing);copy.MultiServer.DefaultProfileId="missing";
        var missing=codec.Deserialize<Dictionary<string,object>>(MultiServerConfigBuilder.Build(copy,new[]{us},path));
        Check(Equals(Items(((Dictionary<string,object>)missing["route"])["rules"]).Last()["action"],"reject"));
        File.WriteAllText(Path.Combine(args[0],"synthetic-multi-config.json"),json);
        var captured=MultiServerConfigBuilder.CaptureProfiles(new[]{fr,us});
        var oldHost=fr.Server;fr.Server="changed.example.test";
        Check(captured.Single(p=>p.Id=="fr").Server==oldHost);
        fr.Server=oldHost;
        var throughCore=CoreManager.BuildPreparedConfiguration(fr,routing,VpnEngine.SingBox,captured,path);
        Check(throughCore==json);
        var reality = CoreManager.BuildPreparedConfiguration(fr,routing,VpnEngine.Reality,captured,path);
        var realityConfig = codec.Deserialize<Dictionary<string,object>>(reality);
        var realityDns = (Dictionary<string,object>)realityConfig["dns"];
        Check(Equals(realityDns["bootstrap"], "nova-bootstrap-dns") &&
            Items(realityConfig["outbounds"]).Count(o => ((string)o["tag"]).StartsWith("nova-exit-", StringComparison.Ordinal)) == 2 &&
            !Items(realityConfig["outbounds"]).Any(o => o.ContainsKey("domain_resolver")));
        File.WriteAllText(Path.Combine(args[0],"reality-dual-config.json"), reality);
        Check(Refuses(()=>CoreManager.BuildPreparedConfiguration(fr,routing,VpnEngine.SingBox,null,path)));
        Check(Refuses(()=>CoreManager.BuildPreparedConfiguration(fr,routing,VpnEngine.SingBox,captured,null)));
        var legacy=new RoutingSettings();
        Check(CoreManager.BuildPreparedConfiguration(fr,legacy,VpnEngine.SingBox)==ConfigBuilder.Build(fr,legacy));
        us.Name="US fixed";fr.Name="FR fixed";
        var inspector=CompiledRoutingPlan.CreateFor(fr,routing,VpnEngine.SingBox,new[]{fr,us});
        var appDecision=inspector.Explain("unassigned.example.org",@"C:\Apps\ChatGPT.exe","tcp",443);
        Check(appDecision.Route=="proxy"&&appDecision.ProfileId=="us");
        Check(appDecision.ProfileName=="US fixed");
        Check(!appDecision.IsCertain && appDecision.Reason.Contains("IPv6"));
        Check(inspector.Explain("2606:4700:4700::1111",@"C:\Apps\ChatGPT.exe","tcp",443).Route=="block");
        var withLan = RoutingOverlayService.Clone(routing);
        withLan.BypassLan = true;
        withLan.EnableIpv6 = true;
        var lanInspector = CompiledRoutingPlan.CreateFor(fr, withLan, VpnEngine.SingBox, new[] { fr, us });
        var fakeV4 = lanInspector.Explain("198.19.0.23", @"C:\Apps\ChatGPT.exe", "tcp", 443);
        Check(fakeV4.Route == "unknown" && !fakeV4.IsCertain && fakeV4.Reason.Contains("FakeIP"));
        var fakeV6 = lanInspector.Explain("fc00::23", @"C:\Apps\Other.exe", "tcp", 443);
        Check(fakeV6.Route == "unknown" && !fakeV6.IsCertain && fakeV6.Reason.Contains("FakeIP"));
        Check(lanInspector.Explain("fd00::23", @"C:\Apps\Other.exe", "tcp", 443).Route == "direct");
        Check(lanInspector.Explain("192.168.1.23", @"C:\Apps\ChatGPT.exe", "tcp", 443).ProfileId == "us");
        Check(appDecision.DisplayRoute.Contains("US fixed") &&
            inspector.Explain("unassigned.example.org", @"c:\apps\CHATGPT.EXE", "tcp", 443).ProfileId == "us");
        var siteDecision=inspector.Explain("example.com",@"C:\Apps\ChatGPT.exe","tcp",443);
        Check(siteDecision.ProfileId=="fr");
        Check(siteDecision.OutboundTag!=appDecision.OutboundTag);
        var defaultDecision=inspector.Explain("unassigned.example.org",@"C:\Apps\Other.exe","udp",443);
        Check(defaultDecision.Route=="proxy"&&defaultDecision.ProfileId=="fr");
        Check(inspector.Explain("chat.example.com",@"C:\Apps\ChatGPT.exe","tcp",443).Route=="direct");
        Check(inspector.Explain("gone.example.net",@"C:\Apps\ChatGPT.exe","tcp",443).Route=="block");
        Check(inspector.Explain("unassigned.example.org","","tcp",443).Route=="unknown");
        us.Name="Changed later";
        Check(inspector.Explain("unassigned.example.org",@"C:\Apps\ChatGPT.exe","tcp",443).ProfileName=="US fixed");
        var projection=codec.Serialize(typeof(CompiledRoutingPlan).GetField("config",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(inspector));
        Check(!projection.Contains(us.UserId));
        Check(!projection.Contains(us.Server));
        var retiredCatalog=MultiServerConfigBuilder.CaptureProfiles(new[]{fr,us});
        retiredCatalog.Single(p=>p.Id=="us").RetiredFromSubscription=true;
        var retiredConfig=codec.Deserialize<Dictionary<string,object>>(MultiServerConfigBuilder.Build(routing,retiredCatalog,path));
        Check(!Items(retiredConfig["outbounds"]).Any(o=>o.ContainsKey("server")&&Equals(o["server"],us.Server)));
        Check(MultiServerConfigBuilder.ExitIdentities(settings,retiredCatalog).Values.All(i=>i.ProfileId!="us"));
        var retiredPlan=CompiledRoutingPlan.CreateFor(fr,routing,VpnEngine.SingBox,retiredCatalog);
        Check(retiredPlan.Explain("203.0.113.9",@"C:\Apps\ChatGPT.exe","tcp",443).Route=="block");
        Check(retiredPlan.Explain("203.0.113.9",@"C:\Apps\Other.exe","tcp",443).ProfileId=="fr");
        Check(!us.RetiredFromSubscription);
        retiredCatalog.Single(p=>p.Id=="us").RetiredFromSubscription=false;
        Check(CompiledRoutingPlan.CreateFor(fr,routing,VpnEngine.SingBox,retiredCatalog).Explain("203.0.113.9",@"C:\Apps\ChatGPT.exe","tcp",443).ProfileId=="us");
        retiredCatalog.Single(p=>p.Id=="fr").RetiredFromSubscription=true;
        var retiredDefault=CompiledRoutingPlan.CreateFor(fr,routing,VpnEngine.SingBox,retiredCatalog);
        Check(retiredDefault.Explain("203.0.113.9",@"C:\Apps\Other.exe","tcp",443).Route=="block");
        Check(retiredDefault.Explain("203.0.113.9",@"C:\Apps\ChatGPT.exe","tcp",443).ProfileId=="us");
        Console.WriteLine("PASS "+count+" Windows multi-config/preparation/inspector assertions");
    }
}
static class FixtureExtensions {public static VpnProfile WithRaw(this VpnProfile p){p.Protocol="sing-box";p.RawJson="{}";return p;}}
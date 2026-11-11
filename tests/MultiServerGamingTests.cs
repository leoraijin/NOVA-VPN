using System;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using NovaVpn;
class MultiServerGamingTests {
 static int n;
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);n++;}
 static void Refuses(Action a,string message){try{a();}catch(ArgumentException){n++;return;}catch(InvalidOperationException){n++;return;}throw new Exception(message);}
 static VpnProfile Profile(string id,int port)=>new VpnProfile{Id=id,Name=id,Protocol="socks",Server="127.0.0.1",Port=port,PortableOutboundJson="{\"type\":\"socks\",\"server\":\"127.0.0.1\",\"server_port\":"+port+",\"version\":\"5\"}"};
 static Dictionary<string,object>[] Items(object value)=>((IEnumerable)value).Cast<Dictionary<string,object>>().ToArray();
 [STAThread] static void Main(string[] args){
  var fr=Profile("fr",18001);var us=Profile("us",18002);var gameProfile=Profile("game",18003);
  var basis=new RoutingSettings{MultiServer=new MultiServerSettings{Revision=8,DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{
   new MultiServerRuleRecord{ApplicationPath=@"C:\Apps\Chat.exe",Target="Profile",ProfileId="us"},
   new MultiServerRuleRecord{ApplicationPath=@"C:\Games\Play.exe",Target="Direct"},
   new MultiServerRuleRecord{ApplicationPath=@"C:\Games\Blocked.exe",Target="Block"},
   new MultiServerRuleRecord{Domain="example.org",IncludeSubdomains=true,Target="Profile",ProfileId="us"}}}};
  var codec=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};var before=codec.Serialize(basis);
  var route=new GamingRoute{GameId="fixture",Game="Fixture",ExecutablePaths=new[]{@"C:\Games\Play.exe",@"c:\games\PLAY.exe",@"C:\Games\Blocked.exe",@"C:\Games\Child.exe"},Profile=gameProfile};
  var effective=GamingRouting.Effective(basis,route,false);var ids=new[]{"fr","us","game"};var policy=effective.MultiServer.Compile(ids);
  Check(effective.MultiServer.DefaultProfileId=="fr","default changed");Check(effective.Gaming==null,"legacy overlay survived");Check(effective.MultiServer.Revision==9,"temporary revision missing");
  Check(policy.Decide(@"C:\Games\Play.exe","example.org").Effective.ProfileId=="us","site priority lost");
  Check(policy.Decide(@"C:\Games\Play.exe","other.org").Effective.ProfileId=="game","game exit missing");
  Check(policy.Decide(@"C:\Games\Child.exe","other.org").Effective.ProfileId=="game","child exit missing");
  Check(policy.Decide(@"C:\Games\Blocked.exe","example.org").Effective.Kind==ServerRouteKind.Block,"explicit block lost");
  Check(policy.Decide(@"C:\Apps\Chat.exe","other.org").Effective.ProfileId=="us","other app changed");
  Check(policy.Decide(@"C:\Apps\Other.exe","other.org").Effective.ProfileId=="fr","other default changed");
  Check(effective.MultiServer.Rules.Count==5,"duplicate canonical paths");Check(codec.Serialize(basis)==before,"persistent baseline mutated");
  var again=GamingRouting.Effective(basis,route,false);Check(codec.Serialize(again)==codec.Serialize(effective),"reprepare inconsistent");
  Check(codec.Serialize(GamingRouting.Effective(basis,null,false))==before,"restore baseline changed");
  var absent=effective.MultiServer.Compile(new[]{"fr","us"});Check(absent.Decide(@"C:\Games\Play.exe","other.org").Effective.Kind==ServerRouteKind.Block,"missing game fell back");
  var json=MultiServerConfigBuilder.Build(effective,new[]{fr,us,gameProfile},Path.Combine(args[0],"synthetic-game-dns.db"));
  var root=codec.Deserialize<Dictionary<string,object>>(json);var exits=MultiServerConfigBuilder.ExitIdentities(effective.MultiServer,new[]{fr,us,gameProfile});
  string gameTag=exits.Single(pair=>pair.Value.ProfileId=="game").Key;
  var rules=Items(((Dictionary<string,object>)root["route"])["rules"]);var gameIndex=Array.FindIndex(rules,r=>r.TryGetValue("process_path_regex",out var paths)&&((IEnumerable)paths).Cast<object>().Contains(@"(?i)\AC:\\Games\\Play\.exe\z"));
  var siteIndex=Array.FindIndex(rules,r=>r.ContainsKey("domain"));Check(siteIndex>=0&&gameIndex>siteIndex,"compiled site priority lost");
  Check(Items(root["outbounds"]).Length==4,"exits not deduplicated");
  Check(Items(((Dictionary<string,object>)root["dns"])["servers"]).Any(d=>Equals(d["tag"],"dns-"+gameTag)&&Equals(d["detour"],gameTag)),"game DNS not pinned");
  File.WriteAllText(Path.Combine(args[0],"multi-game-profile.json"),json);
  route.Profile=null;var direct=GamingRouting.Effective(basis,route,false);Check(direct.MultiServer.Compile(ids).Decide(@"C:\Games\Play.exe","other.org").Effective.Kind==ServerRouteKind.Direct,"direct game failed");
  Check(direct.MultiServer.DefaultProfileId=="fr","direct default changed");
  File.WriteAllText(Path.Combine(args[0],"multi-game-direct.json"),MultiServerConfigBuilder.Build(direct,new[]{fr,us},Path.Combine(args[0],"synthetic-game-dns.db")));
  Refuses(()=>GamingRouting.Effective(basis,route,true),"strict direct allowed");
  route.Profile=gameProfile;route.GameOnly=true;Refuses(()=>GamingRouting.Effective(basis,route,false),"game-only silently changed default");route.GameOnly=false;
  route.ExecutablePaths=null;route.Processes=new[]{"Play.exe"};Refuses(()=>GamingRouting.Effective(basis,route,false),"ambiguous process accepted");
  route.ExecutablePath="relative.exe";Refuses(()=>GamingRouting.Effective(basis,route,false),"relative path accepted");
  route.ExecutablePath=@"C:\Games\Play.exe";gameProfile.RetiredFromSubscription=true;Refuses(()=>GamingRouting.Effective(basis,route,false),"retired game accepted");gameProfile.RetiredFromSubscription=false;
  var strict=GamingRouting.Effective(basis,route,true);Check(strict.MultiServer.DefaultProfileId=="fr","strict profile failed");
  var rebind=RoutingOverlayService.Clone(basis);rebind.MultiServer.RequiresRebinding=true;Refuses(()=>GamingRouting.Effective(rebind,route,false),"unbound policy accepted");
  Check(codec.Serialize(basis)==before,"failure mutated baseline");
  File.WriteAllText(Path.Combine(args[0],"multi-game-restored.json"),MultiServerConfigBuilder.Build(GamingRouting.Effective(basis,null,false),new[]{fr,us},Path.Combine(args[0],"synthetic-game-dns.db")));
  Console.WriteLine("PASS "+n+" multi-server gaming assertions; managed compiler only, no TUN.");
 }
}

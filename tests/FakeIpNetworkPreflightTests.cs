using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using NovaVpn;
class FakeIpNetworkPreflightTests {
 static int n;
 static void Check(bool value,string message){if(!value)throw new Exception(message);n++;}
 static string Config(string v4="198.19.0.0/16",string v6="fc00::/18")=>new JavaScriptSerializer().Serialize(new{dns=new{servers=new[]{new{type="fakeip",inet4_range=v4,inet6_range=v6}}}});
 static bool Conflict(string config,string subnet){try{TunnelNetworkPreflight.Validate(config,new[]{new TunnelNetworkPreflight.ConnectedPrefix("synthetic",subnet)});return false;}catch(IOException){return true;}catch(InvalidDataException){return true;}}
 static string V4(uint v)=>String.Join(".",new[]{v>>24,(v>>16)&255,(v>>8)&255,v&255});
 static void Main(string[] args){
  Check(!Conflict(Config(),"198.18.255.254/32"),"lab still collides");Check(Conflict(Config("198.18.0.0/15"),"198.18.255.254/32"),"old pool conflict missed");
  Check(Conflict(Config(),"198.19.255.254/32"),"contained IPv4 route missed");Check(Conflict(Config(),"198.18.0.0/15"),"containing route missed");
  Check(Conflict(Config(),"fc00:1234::1/64"),"IPv6 conflict missed");Check(!Conflict(Config(),"fd00::1/8"),"adjacent IPv6 falsely overlaps");
  Check(!Conflict(Config(),"192.168.1.1/24"),"unrelated physical network blocked");
  Check(Conflict(Config("fc00::/18"),"192.168.1.1/24"),"wrong FakeIP family accepted");
  Check(Conflict(Config(v6:"198.19.0.0/16"),"192.168.1.1/24"),"wrong v6 family accepted");
  var random=new Random(71903);
  for(int i=0;i<2000;i++){
   uint a=(uint)random.Next()*2u+(uint)random.Next(2),b=(uint)random.Next()*2u+(uint)random.Next(2);int ap=random.Next(33),bp=random.Next(33);
   ulong sizeA=1UL<<(32-ap),sizeB=1UL<<(32-bp),startA=a/sizeA*sizeA,startB=b/sizeB*sizeB;
   Check(Conflict(Config(V4(a)+"/"+ap),V4(b)+"/"+bp)==(startA<=startB+sizeB-1&&startB<=startA+sizeA-1),"interval oracle mismatch");
  }
  var fr=new VpnProfile{Id="fr",Name="FR",Protocol="socks",Server="127.0.0.1",Port=18001,PortableOutboundJson="{\"type\":\"socks\",\"server\":\"127.0.0.1\",\"server_port\":18001}"};
  var routing=new RoutingSettings{MultiServer=new MultiServerSettings{Revision=1,DefaultProfileId="fr"}};
  var json=MultiServerConfigBuilder.Build(routing,new[]{fr},Path.Combine(args[0],"synthetic-fakeip.db"));
  Check(json.Contains("198.19.0.0/16"),"generator still uses old pool");
  var lab=new[]{new TunnelNetworkPreflight.ConnectedPrefix("lab","198.18.255.254/32")};TunnelNetworkPreflight.Validate(json,lab);Check(true,"actual generated lab route validation");
  var managed=TunnelNetworkPreflight.PrepareManaged(json,lab,true);Check(managed.Json.Contains("198.19.0.0/16"),"managed preflight changed FakeIP");
  Check(Conflict(json,"198.19.7.8/32"),"generated conflict not caught");
  bool refused=false;try{TunnelNetworkPreflight.PrepareManaged(json,new[]{new TunnelNetworkPreflight.ConnectedPrefix("busy","198.19.7.8/32")},true);}catch(IOException){refused=true;}Check(refused,"managed path bypassed FakeIP guard");
  TunnelNetworkPreflight.Validate("{}",new[]{new TunnelNetworkPreflight.ConnectedPrefix("ordinary","0.0.0.0/0")});Check(true,"nonfake config altered");
  File.WriteAllText(Path.Combine(args[0],"synthetic-fakeip-config.json"),json);
  Console.WriteLine("PASS "+n+" FakeIP overlap assertions; seed71903, managed/read-only only; no host inventory or TUN.");
 }
}

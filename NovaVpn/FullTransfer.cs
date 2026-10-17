using System;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
namespace NovaVpn;
public sealed class TransferProfile {
 public string id,name,protocol,server,outboundJson,fullConfigJson,subscriptionUrl; public int port; public bool favorite;
}
public sealed class FullTransferDocument {
 public string format="nova-full-v1";
 public IdentityState identity=new IdentityState();
 public List<TransferProfile> profiles=new List<TransferProfile>();
 public string selectedId;
 public bool dark,ipv6;
 public List<string> directDomains=new List<string>(),proxyDomains=new List<string>();
 public Dictionary<string,string> platforms=new Dictionary<string,string>();
}
public static class FullTransfer {
 static JavaScriptSerializer Json()=>new JavaScriptSerializer {MaxJsonLength=IdentityTransfer.MaxBytes,RecursionLimit=64};
 public static AppState Copy(AppState state)=>Restore(Snapshot(state));
 static string Snapshot(AppState state){using(var m=new MemoryStream()){new DataContractJsonSerializer(typeof(AppState),new DataContractJsonSerializerSettings { DateTimeFormat=new System.Runtime.Serialization.DateTimeFormat("o",System.Globalization.CultureInfo.InvariantCulture) { DateTimeStyles=System.Globalization.DateTimeStyles.RoundtripKind } }).WriteObject(m,state);return Encoding.UTF8.GetString(m.ToArray());}}
 static AppState Restore(string json){using(var m=new MemoryStream(Encoding.UTF8.GetBytes(json)))return (AppState)new DataContractJsonSerializer(typeof(AppState),new DataContractJsonSerializerSettings { DateTimeFormat=new System.Runtime.Serialization.DateTimeFormat("o",System.Globalization.CultureInfo.InvariantCulture) { DateTimeStyles=System.Globalization.DateTimeStyles.RoundtripKind } }).ReadObject(m);}
 public static string Export(AppState state,string password){
  var identity=state.SubscriptionIdentities??new IdentityState();identity.Validate();
  var doc=new FullTransferDocument {identity=new IdentityState {identities=identity.identities,bindings=identity.bindings},selectedId=state.SelectedProfileId??"",dark=state.Visual?.DarkMode??false,ipv6=state.Routing?.EnableIpv6??false,directDomains=state.Routing?.DirectDomains??new List<string>(),proxyDomains=state.Routing?.ProxyDomains??new List<string>(),platforms=new Dictionary<string,string>(identity.platforms??new Dictionary<string,string>())};
  var copy=Restore(Snapshot(state));copy.SubscriptionIdentities=new IdentityState();doc.platforms["windows"]=Snapshot(copy);
  foreach(var p in state.Profiles)doc.profiles.Add(new TransferProfile {id=p.Id,name=p.Name,protocol=p.Protocol,server=p.Server,port=p.Port,outboundJson=p.Protocol=="sing-box"?null:ConfigBuilder.ExportOutbound(p),fullConfigJson=p.Protocol=="sing-box"?p.RawJson:null,subscriptionUrl=p.SubscriptionUrl,favorite=p.IsFavorite});
  return IdentityTransfer.Seal(Json().Serialize(doc),password);
 }
 public static AppState Import(string code,string password){
  var raw=Json().DeserializeObject(IdentityTransfer.Open(code,password)) as Dictionary<string,object>;
  if(raw==null||!raw.ContainsKey("format")||!Equals(raw["format"],"nova-full-v1"))throw new FormatException("Это не полный перенос NOVA.");
  if(new[]{"identity","profiles","selectedId","dark","ipv6","directDomains","proxyDomains","platforms"}.Any(k=>!raw.ContainsKey(k)))throw new FormatException("Неполный пакет.");
  var d=Json().Deserialize<FullTransferDocument>(Json().Serialize(raw));
  if(d.identity==null||d.profiles==null||d.profiles.Count>2000||d.platforms==null||d.platforms.Count>4||d.directDomains==null||d.proxyDomains==null)throw new FormatException("Неполный пакет.");
  d.identity=IdentityTransfer.ParsePayload(Json().Serialize(new Dictionary<string,object>{{"format","nova-identity-v1"},{"identity",raw["identity"]}}));
  var state=d.platforms.ContainsKey("windows")?Restore(d.platforms["windows"]):new AppState();
  var profiles=new List<VpnProfile>();var ids=new HashSet<string>();
  foreach(var p in d.profiles){if(p==null||String.IsNullOrEmpty(p.id)||!ids.Add(p.id)||String.IsNullOrWhiteSpace(p.name))throw new FormatException("Некорректные профили.");
   if(String.IsNullOrWhiteSpace(p.fullConfigJson)){var o=Json().DeserializeObject(p.outboundJson??"") as Dictionary<string,object>;if(o==null||!o.ContainsKey("type"))throw new FormatException("Некорректная конфигурация сервера.");}
   else if(!(Json().DeserializeObject(p.fullConfigJson) is Dictionary<string,object>))throw new FormatException("Некорректная полная конфигурация.");
   profiles.Add(new VpnProfile {Id=p.id,Name=p.name,Protocol=String.IsNullOrWhiteSpace(p.fullConfigJson)?p.protocol:"sing-box",Server=p.server,Port=p.port,PortableOutboundJson=p.outboundJson,RawJson=p.fullConfigJson,SubscriptionUrl=p.subscriptionUrl,IsFavorite=p.favorite || state.Profiles.Any(old=>old.Id==p.id&&old.IsFavorite)});
  }
  state.Profiles=profiles;state.SelectedProfileId=profiles.Any(p=>p.Id==d.selectedId)?d.selectedId:profiles.FirstOrDefault()?.Id;
  state.SubscriptionIdentities=d.identity;state.SubscriptionIdentities.platforms=d.platforms;
  state.Routing=state.Routing??new RoutingSettings();state.Routing.DirectDomains=d.directDomains;state.Routing.ProxyDomains=d.proxyDomains;state.Routing.EnableIpv6=d.ipv6;
  state.Visual=state.Visual??new VisualPreferences();state.Visual.DarkMode=d.dark;state.Visual.MigrateWindowsDesign();
  // Restoring a file never starts a tunnel automatically.
  state.AutoConnect=false;state.StartConnectedWithWindows=false;state.StartWithWindows=false;state.PauseUntilUtc=default(DateTime);
  return state;
 }
}


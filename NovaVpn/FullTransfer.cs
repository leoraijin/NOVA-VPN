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
 public bool? retiredFromSubscription;
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
 const string XhttpMarker = "{\"type\":\"nova-xhttp-required\"}";
 static JavaScriptSerializer Json()=>new JavaScriptSerializer {MaxJsonLength=IdentityTransfer.MaxBytes,RecursionLimit=64};
 public static AppState Copy(AppState state)=>Restore(Snapshot(state));
 static string Snapshot(AppState state){using(var m=new MemoryStream()){new DataContractJsonSerializer(typeof(AppState),new DataContractJsonSerializerSettings { DateTimeFormat=new System.Runtime.Serialization.DateTimeFormat("o",System.Globalization.CultureInfo.InvariantCulture) { DateTimeStyles=System.Globalization.DateTimeStyles.RoundtripKind } }).WriteObject(m,state);return Encoding.UTF8.GetString(m.ToArray());}}
 static AppState Restore(string json){using(var m=new MemoryStream(Encoding.UTF8.GetBytes(json)))return (AppState)new DataContractJsonSerializer(typeof(AppState),new DataContractJsonSerializerSettings { DateTimeFormat=new System.Runtime.Serialization.DateTimeFormat("o",System.Globalization.CultureInfo.InvariantCulture) { DateTimeStyles=System.Globalization.DateTimeStyles.RoundtripKind } }).ReadObject(m);}
 public static string Export(AppState state,string password){
  ApplicationBandwidthPolicySnapshot.Freeze(state);state.MultiServerRoutingHistory?.Validate();
  var identity=state.SubscriptionIdentities??new IdentityState();identity.Validate();
  var doc=new FullTransferDocument {identity=new IdentityState {identities=identity.identities,bindings=identity.bindings},selectedId=state.SelectedProfileId??"",dark=state.Visual?.DarkMode??false,ipv6=state.Routing?.EnableIpv6??false,directDomains=state.Routing?.DirectDomains??new List<string>(),proxyDomains=state.Routing?.ProxyDomains??new List<string>(),platforms=new Dictionary<string,string>(identity.platforms??new Dictionary<string,string>())};
  var copy=Restore(Snapshot(state));copy.SubscriptionIdentities=new IdentityState();doc.platforms["windows"]=Snapshot(copy);
  foreach(var p in state.Profiles)doc.profiles.Add(new TransferProfile {id=p.Id,name=p.Name,protocol=p.Protocol,server=p.Server,port=p.Port,outboundJson=p.Protocol=="sing-box"?null:ConfigBuilder.RequiresXrayTransport(p)?XhttpMarker:ConfigBuilder.ExportOutbound(p),fullConfigJson=p.Protocol=="sing-box"?p.RawJson:null,subscriptionUrl=p.SubscriptionUrl,favorite=p.IsFavorite,retiredFromSubscription=p.RetiredFromSubscription});
  return IdentityTransfer.Seal(Json().Serialize(doc),password);
 }
 // Older Android copies record availability as subscription/manual ownership.
 static HashSet<string> LegacyAndroidRetirements(Dictionary<string,string> platforms)
 {
  var unavailable=new HashSet<string>(StringComparer.Ordinal);
  if(!platforms.TryGetValue("android",out string nativeText))return unavailable;
  Dictionary<string,object> TryReadObject(string text)
  {
   if(string.IsNullOrWhiteSpace(text))return null;
   try {return Json().DeserializeObject(text) as Dictionary<string,object>;}
   catch(ArgumentException){return null;}
   catch(InvalidOperationException){return null;}
  }
  var native=TryReadObject(nativeText);
  if(native==null||!native.TryGetValue("profileDocument",out object encoded)||!(encoded is string documentText))return unavailable;
  var document=TryReadObject(documentText);
  if(document==null||!document.TryGetValue("schema",out object schema)||!Equals(schema,2))return unavailable;
  object[] Rows(string key)
  {
   if(!document.TryGetValue(key,out object value)||!(value is object[] rows))
    throw new FormatException("Некорректный каталог Android в копии переноса.");
   return rows;
  }
  string Id(object row,string key)
  {
   if(!(row is Dictionary<string,object> item)||!item.TryGetValue(key,out object id)||
      !(id is string text)||string.IsNullOrWhiteSpace(text))
    throw new FormatException("Некорректный каталог Android в копии переноса.");
   return text;
  }
  var profiles=Rows("profiles");
  var subscriptions=Rows("subscriptions");
  var ids=new HashSet<string>(profiles.Select(row=>Id(row,"id")),StringComparer.Ordinal);
  var sources=new HashSet<string>(subscriptions.Select(row=>Id(row,"id")),StringComparer.Ordinal);
  if(ids.Count!=profiles.Length||sources.Count!=subscriptions.Length)
   throw new FormatException("Повторяющиеся ID в каталоге Android.");
  var owned=new HashSet<string>(StringComparer.Ordinal);
  foreach(var row in Rows("manuallyOwned"))
  {
   if(!(row is string id)||!ids.Contains(id))throw new FormatException("Некорректное владение профилем Android.");
   owned.Add(id);
  }
  foreach(var row in Rows("memberships"))
  {
   string id=Id(row,"profileId");
   if(!ids.Contains(id)||!sources.Contains(Id(row,"subscriptionId")))
    throw new FormatException("Некорректное владение профилем Android.");
   owned.Add(id);
  }
  ids.ExceptWith(owned);
  return ids;
 }

 public static AppState Import(string code,string password){
  var raw=Json().DeserializeObject(IdentityTransfer.Open(code,password)) as Dictionary<string,object>;
  if(raw==null||!raw.ContainsKey("format")||!Equals(raw["format"],"nova-full-v1"))throw new FormatException("Это не полный перенос NOVA.");
  if(new[]{"identity","profiles","selectedId","dark","ipv6","directDomains","proxyDomains","platforms"}.Any(k=>!raw.ContainsKey(k)))throw new FormatException("Неполный пакет.");
  if (!(raw["profiles"] is System.Collections.IEnumerable profilesJson)) throw new FormatException("Некорректные профили.");
  foreach (var entry in profilesJson)
   if (!(entry is Dictionary<string,object> profileJson) ||
       profileJson.TryGetValue("retiredFromSubscription",out var retired) && retired != null && !(retired is bool))
    throw new FormatException("Некорректная доступность профиля.");
  var d=Json().Deserialize<FullTransferDocument>(Json().Serialize(raw));
  if(d.identity==null||d.profiles==null||d.profiles.Count>2000||d.platforms==null||d.platforms.Count>4||d.directDomains==null||d.proxyDomains==null)throw new FormatException("Неполный пакет.");
  d.identity=IdentityTransfer.ParsePayload(Json().Serialize(new Dictionary<string,object>{{"format","nova-identity-v1"},{"identity",raw["identity"]}}));
  var state=d.platforms.ContainsKey("windows")?Restore(d.platforms["windows"]):new AppState();
  var savedProfiles=state.Profiles.ToDictionary(profile=>profile.Id,StringComparer.Ordinal);
  var foreignRetired=d.profiles.Any(p=>p!=null&&p.id!=null&&!p.retiredFromSubscription.HasValue&&!savedProfiles.ContainsKey(p.id))
   ?LegacyAndroidRetirements(d.platforms):new HashSet<string>(StringComparer.Ordinal);
  var profiles=new List<VpnProfile>();var ids=new HashSet<string>();
  foreach(var p in d.profiles){if(p==null||String.IsNullOrEmpty(p.id)||!ids.Add(p.id)||String.IsNullOrWhiteSpace(p.name))throw new FormatException("Некорректные профили.");
   if(String.IsNullOrWhiteSpace(p.fullConfigJson)){var o=Json().DeserializeObject(p.outboundJson??"") as Dictionary<string,object>;if(o==null||!o.ContainsKey("type"))throw new FormatException("Некорректная конфигурация сервера.");}
   else if(!(Json().DeserializeObject(p.fullConfigJson) is Dictionary<string,object>))throw new FormatException("Некорректная полная конфигурация.");
   savedProfiles.TryGetValue(p.id,out var saved);
   if (p.outboundJson == XhttpMarker) {
    if (!ConfigBuilder.RequiresXrayTransport(saved) || !String.IsNullOrWhiteSpace(p.fullConfigJson))
     throw new FormatException("В пакете отсутствует исходный профиль XHTTP для Windows.");
    saved.Name=p.name;saved.IsFavorite=p.favorite||saved.IsFavorite;
    saved.RetiredFromSubscription=p.retiredFromSubscription??saved.RetiredFromSubscription;
    profiles.Add(saved);
    continue;
   }
   var portable=p.outboundJson;
   if(String.IsNullOrWhiteSpace(p.fullConfigJson)&&Equals((Json().DeserializeObject(p.outboundJson) as Dictionary<string,object>)?["type"],CorporateProxy.RequiredOutboundType)) {
    if(!CorporateProxy.Enabled(saved))throw new FormatException("Для этого профиля нужен Windows-пакет с настройками корпоративного прокси.");
    portable=ConfigBuilder.ExportBaseOutbound(saved);
    if(Equals((Json().DeserializeObject(portable) as Dictionary<string,object>)?["type"],CorporateProxy.RequiredOutboundType))throw new FormatException("В пакете отсутствуют обязательные исходные параметры корпоративной цепочки.");
   }
   profiles.Add(new VpnProfile {Id=p.id,Name=p.name,Protocol=String.IsNullOrWhiteSpace(p.fullConfigJson)?p.protocol:"sing-box",Server=p.server,Port=p.port,PortableOutboundJson=portable,RawJson=p.fullConfigJson,SubscriptionUrl=p.subscriptionUrl,IsFavorite=p.favorite || state.Profiles.Any(old=>old.Id==p.id&&old.IsFavorite),CorporateProxy=saved?.CorporateProxy?.Copy(),
    ProviderName=saved?.ProviderName??p.name,UserName=saved!=null&&saved.Name==p.name?saved.UserName:p.name,
    SubscriptionId=saved?.SubscriptionId??"",Source=saved?.Source,FallbackProfileIds=saved?.FallbackProfileIds??new List<string>(),DeclaredCountryCode=saved?.DeclaredCountryCode??"",
    RetiredFromSubscription=p.retiredFromSubscription??saved?.RetiredFromSubscription??foreignRetired.Contains(p.id),
    Transport=saved?.Transport,Security=saved?.Security,Sni=saved?.Sni,Host=saved?.Host,Path=saved?.Path});
  }
  state.Profiles=profiles;state.SelectedProfileId=profiles.Any(p=>p.Id==d.selectedId)?d.selectedId:profiles.FirstOrDefault()?.Id;
  state.SubscriptionIdentities=d.identity;state.SubscriptionIdentities.platforms=d.platforms;
  state.Routing=state.Routing??new RoutingSettings();state.Routing.DirectDomains=d.directDomains;state.Routing.ProxyDomains=d.proxyDomains;state.Routing.EnableIpv6=d.ipv6;
  state.Visual=state.Visual??new VisualPreferences();state.Visual.DarkMode=d.dark;state.Visual.MigrateWindowsDesign();
  // Restoring a file never starts a tunnel automatically.
  state.ApplicationLimitsEnabled=false; // Retain exact Windows SID/path rules for review, never rebind or activate them on import.
  state.AutoConnect=false;state.StartConnectedWithWindows=false;state.StartWithWindows=false;state.PauseUntilUtc=default(DateTime);
  state.MultiServerRoutingHistory?.Validate();return state;
 }
}


using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed partial class ActiveCoreProbe
{
    ReadOnlyCollection<ExitProbeTarget> multiTargets;
    Dictionary<string,LocalProxyEndpoint> multiChannels;
    public bool HasMultiExits => multiTargets!=null;
    public IReadOnlyList<ExitProbeTarget> MultiTargets => multiTargets;
    static Dictionary<string,object> Obj(params object[] pairs) {
        var value=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)value[(string)pairs[i]]=pairs[i+1];return value;
    }
    static Dictionary<string,object>[] Objects(object value) => ((IEnumerable)value).Cast<object>().Cast<Dictionary<string,object>>().ToArray();
    static object Canonical(object value) {
        if(value is Dictionary<string,object> dictionary)return new SortedDictionary<string,object>(dictionary.ToDictionary(p=>p.Key,p=>Canonical(p.Value)),StringComparer.Ordinal);
        if(value is IEnumerable sequence&&!(value is string))return sequence.Cast<object>().Select(Canonical).ToArray();
        return value;
    }
    public string ApplyMulti(string json,MultiServerSettings settings,IEnumerable<VpnProfile> profiles, bool separateListeners = false)
    {
        lock(sync) {
            if(disposed)throw new ObjectDisposedException(nameof(ActiveCoreProbe));
            if(HasMultiExits)throw new InvalidOperationException("План проверки уже подготовлен.");
            var catalog=MultiServerConfigBuilder.CaptureProfiles(profiles);
            if(catalog.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Id))||catalog.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=catalog.Length)
                throw new InvalidDataException("Некорректный снимок серверов.");
            var policy=settings.Copy();var ids=policy.ReferencedProfileIds;
            if(ids.Length>2000)throw new InvalidDataException("Слишком много выходов для проверки.");
            var identities=MultiServerConfigBuilder.ExitIdentities(policy,catalog);
            var codec=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
            var root=codec.Deserialize<Dictionary<string,object>>(json);
            var inbounds=Objects(root["inbounds"]);var outbounds=Objects(root["outbounds"]);
            var route=(Dictionary<string,object>)root["route"];var oldRules=Objects(route["rules"]);
            var resolvers=Objects(((Dictionary<string,object>)root["dns"])["servers"]);
            if(inbounds.Any(i=>i.ContainsKey("tag")&&Equals(i["tag"],Tag)))throw new InvalidDataException("Занят тег проверки.");
            var tags=outbounds.Select(o=>Convert.ToString(o["tag"])).ToArray();
            if(tags.Distinct(StringComparer.Ordinal).Count()!=tags.Length||!new HashSet<string>(tags.Where(t=>t.StartsWith("nova-exit-",StringComparison.Ordinal)),StringComparer.Ordinal).SetEquals(identities.Keys))
                throw new InvalidDataException("Выходы проверки не соответствуют снимку серверов.");
            var dnsTags=resolvers.Select(r=>Convert.ToString(r["tag"])).ToArray();
            if(dnsTags.Distinct(StringComparer.Ordinal).Count()!=dnsTags.Length)throw new InvalidDataException("Повторяющиеся DNS-выходы.");
            var used=new HashSet<int>(inbounds.Where(i=>i.ContainsKey("listen_port")).Select(i=>Convert.ToInt32(i["listen_port"])));
            if(root.TryGetValue("experimental",out object exp)&&exp is Dictionary<string,object> experimental&&
                experimental.TryGetValue("clash_api",out object cv)&&cv is Dictionary<string,object> clash&&clash.TryGetValue("external_controller",out object controller)&&
                Uri.TryCreate("http://"+Convert.ToString(controller),UriKind.Absolute,out Uri uri))used.Add(uri.Port);
            for(int attempt=0;used.Contains(endpoint.Port)&&attempt<16;attempt++)endpoint.Port=FreePort();
            if(used.Contains(endpoint.Port))throw new IOException("Не найден свободный порт проверки.");
            var targets=new List<ExitProbeTarget>();var channels=new Dictionary<string,LocalProxyEndpoint>(StringComparer.Ordinal);
            var listeners = new List<object>();
            var users=new List<object>();var rules=new List<object>();var usernames=new HashSet<string>(StringComparer.Ordinal);
            foreach(string id in ids.OrderBy(i=>i,StringComparer.Ordinal)) {
                var profile=catalog.SingleOrDefault(p=>p.Id==id);
                string tag=identities.FirstOrDefault(pair=>pair.Value.ProfileId==id).Key;
                if(tag==null) {targets.Add(new ExitProbeTarget(id,profile?.Name??"Недоступный сервер",null,ExitProbePhase.Unavailable));continue;}
                var outbound=outbounds.Single(o=>Equals(o["tag"],tag));
                var expected=codec.Deserialize<Dictionary<string,object>>(ConfigBuilder.ExportBaseOutbound(profile));
                expected["tag"]=tag;expected["domain_resolver"]="nova-bootstrap-dns";
                if(codec.Serialize(Canonical(expected))!=codec.Serialize(Canonical(outbound)))throw new InvalidDataException("Конфиг выхода не соответствует снимку профиля.");
                string fingerprint=ExitProbeTarget.Fingerprint(profile);
                if(!new[]{"vless","vmess","trojan","shadowsocks","hysteria2","tuic","socks","http","wireguard","ssh"}.Contains(Convert.ToString(outbound["type"]))||outbound.ContainsKey("detour")) {
                    targets.Add(new ExitProbeTarget(id,profile.Name,fingerprint,ExitProbePhase.Unsupported));continue;
                }
                string dnsTag="dns-"+tag;var resolver=resolvers.SingleOrDefault(r=>Equals(r["tag"],dnsTag));
                if(resolver==null||!Equals(resolver["type"],"https")||!resolver.TryGetValue("detour",out object detour)||!Equals(detour,tag)||
                    !resolver.TryGetValue("tls",out object tv)||!(tv is Dictionary<string,object> tls)||!tls.TryGetValue("enabled",out object enabled)||!Equals(enabled,true)||
                    tls.TryGetValue("insecure",out object insecure)&&!Equals(insecure,false))throw new InvalidDataException("DNS проверки не привязан к защищённому выходу.");
                var channel=new LocalProxyEndpoint{ProfileId=id,Port=endpoint.Port,InboundType="http"};
                for(int attempt=0;usernames.Contains(channel.Username)&&attempt<16;attempt++)channel.Username="nova-"+Guid.NewGuid().ToString("N");
                if(!usernames.Add(channel.Username))throw new IOException("Не создан уникальный канал проверки.");
                channels.Add(id,channel);
                var credential = Obj("username", channel.Username, "password", channel.Password);
                if (separateListeners)
                {
                    string probeTag = Tag + "-" + listeners.Count;
                    if (inbounds.Any(input => input.TryGetValue("tag", out object value) && Equals(value, probeTag)))
                        throw new InvalidDataException("Занят тег канала проверки.");
                    for (int attempt = 0; used.Contains(channel.Port) && attempt < 16; attempt++) channel.Port = FreePort();
                    if (!used.Add(channel.Port)) throw new IOException("Не найден отдельный порт проверки.");
                    listeners.Add(Obj("type", "http", "tag", probeTag, "listen", "127.0.0.1",
                        "listen_port", channel.Port, "users", new[] {credential}));
                    rules.Add(Obj("inbound", new[] {probeTag}, "action", "resolve",
                        "server", dnsTag, "strategy", "ipv4_only", "disable_cache", true));
                    rules.Add(Obj("inbound", new[] {probeTag}, "action", "route", "outbound", tag));
                }
                else
                {
                    users.Add(credential);
                    rules.Add(Obj("inbound",new[]{Tag},"auth_user",new[]{channel.Username},"action","resolve","server",dnsTag,"strategy","ipv4_only","disable_cache",true));
                    rules.Add(Obj("inbound",new[]{Tag},"auth_user",new[]{channel.Username},"action","route","outbound",tag));
                }
                targets.Add(new ExitProbeTarget(id,profile.Name,fingerprint,ExitProbePhase.Queued));
            }
            if (separateListeners)
            {
                if (listeners.Count > 0)
                    rules.Add(Obj("inbound", listeners.Cast<Dictionary<string,object>>()
                        .Select(listener => (string)listener["tag"]).ToArray(), "action", "reject"));
            }
            else rules.Add(Obj("inbound",new[]{Tag},"action","reject"));
            if (users.Count > 0) listeners.Add(Obj("type","http","tag",Tag,"listen","127.0.0.1","listen_port",endpoint.Port,"users",users.ToArray()));
            if (listeners.Count > 0) root["inbounds"] = inbounds.Cast<object>().Concat(listeners).ToArray();
            // Even with no available channels no ordinary route can certify an unavailable exit.
            route["rules"]=rules.Concat(oldRules.Cast<object>()).ToArray();
            string result=codec.Serialize(root);
            multiChannels=channels;multiTargets=targets.AsReadOnly();return result;
        }
    }
    public async Task<int> ReadExitStatusAsync(string profileId,Uri target,CancellationToken cancellation)
    {
        new ServiceCheckTarget("Проверка выхода",target?.AbsoluteUri);
        CancellationTokenSource deadline;LocalProxyEndpoint channel;
        lock(sync) {
            if(disposed)throw new ObjectDisposedException(nameof(ActiveCoreProbe));
            if(!HasMultiExits||!multiChannels.TryGetValue(profileId,out channel))throw new NotSupportedException("Для этого выхода нет канала проверки.");
            deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(8));
        }
        using(deadline)return await ServiceMatrixRunner.ReadHttpsStatusAsync(channel,target,deadline.Token).ConfigureAwait(false);
    }
}

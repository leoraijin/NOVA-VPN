using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Authenticated loopback HTTP CONNECT inside the active owned core, not a second core.
public sealed partial class ActiveCoreProbe : IDisposable
{
    const string Tag = "nova-active-check";
    readonly LocalProxyEndpoint endpoint;
    readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    readonly object sync = new object();
    bool disposed;
    public ActiveCoreProbe()
    {
        endpoint = new LocalProxyEndpoint {Port=FreePort(),InboundType="http"};
    }
    static int FreePort()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try{return((IPEndPoint)listener.LocalEndpoint).Port;}finally{listener.Stop();}
    }
    public string Apply(string json) => Apply(json,"proxy");
    public string Apply(string json,string outboundTag)
    {
        if(string.IsNullOrWhiteSpace(outboundTag))throw new ArgumentException("Нет выхода для проверки.",nameof(outboundTag));
        lock(sync) {
            if(disposed)throw new ObjectDisposedException(nameof(ActiveCoreProbe));
            if(HasMultiExits)throw new InvalidOperationException("План проверки уже подготовлен.");
            var serializer=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
            var root=serializer.Deserialize<Dictionary<string,object>>(json);
            object outs,ins,routes;
            if(root==null || !root.TryGetValue("outbounds",out outs) || !root.TryGetValue("inbounds",out ins) || !root.TryGetValue("route",out routes))
                throw new InvalidDataException("Нет управляемого выхода для проверки активного ядра.");
            var outbounds=((IEnumerable)outs).Cast<object>().OfType<Dictionary<string,object>>().ToArray();
            var matches=outbounds.Where(o=>o.ContainsKey("tag") && Equals(o["tag"],outboundTag)).ToArray();
            if(matches.Length!=1 || !matches[0].ContainsKey("type") || !new[]{"vless","vmess","trojan","shadowsocks","hysteria2","tuic","socks","http","wireguard","ssh"}.Contains(Convert.ToString(matches[0]["type"])))
                throw new NotSupportedException("Выход профиля нельзя достоверно проверить через активное ядро.");
            var inbounds=((IEnumerable)ins).Cast<object>().ToArray();
            if(inbounds.OfType<Dictionary<string,object>>().Any(i=>i.ContainsKey("tag") && Equals(i["tag"],Tag)))throw new InvalidDataException("Занят тег проверки активного ядра.");
            var route=routes as Dictionary<string,object>;
            object rules;
            if(route==null || !route.TryGetValue("rules",out rules))throw new InvalidDataException("Нет управляемых правил проверки активного ядра.");
            var used=new HashSet<int>();
            foreach(var inbound in inbounds.OfType<Dictionary<string,object>>()) {
                object port;int value;
                if(inbound.TryGetValue("listen_port",out port) && Int32.TryParse(Convert.ToString(port),out value)) used.Add(value);
            }
            object experimentalValue,clashValue,controllerValue;
            if(root.TryGetValue("experimental",out experimentalValue) && experimentalValue is Dictionary<string,object> experimental &&
                experimental.TryGetValue("clash_api",out clashValue) && clashValue is Dictionary<string,object> clash &&
                clash.TryGetValue("external_controller",out controllerValue) && Uri.TryCreate("http://"+Convert.ToString(controllerValue),UriKind.Absolute,out Uri controller)) used.Add(controller.Port);
            for(int attempt=0;used.Contains(endpoint.Port) && attempt<16;attempt++)endpoint.Port=FreePort();
            if(used.Contains(endpoint.Port))throw new IOException("Не найден свободный порт проверки активного ядра.");
            root["inbounds"]=inbounds.Concat(new object[]{new Dictionary<string,object>{
                {"type","http"},{"tag",Tag},{"listen","127.0.0.1"},{"listen_port",endpoint.Port},
                {"users",new object[]{new Dictionary<string,object>{{"username",endpoint.Username},{"password",endpoint.Password}}}}
            }}).ToArray();
            route["rules"]=new object[]{new Dictionary<string,object>{{"inbound",new[]{Tag}},{"action","route"},{"outbound",outboundTag}}}
                .Concat(((IEnumerable)rules).Cast<object>()).ToArray();
            return serializer.Serialize(root);
        }
    }
    public async Task<int> ReadStatusAsync(Uri target,CancellationToken cancellation)
    {
        // Reuse the strict manual CONNECT/TLS client: no Windows proxy bypass.
        new ServiceCheckTarget("Проверка активного ядра",target?.AbsoluteUri);
        CancellationTokenSource deadline;
        lock(sync) {
            if(disposed)throw new ObjectDisposedException(nameof(ActiveCoreProbe));
            if(HasMultiExits)throw new InvalidOperationException("Выберите конкретный выход для проверки.");
            deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
        }
        using(deadline) return await ServiceMatrixRunner.ReadHttpsStatusAsync(endpoint,target,deadline.Token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock(sync) {
            if(disposed)return;
            disposed=true; lifetime.Cancel(); lifetime.Dispose();
        }
    }
}

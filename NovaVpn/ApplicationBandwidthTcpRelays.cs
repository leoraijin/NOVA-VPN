using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class BandwidthTcpRelayResult
{
    public ApplicationBandwidthIdentity PeerIdentity { get; internal set; }
    public long UploadBytes { get; internal set; }
    public long DownloadBytes { get; internal set; }
}

// Internal transport building block, not a configured proxy or TUN capture host.
// A trusted caller either supplies an established upstream or approves a fixed
// loopback core endpoint before connect. No DNS, retry, direct fallback or
// inferred original application behind a VPN/proxy core occurs.
public sealed class ApplicationBandwidthTcpRelays : IDisposable
{
    sealed class Run
    {
        public Socket Application,Upstream;
        public IPEndPoint ConnectEndpoint;
        public Func<ApplicationBandwidthIdentity,IPEndPoint,bool> Approve;
        public Func<Socket,CancellationToken,Task> PrepareCore;
        public TaskCompletionSource<BandwidthTcpRelayResult> Completion=new TaskCompletionSource<BandwidthTcpRelayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    readonly object sync=new object();
    readonly HashSet<Run> runs=new HashSet<Run>();
    readonly ApplicationBandwidthController budgets;
    readonly Func<Socket,LocalTcpPeerObservation> observe;
    readonly int capacity;
    readonly TimeSpan halfCloseIdle;
    readonly CancellationTokenSource stop=new CancellationTokenSource();
    readonly CancellationToken stopToken;
    bool stopping;
    public int ActiveCount{get{lock(sync)return runs.Count;}}
    // No implicit timeout changes ordinary TCP semantics; callers may opt in.
    public ApplicationBandwidthTcpRelays(ApplicationBandwidthController budgets,int capacity=128,TimeSpan? halfCloseIdle=null)
        :this(budgets,capacity,halfCloseIdle??Timeout.InfiniteTimeSpan,LocalTcpPeerObservation.TryObserve){}
    internal ApplicationBandwidthTcpRelays(ApplicationBandwidthController budgets,int capacity,TimeSpan halfCloseIdle,Func<Socket,LocalTcpPeerObservation> observe)
    {
        if(budgets==null||observe==null)throw new ArgumentNullException("bandwidth relay");
        if(capacity<1||capacity>1024||(halfCloseIdle!=Timeout.InfiniteTimeSpan&&(halfCloseIdle<=TimeSpan.Zero||halfCloseIdle>TimeSpan.FromMinutes(5))))throw new ArgumentOutOfRangeException("bandwidth relay");
        this.budgets=budgets;this.capacity=capacity;this.halfCloseIdle=halfCloseIdle;this.observe=observe;stopToken=stop.Token;
    }
    // Ownership transfers only after validation and capacity reservation succeed.
    // Caller retains rejected sockets. Admitted sockets close after worker drain.
    public Task<BandwidthTcpRelayResult> RelayAsync(Socket application,Socket upstream,CancellationToken token)
    {
        Validate(application);Validate(upstream);
        if(ReferenceEquals(application,upstream)||Same(application.LocalEndPoint,upstream.RemoteEndPoint)&&Same(application.RemoteEndPoint,upstream.LocalEndPoint))throw new ArgumentException("Нельзя соединить сокет с ним самим.");
        return Admit(new Run{Application=application,Upstream=upstream},token);
    }
    // The caller must authorize the native observation and frozen core endpoint.
    // It still owns routing policy, core attestation and proxy authentication.
    public Task<BandwidthTcpRelayResult> ConnectAndRelayAsync(Socket application,IPEndPoint coreEndpoint,Func<ApplicationBandwidthIdentity,IPEndPoint,bool> approve,Func<Socket,CancellationToken,Task> prepareCore,CancellationToken token)
    {
        Validate(application);if(approve==null||prepareCore==null)throw new ArgumentNullException("core admission");
        if(coreEndpoint==null||coreEndpoint.Port<=0)throw new ArgumentException("Нужен локальный адрес VPN-ядра.");
        var endpoint=CopyEndpoint(coreEndpoint);
        if(!IPAddress.IsLoopback(endpoint.Address)||Same(endpoint,application.LocalEndPoint))throw new ArgumentException("Соединение разрешено только с отдельным локальным VPN-ядром.");
        return Admit(new Run{Application=application,ConnectEndpoint=endpoint,Approve=approve,PrepareCore=prepareCore},token);
    }
    Task<BandwidthTcpRelayResult> Admit(Run run,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock(sync){if(stopping)throw new ObjectDisposedException(nameof(ApplicationBandwidthTcpRelays));if(runs.Count>=capacity)throw new InvalidOperationException("Все слоты передачи заняты.");runs.Add(run);}
        _=ExecuteAsync(run,token);return run.Completion.Task;
    }
    static IPEndPoint CopyEndpoint(IPEndPoint endpoint)
    {
        var address=endpoint.Address.IsIPv4MappedToIPv6?endpoint.Address.MapToIPv4():endpoint.Address;
        address=address.AddressFamily==AddressFamily.InterNetworkV6?new IPAddress(address.GetAddressBytes(),address.ScopeId):new IPAddress(address.GetAddressBytes());
        return new IPEndPoint(address,endpoint.Port);
    }
    static bool Matches(LocalTcpPeerObservation owner,Socket application)=>owner!=null&&owner.IsCurrent()&&Same(owner.LocalEndpoint,application.LocalEndPoint)&&Same(owner.PeerEndpoint,application.RemoteEndPoint);
    static bool Same(EndPoint a,EndPoint b){var x=a as IPEndPoint;var y=b as IPEndPoint;if(x==null||y==null)return false;var xa=x.Address.IsIPv4MappedToIPv6?x.Address.MapToIPv4():x.Address;var ya=y.Address.IsIPv4MappedToIPv6?y.Address.MapToIPv4():y.Address;return x.Port==y.Port&&xa.Equals(ya);}
    static void Validate(Socket value){if(value==null||value.SocketType!=SocketType.Stream||value.ProtocolType!=ProtocolType.Tcp||!value.Connected||value.LocalEndPoint==null||value.RemoteEndPoint==null)throw new ArgumentException("Нужен установленный TCP-сокет.");}
    async Task ExecuteAsync(Run run,CancellationToken token)
    {
        BandwidthTcpRelayResult result=null;Exception error=null;bool cancelled=false;
        try{result=await RelayCoreAsync(run,token).ConfigureAwait(false);}
        catch(OperationCanceledException){cancelled=true;}
        catch(Exception failure){if(token.IsCancellationRequested||stopToken.IsCancellationRequested)cancelled=true;else error=failure;}
        finally{
            Close(run,result==null);
            lock(sync){runs.Remove(run);if(cancelled)run.Completion.TrySetCanceled();else if(error!=null)run.Completion.TrySetException(error);else run.Completion.TrySetResult(result);}
        }
    }
    async Task<BandwidthTcpRelayResult> RelayCoreAsync(Run run,CancellationToken token)
    {
        using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stopToken))
        using(var closing=linked.Token.Register(()=>Close(run,true)))
        using(var owner=observe(run.Application)){
            linked.Token.ThrowIfCancellationRequested();
            if(!Matches(owner,run.Application))throw new InvalidOperationException("Владелец локального TCP-соединения не подтверждён.");
            if(run.ConnectEndpoint!=null){
                if(!run.Approve(owner.Identity,CopyEndpoint(run.ConnectEndpoint)))throw new UnauthorizedAccessException("Соединение не разрешено политикой владельца.");
                linked.Token.ThrowIfCancellationRequested();
                if(!Matches(owner,run.Application))throw new InvalidOperationException("Владелец TCP-соединения изменился до подключения.");
            }
            var budget=budgets.Get(owner.Identity);
            if(run.ConnectEndpoint!=null){
                linked.Token.ThrowIfCancellationRequested();
                var socket=new Socket(run.ConnectEndpoint.AddressFamily,SocketType.Stream,ProtocolType.Tcp);Volatile.Write(ref run.Upstream,socket);
                linked.Token.ThrowIfCancellationRequested();
                socket.Bind(new IPEndPoint(run.ConnectEndpoint.AddressFamily==AddressFamily.InterNetwork?IPAddress.Loopback:IPAddress.IPv6Loopback,0));
                await Task.Factory.FromAsync((callback,state)=>socket.BeginConnect(CopyEndpoint(run.ConnectEndpoint),callback,state),socket.EndConnect,null).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if(!Matches(owner,run.Application))throw new InvalidOperationException("Владелец TCP-соединения изменился до подготовки ядра.");
                // Trusted attestation/authentication callback owns no new socket.
                // It must drain on cancellation; its slot is retained until it does.
                await run.PrepareCore(socket,linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if(!Matches(owner,run.Application))throw new InvalidOperationException("Владелец TCP-соединения изменился во время подключения.");
                if(!Same(socket.RemoteEndPoint,run.ConnectEndpoint))throw new IOException("Адрес ядра изменился во время подготовки.");
            }
            // Prevent caller-supplied blocking linger from holding global slots.
            run.Application.LingerState=new LingerOption(false,0);run.Upstream.LingerState=new LingerOption(false,0);
            using(var incoming=new NetworkStream(run.Application,false))using(var outgoing=new NetworkStream(run.Upstream,false)){
                var watch=new HalfCloseWatch(linked,halfCloseIdle);Task<long> upload=null,download=null;bool complete=false;
                try{
                    upload=Pump(budget,BandwidthDirection.Upload,incoming,outgoing,run.Upstream,watch,linked.Token);
                    download=Pump(budget,BandwidthDirection.Download,outgoing,incoming,run.Application,watch,linked.Token);
                    var first=await Task.WhenAny(upload,download).ConfigureAwait(false);await first.ConfigureAwait(false);
                    await Task.WhenAll(upload,download).ConfigureAwait(false);linked.Token.ThrowIfCancellationRequested();watch.Finish();complete=true;
                    return new BandwidthTcpRelayResult{PeerIdentity=owner.Identity,UploadBytes=upload.Result,DownloadBytes=download.Result};
                }catch(Exception){
                    if(token.IsCancellationRequested||stopToken.IsCancellationRequested)throw new OperationCanceledException(linked.Token);
                    if(linked.IsCancellationRequested)throw new TimeoutException("Второе направление TCP-соединения не завершилось после простоя.");
                    throw;
                }finally{
                    if(!complete){linked.Cancel();Close(run,true);}
                    if(upload!=null&&download!=null)try{await Task.WhenAll(upload,download).ConfigureAwait(false);}catch{}
                    watch.Finish();
                }
            }
        }
    }
    sealed class HalfCloseWatch
    {
        readonly object sync=new object();readonly CancellationTokenSource cancel;readonly TimeSpan idle;int ended,pacing;bool finished;
        public HalfCloseWatch(CancellationTokenSource cancel,TimeSpan idle){this.cancel=cancel;this.idle=idle;}
        void Update(){if(!finished)cancel.CancelAfter(ended==1&&pacing==0?idle:Timeout.InfiniteTimeSpan);}
        public void Pacing(bool begin){lock(sync){pacing+=begin?1:-1;Update();}}
        public void Progress(){lock(sync)Update();}
        public void End(){lock(sync){ended++;Update();}}
        public void Finish(){lock(sync){finished=true;cancel.CancelAfter(Timeout.Infinite);}}
    }
    static async Task<long> Pump(ApplicationBandwidthBudget budget,BandwidthDirection direction,Stream source,Stream target,Socket destination,HalfCloseWatch watch,CancellationToken token)
    {
        byte[] buffer=new byte[16384];long total=0;
        while(true){
            int count=await source.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false);if(count==0){destination.Shutdown(SocketShutdown.Send);watch.End();return total;}
            int at=0;while(at<count){
                int amount;watch.Pacing(true);try{amount=await budget.AcquireStreamAsync(direction,count-at,token).ConfigureAwait(false);}finally{watch.Pacing(false);}
                await target.WriteAsync(buffer,at,amount,token).ConfigureAwait(false);total=checked(total+amount);at+=amount;watch.Progress();
            }
        }
    }
    static void Close(Run run,bool abort)
    {
        foreach(var socket in new[]{run.Application,Volatile.Read(ref run.Upstream)})try{if(socket==null)continue;if(abort)socket.Close(0);else{socket.LingerState=new LingerOption(false,0);socket.Close();}}catch{}
    }
    public async Task StopAsync()
    {
        Task[] pending;lock(sync){stopping=true;pending=runs.Select(r=>(Task)r.Completion.Task).ToArray();}
        stop.Cancel();try{await Task.WhenAll(pending).ConfigureAwait(false);}catch{}
    }
    // StopAsync is the confirmation that every admitted pair has drained.
    public void Dispose(){lock(sync)stopping=true;stop.Cancel();}
}

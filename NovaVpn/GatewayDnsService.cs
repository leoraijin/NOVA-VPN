using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
namespace NovaVpn;

// One-shot paired bind and drain; not a system gateway activation transaction.
public sealed class GatewayDnsService
{
    readonly GatewayDnsTcpServer tcp;
    readonly GatewayDnsUdpServer udp;
    readonly object sync=new object();
    readonly GatewayDnsTcpClient upstream;readonly CancellationTokenSource watching=new CancellationTokenSource();
    readonly CancellationToken ownerToken;CancellationTokenRegistration ownerRegistration;
    GatewayCoreLifetime coreLifetime;Task coreWatch,stopping;
    bool CoreAlive=>coreLifetime!=null&&coreLifetime.IsAlive;
    bool attempted,ready;int admission,ended;
    readonly TaskCompletionSource<bool> completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion=>completion.Task;
    public bool IsListening{get{lock(sync)return ready&&CoreAlive&&Volatile.Read(ref ended)==0&&tcp.IsListening&&udp.IsListening;}}
    public GatewayDnsService(GatewayDnsTcpClient upstream,IPAddress address,int port,IEnumerable<IPAddress> clients,CancellationToken ownerToken=default(CancellationToken))
    {
        this.upstream=upstream;this.ownerToken=ownerToken;
        if(clients==null)throw new ArgumentNullException(nameof(clients));
        if(port<1||port>65535)throw new ArgumentOutOfRangeException(nameof(port));
        var frozen=clients.Take(65).Select(ip=>ip==null?null:new IPAddress(ip.GetAddressBytes())).ToArray();
        var bind=IPAddress.Parse(GatewayDnsTcpServer.Address(address));
        tcp=new GatewayDnsTcpServer(upstream,bind,port,frozen,admission:()=>Volatile.Read(ref admission)==1&&Volatile.Read(ref ended)==0&&CoreAlive);
        udp=new GatewayDnsUdpServer(upstream,bind,port,frozen,admission:()=>Volatile.Read(ref admission)==1&&Volatile.Read(ref ended)==0&&CoreAlive);
        tcp.Stopped+=OnEnded;udp.Stopped+=OnEnded;
    }
    void OnEnded(){Interlocked.Exchange(ref ended,1);Volatile.Write(ref admission,0);_=StopAsync();}
    public async Task StartAsync()
    {
        Exception failure=null;
        lock(sync){if(attempted||stopping!=null)throw new InvalidOperationException("Экземпляр DNS-службы уже использован.");attempted=true;
            try{ownerToken.ThrowIfCancellationRequested();coreLifetime=upstream.OpenLifetime();tcp.Start();udp.Start();ownerRegistration=ownerToken.Register(OnEnded);ownerToken.ThrowIfCancellationRequested();ready=CoreAlive&&Volatile.Read(ref ended)==0&&tcp.IsListening&&udp.IsListening;if(!ready)throw new InvalidOperationException("Не удалось подтвердить оба DNS-входа.");Volatile.Write(ref admission,1);coreWatch=WatchCore();}
            catch(Exception error){ready=false;failure=error;}
        }
        if(failure!=null){try{await StopAsync().ConfigureAwait(false);}catch(Exception cleanup){throw new AggregateException("Не удалось запустить и полностью закрыть DNS-входы.",failure,cleanup);}System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();}
    }
    static Task Stop(Func<Task> stop){try{return stop();}catch(Exception error){return Task.FromException(error);}}
    // Registration disposal may wait for an owner callback needing sync; drain outside that lock.
    public Task StopAsync()
    {
        lock(sync){ready=false;Volatile.Write(ref admission,0);if(stopping!=null)return stopping;
            stopping=completion.Task;watching.Cancel();_=Task.Run(()=>Drain(completion));return stopping;}
    }
    async Task WatchCore(){try{while(!watching.IsCancellationRequested){if(!CoreAlive){OnEnded();return;}await Task.Delay(100,watching.Token).ConfigureAwait(false);}}catch(OperationCanceledException){}catch(Exception){OnEnded();}}
    async Task Drain(TaskCompletionSource<bool> completion)
    {
        Exception failure=null;
        try{await Task.WhenAll(Stop(tcp.StopAsync),Stop(udp.StopAsync),coreWatch??Task.CompletedTask).ConfigureAwait(false);}
        catch(Exception error){failure=error;}
        finally{ownerRegistration.Dispose();coreLifetime?.Dispose();watching.Dispose();}
        if(failure==null)completion.TrySetResult(true);else completion.TrySetException(failure);
    }
}

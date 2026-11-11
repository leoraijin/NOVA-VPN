using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Explicit SOCKS-aware applications only. This does not recover original apps
// behind TUN/core connections or authorize a WFP flow. Caller owns core/profile
// configuration and must share one budget registry across its capture hosts.
public sealed class ApplicationBandwidthSocksListener : IDisposable
{
    sealed class Session { public Socket Socket;public TaskCompletionSource<bool> Done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
    readonly object sync=new object();
    readonly TcpListener listener;
    readonly Socket listening;
    readonly ApplicationBandwidthTcpRelays relays;
    readonly HashSet<Session> sessions=new HashSet<Session>();
    readonly CancellationTokenSource stop=new CancellationTokenSource();
    readonly IPEndPoint core;
    readonly string sid;
    readonly byte[] username,password;
    readonly Func<ApplicationBandwidthIdentity,string,int,bool> approve;
    readonly Func<string,int,ApplicationBandwidthSocksCore> prepare;
    readonly int capacity;
    readonly TimeSpan handshakeTimeout;
    readonly Task accepting;
    bool stopping;
    int completed,failed;
    public int Port{get;}
    public int ActiveCount{get{lock(sync)return sessions.Count;}}
    public bool IsRunning{get{lock(sync)return !stopping;}}
    public int CompletedCount=>Volatile.Read(ref completed);
    public int FailedCount=>Volatile.Read(ref failed);
    public ApplicationBandwidthSocksListener(ApplicationBandwidthController budgets,int port,IPEndPoint coreEndpoint,string ownerSid,string username,string password,Func<ApplicationBandwidthIdentity,string,int,bool> approve,Func<string,int,ApplicationBandwidthSocksCore> prepare,int capacity=128,TimeSpan? handshakeTimeout=null)
    {
        if(budgets==null||approve==null||prepare==null)throw new ArgumentNullException("SOCKS listener");
        if(coreEndpoint==null||!IPAddress.IsLoopback(coreEndpoint.Address)||coreEndpoint.Port<1||port<0||port>65535||capacity<1||capacity>1024)throw new ArgumentException("Некорректные параметры локального входа.");
        sid=new SecurityIdentifier(ownerSid).Value;
        var utf8=new UTF8Encoding(false,true);this.username=Credential(utf8,username);this.password=Credential(utf8,password);
        this.handshakeTimeout=handshakeTimeout??TimeSpan.FromSeconds(10);if(this.handshakeTimeout<=TimeSpan.Zero||this.handshakeTimeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        core=new IPEndPoint(coreEndpoint.Address.AddressFamily==AddressFamily.InterNetworkV6?new IPAddress(coreEndpoint.Address.GetAddressBytes(),coreEndpoint.Address.ScopeId):new IPAddress(coreEndpoint.Address.GetAddressBytes()),coreEndpoint.Port);
        this.approve=approve;this.prepare=prepare;this.capacity=capacity;
        relays=new ApplicationBandwidthTcpRelays(budgets,capacity);
        listener=new TcpListener(IPAddress.Loopback,port);listener.ExclusiveAddressUse=true;
        try{listener.Start(capacity);listening=listener.Server;listening.Blocking=false;Port=((IPEndPoint)listener.LocalEndpoint).Port;if(core.Port==Port&&core.Address.Equals(IPAddress.Loopback))throw new ArgumentException("Вход не может подключаться к самому себе.");accepting=Task.Factory.StartNew(AcceptLoop,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}catch{listener.Stop();relays.Dispose();throw;}
    }
    static byte[] Credential(Encoding encoding,string value){if(String.IsNullOrEmpty(value))throw new ArgumentException("Нужны логин и пароль входа.");var bytes=encoding.GetBytes(value);if(bytes.Length>255)throw new ArgumentException("Учётные данные слишком длинные.");return bytes;}
    // Poll has a bounded native wait; Accept is nonblocking. Neither an APM
    // completion nor a blocked native accept is required to confirm shutdown.
    void AcceptLoop()
    {
        try{while(true){
            lock(sync)if(stopping)return;
            if(!listening.Poll(250000,SelectMode.SelectRead))continue;
            lock(sync)if(stopping)return;
            Socket socket;try{socket=listening.Accept();}catch(SocketException error)when(error.SocketErrorCode==SocketError.WouldBlock){continue;}
            try{socket.Blocking=true;}catch{Abort(socket);throw;}Session run=null;lock(sync){if(!stopping&&sessions.Count<capacity){run=new Session{Socket=socket};sessions.Add(run);}}if(run==null){Abort(socket);continue;}_=RunAsync(run);}}
        catch(Exception){RequestStop();}
    }
    async Task RunAsync(Session run)
    {
        try{
            using(var token=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            using(var close=token.Token.Register(()=>Abort(run.Socket)))
            using(var owner=LocalTcpPeerObservation.TryObserve(run.Socket))
            using(var stream=new NetworkStream(run.Socket,false)){
                token.CancelAfter(handshakeTimeout);CheckOwner(owner);
                var greeting=await Read(stream,2,token.Token).ConfigureAwait(false);if(greeting[0]!=5||greeting[1]==0)throw new IOException("Некорректное приветствие SOCKS5.");
                var methods=await Read(stream,greeting[1],token.Token).ConfigureAwait(false);bool passwordMethod=methods.Contains((byte)2);
                await Write(stream,new byte[]{5,(byte)(passwordMethod?2:255)},token.Token).ConfigureAwait(false);if(!passwordMethod)throw new UnauthorizedAccessException("Нужна авторизация SOCKS5.");
                var auth=await Read(stream,2,token.Token).ConfigureAwait(false);if(auth[0]!=1||auth[1]==0)throw new UnauthorizedAccessException("Некорректная авторизация входа.");
                byte[] user=await Read(stream,auth[1],token.Token).ConfigureAwait(false);int size=(await Read(stream,1,token.Token).ConfigureAwait(false))[0];byte[] secret=await Read(stream,size,token.Token).ConfigureAwait(false);
                bool valid;try{valid=Equal(user,username)&Equal(secret,password);}finally{Array.Clear(secret,0,secret.Length);}
                CheckOwner(owner);await Write(stream,new byte[]{1,(byte)(valid?0:1)},token.Token).ConfigureAwait(false);if(!valid)throw new UnauthorizedAccessException("Авторизация входа отклонена.");
                var header=await Read(stream,4,token.Token).ConfigureAwait(false);if(header[0]!=5||header[1]!=1||header[2]!=0)throw new IOException("Поддерживается только SOCKS5 CONNECT.");
                string destination;
                if(header[3]==1||header[3]==4)destination=new IPAddress(await Read(stream,header[3]==1?4:16,token.Token).ConfigureAwait(false)).ToString();
                else if(header[3]==3){int length=(await Read(stream,1,token.Token).ConfigureAwait(false))[0];var name=await Read(stream,length,token.Token).ConfigureAwait(false);if(length==0||name.Any(b=>b<33||b>126))throw new IOException("Некорректное имя SOCKS5.");destination=Encoding.ASCII.GetString(name);if(Uri.CheckHostName(destination)==UriHostNameType.Unknown)throw new IOException("Некорректное имя назначения.");}
                else throw new IOException("Неизвестный адрес SOCKS5.");
                var bytes=await Read(stream,2,token.Token).ConfigureAwait(false);int port=(bytes[0]<<8)|bytes[1];if(port==0)throw new IOException("Некорректный порт SOCKS5.");
                CheckOwner(owner);token.Token.ThrowIfCancellationRequested();if(!approve(owner.Identity,destination,port))throw new UnauthorizedAccessException("Назначение не разрешено политикой приложения.");
                CheckOwner(owner);token.Token.ThrowIfCancellationRequested();var preparation=prepare(destination,port);if(preparation==null)throw new InvalidOperationException("Нет подготовки выбранного ядра.");
                await relays.ConnectAndRelayAsync(run.Socket,core,(identity,endpoint)=>{CheckOwner(owner);return identity.UserSid==owner.Identity.UserSid&&String.Equals(identity.ExecutablePath,owner.Identity.ExecutablePath,StringComparison.OrdinalIgnoreCase);},async(socket,cancel)=>{
                    await preparation.PrepareAsync(socket,cancel).ConfigureAwait(false);CheckOwner(owner);cancel.ThrowIfCancellationRequested();
                    await Write(stream,new byte[]{5,0,0,1,0,0,0,0,0,0},cancel).ConfigureAwait(false);token.CancelAfter(Timeout.Infinite);
                },token.Token).ConfigureAwait(false);
                Interlocked.Increment(ref completed);
            }
        }catch(OperationCanceledException){}catch(Exception){Interlocked.Increment(ref failed);}
        finally{Abort(run.Socket);lock(sync){sessions.Remove(run);run.Done.TrySetResult(true);}}
    }
    void CheckOwner(LocalTcpPeerObservation owner){if(owner==null||owner.Identity.UserSid!=sid||!owner.IsCurrent())throw new UnauthorizedAccessException("Владелец локального входа не подтверждён.");}
    static bool Equal(byte[] a,byte[] b){int difference=a.Length^b.Length;for(int i=0;i<Math.Max(a.Length,b.Length);i++)difference|=(i<a.Length?a[i]:0)^(i<b.Length?b[i]:0);return difference==0;}
    static async Task<byte[]> Read(NetworkStream stream,int size,CancellationToken token){var bytes=new byte[size];int at=0;while(at<size){int count=await stream.ReadAsync(bytes,at,size-at,token).ConfigureAwait(false);if(count==0)throw new EndOfStreamException();at+=count;}return bytes;}
    static Task Write(NetworkStream stream,byte[] bytes,CancellationToken token)=>stream.WriteAsync(bytes,0,bytes.Length,token);
    static void Abort(Socket socket){try{socket.Close(0);}catch{}}
    void RequestStop(){lock(sync){if(stopping)return;stopping=true;}listener.Stop();stop.Cancel();}
    public async Task StopAsync(){RequestStop();await accepting.ConfigureAwait(false);await relays.StopAsync().ConfigureAwait(false);Task[] pending;lock(sync)pending=sessions.Select(s=>(Task)s.Done.Task).ToArray();await Task.WhenAll(pending).ConfigureAwait(false);}
    public void Dispose(){RequestStop();relays.Dispose();}
}

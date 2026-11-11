using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
namespace NovaVpn;

// Standalone IPv4 UDP DNS frontend; source IP is not device authentication.
// All upstream DNS is TCP through the borrowed pinned core, never direct UDP.
public sealed class GatewayDnsUdpServer
{
    sealed class Bucket{public double Tokens,Last;}
    internal event Action Stopped;
    readonly Func<bool> admit;readonly object sync=new object();readonly GatewayDnsTcpClient upstream;readonly string bind;readonly int port,maximum,burst;readonly double rate;
    readonly Dictionary<string,Bucket> clients;readonly HashSet<Task> pending=new HashSet<Task>();readonly CancellationTokenSource lifetime=new CancellationTokenSource();
    UdpClient socket;IPEndPoint endpoint;Task receiving,stopped;bool started,stopping;long completed,rejected,failed;
    public int ActiveRequests{get{lock(sync)return pending.Count;}}public long CompletedQueries=>Interlocked.Read(ref completed);public long RejectedDatagrams=>Interlocked.Read(ref rejected);public long FailedQueries=>Interlocked.Read(ref failed);
    public bool IsListening{get{lock(sync)return started&&!stopping&&receiving!=null&&!receiving.IsCompleted;}}
    public IPEndPoint Endpoint{get{lock(sync){if(endpoint==null)throw new InvalidOperationException("DNS UDP не запущен.");return new IPEndPoint(new IPAddress(endpoint.Address.GetAddressBytes()),endpoint.Port);}}}
    public GatewayDnsUdpServer(GatewayDnsTcpClient upstream,IPAddress address,int port,IEnumerable<IPAddress> allowedClients,int maximumRequests=32,double requestsPerSecond=32,int burst=64,Func<bool> admission=null)
    {
        if(upstream==null||allowedClients==null)throw new ArgumentNullException("DNS UDP policy");bind=GatewayDnsTcpServer.Address(address);
        if(port<0||port>65535||maximumRequests<1||maximumRequests>128||double.IsNaN(requestsPerSecond)||requestsPerSecond<0.1||requestsPerSecond>256||burst<1||burst>256)throw new ArgumentOutOfRangeException("DNS UDP limits");
        var allowed=allowedClients.Take(65).Select(GatewayDnsTcpServer.Address).ToArray();if(allowed.Length==0||allowed.Length>64||allowed.Distinct().Count()!=allowed.Length)throw new ArgumentException("Нужно от1 до64 различных адресов устройств.");
        admit=admission??(()=>true);clients=allowed.ToDictionary(a=>a,a=>new Bucket{Tokens=burst,Last=Now()});this.upstream=upstream;this.port=port;maximum=maximumRequests;rate=requestsPerSecond;this.burst=burst;
    }
    static double Now()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    public void Start(){lock(sync){if(started||stopping)throw new InvalidOperationException("Экземпляр DNS UDP уже использован.");var created=new UdpClient(AddressFamily.InterNetwork);try{created.Client.ExclusiveAddressUse=true;created.Client.IOControl(unchecked((int)0x9800000C),new byte[4],null);created.Client.Bind(new IPEndPoint(IPAddress.Parse(bind),port));}catch{created.Close();throw;}socket=created;endpoint=(IPEndPoint)socket.Client.LocalEndPoint;started=true;receiving=Receive();}}
    async Task Receive()
    {
        try{while(true){var packet=await socket.ReceiveAsync().ConfigureAwait(false);TaskCompletionSource<bool> completion;
            lock(sync){Bucket bucket;bool denied=stopping||!admit()||packet.Buffer.Length<12||packet.Buffer.Length>4096||!clients.TryGetValue(packet.RemoteEndPoint.Address.ToString(),out bucket)||pending.Count>=maximum;
                if(!denied){bucket=clients[packet.RemoteEndPoint.Address.ToString()];double now=Now();bucket.Tokens=Math.Min(burst,bucket.Tokens+Math.Max(0,now-bucket.Last)*rate);bucket.Last=now;if(bucket.Tokens<1)denied=true;else bucket.Tokens--;}
                if(denied){Interlocked.Increment(ref rejected);continue;}completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);pending.Add(completion.Task);
            }
            _=Serve(packet.Buffer,packet.RemoteEndPoint,completion);
        }}catch(Exception){lock(sync){if(!stopping)Interlocked.Increment(ref failed);}}
        finally{lock(sync){stopping=true;socket?.Close();lifetime.Cancel();}Stopped?.Invoke();}
    }
    async Task Serve(byte[] query,IPEndPoint peer,TaskCompletionSource<bool> completion)
    {
        try{
            if(!admit())throw new InvalidOperationException("DNS-вход ещё не разрешён.");GatewayDnsTcpClient.UdpPayloadLimit(query);var answer=await upstream.QueryAsync(query,lifetime.Token).ConfigureAwait(false);var reply=GatewayDnsTcpClient.UdpAnswer(query,answer);lifetime.Token.ThrowIfCancellationRequested();
            await socket.SendAsync(reply,reply.Length,peer).ConfigureAwait(false);Interlocked.Increment(ref completed);
        }catch(Exception){Interlocked.Increment(ref failed);}
        finally{lock(sync){pending.Remove(completion.Task);completion.TrySetResult(true);}}
    }
    public Task StopAsync(){lock(sync){if(stopped!=null)return stopped;stopping=true;socket?.Close();lifetime.Cancel();return stopped=Drain();}}
    async Task Drain(){if(receiving!=null)await receiving.ConfigureAwait(false);Task[] tasks;lock(sync)tasks=pending.ToArray();await Task.WhenAll(tasks).ConfigureAwait(false);lifetime.Dispose();}
}

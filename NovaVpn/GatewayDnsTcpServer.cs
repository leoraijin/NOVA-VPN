using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
namespace NovaVpn;

// Owned DNS TCP listener only. IP allowlisting is not device authentication.
// No adapter/firewall/DNS change or gateway enablement is performed here.
public sealed class GatewayDnsTcpServer
{
    internal event Action Stopped;
    readonly object sync=new object();
    readonly GatewayDnsTcpClient upstream;
    readonly Func<bool> admit;
    readonly TcpListener listener;
    readonly HashSet<string> allowed;
    readonly HashSet<Task> sessions=new HashSet<Task>();
    readonly CancellationTokenSource lifetime=new CancellationTokenSource();
    readonly int maximum;
    readonly TimeSpan idle;
    Task accepting,stopped;
    IPEndPoint endpoint;
    bool started,stopping;
    long completed,rejected;
    string lastFailure;
    public int ActiveClients{get{lock(sync)return sessions.Count;}}
    public long CompletedQueries=>Interlocked.Read(ref completed);
    public long RejectedConnections=>Interlocked.Read(ref rejected);
    public string LastFailureType{get{lock(sync)return lastFailure;}}
    public bool IsListening{get{lock(sync)return started&&!stopping&&accepting!=null&&!accepting.IsCompleted;}}
    public IPEndPoint Endpoint{get{lock(sync){if(endpoint==null)throw new InvalidOperationException("DNS-сервер не запущен.");return new IPEndPoint(new IPAddress(endpoint.Address.GetAddressBytes()),endpoint.Port);}}}
    internal static string Address(IPAddress ip)
    {
        if(ip==null||ip.AddressFamily!=AddressFamily.InterNetwork)throw new ArgumentException("В этом DNS-входе пока поддерживается только IPv4.");
        byte[] b=ip.GetAddressBytes();if(!(b[0]==127||b[0]==10||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168))throw new ArgumentException("Нужен точный частный IPv4-адрес или loopback.");
        return new IPAddress(b).ToString();
    }
    public GatewayDnsTcpServer(GatewayDnsTcpClient upstream,IPAddress listenAddress,int port,IEnumerable<IPAddress> allowedClients,int maximumClients=16,TimeSpan? idleTimeout=null,Func<bool> admission=null)
    {
        if(upstream==null||allowedClients==null)throw new ArgumentNullException("DNS server policy");
        if(port<0||port>65535||maximumClients<1||maximumClients>128)throw new ArgumentOutOfRangeException("DNS server capacity");
        string bind=Address(listenAddress);var clients=allowedClients.Take(65).ToArray();if(clients.Length==0||clients.Length>64)throw new ArgumentException("Нужно от1 до64 точных адресов устройств.");
        allowed=new HashSet<string>(clients.Select(Address),StringComparer.Ordinal);if(allowed.Count!=clients.Length)throw new ArgumentException("Повторяющиеся адреса устройств.");
        idle=idleTimeout??TimeSpan.FromSeconds(5);if(idle<=TimeSpan.Zero||idle>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        this.upstream=upstream;admit=admission??(()=>true);maximum=maximumClients;listener=new TcpListener(IPAddress.Parse(bind),port){ExclusiveAddressUse=true};
    }
    public void Start()
    {
        lock(sync){if(started||stopping)throw new InvalidOperationException("Этот экземпляр DNS-входа уже использован.");listener.Start(maximum);started=true;endpoint=(IPEndPoint)listener.LocalEndpoint;accepting=Accept();}
    }
    async Task Accept()
    {
        try{while(true){TcpClient client=null;
            try{
                client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);TaskCompletionSource<bool> completion;
                lock(sync){if(stopping||!admit()||!allowed.Contains(((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString())||sessions.Count>=maximum){Interlocked.Increment(ref rejected);continue;}completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);sessions.Add(completion.Task);}
                _=Serve(client,completion);client=null;
            }finally{client?.Close();}
        }}catch(Exception error){lock(sync){if(!stopping)lastFailure=error.GetType().FullName;}}
        finally{lock(sync){stopping=true;listener.Stop();lifetime.Cancel();}Stopped?.Invoke();}
    }
    async Task Serve(TcpClient client,TaskCompletionSource<bool> completion)
    {
        try{
            using(client)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))using(var abort=deadline.Token.Register(()=>{try{client.Close();}catch(SocketException){}catch(ObjectDisposedException){}})){
                client.ReceiveBufferSize=8192;client.SendBufferSize=8192;var stream=client.GetStream();
                for(int i=0;i<128;i++){
                    deadline.CancelAfter(idle);var prefix=await Read(stream,2,deadline.Token,true).ConfigureAwait(false);if(prefix==null)break;
                    int size=prefix[0]*256+prefix[1];if(size<12)throw new InvalidDataException("Неполный запрос DNS TCP.");
                    var query=await Read(stream,size,deadline.Token,false).ConfigureAwait(false);deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    if(!admit())throw new InvalidOperationException("DNS-вход ещё не разрешён.");var answer=await upstream.QueryAsync(query,lifetime.Token).ConfigureAwait(false);deadline.Token.ThrowIfCancellationRequested();
                    var frame=new byte[answer.Length+2];frame[0]=(byte)(answer.Length>>8);frame[1]=(byte)answer.Length;Buffer.BlockCopy(answer,0,frame,2,answer.Length);
                    deadline.CancelAfter(idle);await stream.WriteAsync(frame,0,frame.Length,deadline.Token).ConfigureAwait(false);Interlocked.Increment(ref completed);
                }
            }
        }catch(Exception error){lock(sync)lastFailure=error.GetType().FullName;}
        finally{client.Close();lock(sync){sessions.Remove(completion.Task);completion.TrySetResult(true);}}
    }
    static async Task<byte[]> Read(NetworkStream stream,int count,CancellationToken token,bool allowEof)
    {
        var bytes=new byte[count];int offset=0;while(offset<count){int got=await stream.ReadAsync(bytes,offset,count-offset,token).ConfigureAwait(false);if(got==0){if(allowEof&&offset==0)return null;throw new EndOfStreamException("Неполный запрос DNS TCP.");}offset+=got;}return bytes;
    }
    public Task StopAsync()
    {
        lock(sync){if(stopped!=null)return stopped;stopping=true;listener.Stop();lifetime.Cancel();return stopped=Drain();}
    }
    async Task Drain(){var accept=accepting;if(accept!=null)await accept.ConfigureAwait(false);Task[] remaining;lock(sync)remaining=sessions.ToArray();await Task.WhenAll(remaining).ConfigureAwait(false);listener.Server?.Close();lifetime.Dispose();}
}

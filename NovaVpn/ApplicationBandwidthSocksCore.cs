using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Preparation for an already approved local core, not original-flow attestation
// or route selection. The caller must own/pin the core process and configuration.
public sealed class ApplicationBandwidthSocksCore
{
    readonly int pid;
    readonly long created;
    readonly ApplicationBandwidthIdentity identity;
    readonly byte[] username,password,request;
    readonly TimeSpan timeout;
    public ApplicationBandwidthSocksCore(int processId,long processCreationFileTime,ApplicationBandwidthIdentity coreIdentity,string username,string password,string destination,int port,TimeSpan? timeout=null)
    {
        if(processId<=0||processCreationFileTime<=0||coreIdentity==null)throw new ArgumentException("Нужен ожидаемый процесс ядра.");
        pid=processId;created=processCreationFileTime;identity=coreIdentity;
        var utf8=new UTF8Encoding(false,true);
        this.username=Credential(utf8,username);this.password=Credential(utf8,password);
        this.timeout=timeout??TimeSpan.FromSeconds(10);
        if(this.timeout<=TimeSpan.Zero||this.timeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(timeout));
        if(port<1||port>65535||String.IsNullOrWhiteSpace(destination)||destination!=destination.Trim())throw new ArgumentException("Некорректный адрес назначения.");
        byte[] address;byte kind;
        if(IPAddress.TryParse(destination,out var ip)){
            if(ip.AddressFamily==AddressFamily.InterNetworkV6&&ip.ScopeId!=0)throw new ArgumentException("SOCKS не передаёт область IPv6.");
            if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();address=ip.GetAddressBytes();kind=(byte)(address.Length==4?1:4);
        }else{
            string name=new IdnMapping{UseStd3AsciiRules=true}.GetAscii(destination);
            if(Uri.CheckHostName(name)!=UriHostNameType.Dns)throw new ArgumentException("Некорректное имя назначения.");
            byte[] bytes=Encoding.ASCII.GetBytes(name);if(bytes.Length<1||bytes.Length>253)throw new ArgumentException("Слишком длинное имя назначения.");
            address=new byte[bytes.Length+1];address[0]=(byte)bytes.Length;Buffer.BlockCopy(bytes,0,address,1,bytes.Length);kind=3;
        }
        request=new byte[address.Length+6];request[0]=5;request[1]=1;request[3]=kind;
        Buffer.BlockCopy(address,0,request,4,address.Length);request[request.Length-2]=(byte)(port>>8);request[request.Length-1]=(byte)port;
    }
    static byte[] Credential(Encoding encoding,string value){if(String.IsNullOrEmpty(value))throw new ArgumentException("Нужны логин и пароль локального ядра.");var bytes=encoding.GetBytes(value);if(bytes.Length>255)throw new ArgumentException("Слишком длинные учётные данные локального ядра.");return bytes;}
    void Verify(LocalTcpPeerObservation peer){if(peer==null||!peer.IsCurrent()||peer.ProcessId!=pid||peer.ProcessCreationFileTime!=created||peer.Identity.UserSid!=identity.UserSid||!String.Equals(peer.Identity.ExecutablePath,identity.ExecutablePath,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Соединение не принадлежит ожидаемому процессу ядра.");}
    public async Task PrepareAsync(Socket socket,CancellationToken token)
    {
        if(socket==null||socket.SocketType!=SocketType.Stream||socket.ProtocolType!=ProtocolType.Tcp||!socket.Connected)throw new ArgumentException("Нужен установленный TCP-сокет ядра.");
        token.ThrowIfCancellationRequested();
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
        using(var close=deadline.Token.Register(()=>{try{socket.Close(0);}catch(ObjectDisposedException){}catch(SocketException){}}))
        using(var peer=LocalTcpPeerObservation.TryObserve(socket)){
            deadline.CancelAfter(timeout);
            try{
                deadline.Token.ThrowIfCancellationRequested();Verify(peer);
                using(var stream=new NetworkStream(socket,false)){
                    await stream.WriteAsync(new byte[]{5,1,2},0,3,deadline.Token).ConfigureAwait(false);
                    var method=await Read(stream,2,deadline.Token).ConfigureAwait(false);
                    if(method[0]!=5||method[1]!=2)throw new UnauthorizedAccessException("Ядро не подтвердило обязательную авторизацию SOCKS5.");
                    Verify(peer);
                    byte[] auth=new byte[3+username.Length+password.Length];auth[0]=1;auth[1]=(byte)username.Length;Buffer.BlockCopy(username,0,auth,2,username.Length);auth[2+username.Length]=(byte)password.Length;Buffer.BlockCopy(password,0,auth,3+username.Length,password.Length);
                    try{deadline.Token.ThrowIfCancellationRequested();Verify(peer);await stream.WriteAsync(auth,0,auth.Length,deadline.Token).ConfigureAwait(false);}finally{Array.Clear(auth,0,auth.Length);}
                    var accepted=await Read(stream,2,deadline.Token).ConfigureAwait(false);
                    if(accepted[0]!=1||accepted[1]!=0)throw new UnauthorizedAccessException("Ядро отклонило авторизацию SOCKS5.");
                    Verify(peer);
                    await stream.WriteAsync(request,0,request.Length,deadline.Token).ConfigureAwait(false);
                    var reply=await Read(stream,4,deadline.Token).ConfigureAwait(false);
                    if(reply[0]!=5||reply[1]!=0||reply[2]!=0)throw new IOException("Ядро отказало в соединении SOCKS5.");
                    int length;if(reply[3]==1)length=4;else if(reply[3]==4)length=16;else if(reply[3]==3){length=(await Read(stream,1,deadline.Token).ConfigureAwait(false))[0];if(length==0)throw new IOException("Пустой адрес ответа SOCKS5.");}else throw new IOException("Неизвестный адрес ответа SOCKS5.");
                    await Read(stream,length+2,deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();Verify(peer);
                }
            }catch(Exception)when(deadline.IsCancellationRequested){throw new OperationCanceledException("Подготовка локального ядра отменена или превысила срок ожидания.",deadline.Token);}
        }
    }
    static async Task<byte[]> Read(NetworkStream stream,int count,CancellationToken token){var bytes=new byte[count];int offset=0;while(offset<count){int size=await stream.ReadAsync(bytes,offset,count-offset,token).ConfigureAwait(false);if(size==0)throw new EndOfStreamException("Неполный ответ SOCKS5.");offset+=size;}return bytes;}
}

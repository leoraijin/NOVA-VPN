using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
namespace NovaVpn;

// Single-request DNS TCP transport through a caller-owned pinned SOCKS core.
// It never opens a socket to the resolver itself or uses the Windows resolver.
// No LAN listener, cache, DNSSEC validation or gateway activation is implied.
public sealed class GatewayDnsTcpClient
{
    readonly int corePid;readonly long coreCreated;readonly ApplicationBandwidthIdentity coreIdentity;
    internal GatewayCoreLifetime OpenLifetime()=>new GatewayCoreLifetime(corePid,coreCreated,coreIdentity);
    readonly IPEndPoint coreEndpoint;
    readonly ApplicationBandwidthSocksCore prepare;
    readonly TimeSpan timeout;
    readonly int maximum;
    int active;
    public int ActiveCount=>Volatile.Read(ref active);
    public GatewayDnsTcpClient(IPEndPoint coreEndpoint,int corePid,long coreCreated,ApplicationBandwidthIdentity coreIdentity,string username,string password,IPAddress resolver,int resolverPort=53,int maximumRequests=32,TimeSpan? timeout=null)
    {
        if(coreEndpoint==null||coreEndpoint.Address.AddressFamily!=AddressFamily.InterNetwork||!IPAddress.IsLoopback(coreEndpoint.Address)||coreEndpoint.Port<1)throw new ArgumentException("Нужен точный IPv4 loopback-вход принадлежащего NOVA ядра.");
        if(resolver==null||resolver.IsIPv4MappedToIPv6||resolver.Equals(IPAddress.Any)||resolver.Equals(IPAddress.IPv6Any)||resolver.Equals(IPAddress.Broadcast)||resolver.IsIPv6Multicast||resolver.AddressFamily==AddressFamily.InterNetwork&&resolver.GetAddressBytes()[0]>=224||resolver.AddressFamily==AddressFamily.InterNetworkV6&&resolver.ScopeId!=0)throw new ArgumentException("Нужен точный unicast IP DNS-сервера.");
        if(maximumRequests<1||maximumRequests>128)throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        this.timeout=timeout??TimeSpan.FromSeconds(10);if(this.timeout<=TimeSpan.Zero||this.timeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(timeout));
        this.coreEndpoint=new IPEndPoint(new IPAddress(coreEndpoint.Address.GetAddressBytes()),coreEndpoint.Port);maximum=maximumRequests;
        prepare=new ApplicationBandwidthSocksCore(corePid,coreCreated,coreIdentity,username,password,resolver.ToString(),resolverPort,this.timeout);
        this.corePid=corePid;this.coreCreated=coreCreated;this.coreIdentity=coreIdentity;
    }
    public async Task<byte[]> QueryAsync(byte[] query,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(query==null)throw new ArgumentNullException(nameof(query));if(query.Length<12||query.Length>65535)throw new InvalidDataException("Некорректный размер DNS.");
        var frozen=(byte[])query.Clone();var question=Packet.Parse(frozen,false);
        while(true){int count=Volatile.Read(ref active);if(count>=maximum)throw new InvalidOperationException("Лимит ожидающих DNS-запросов исчерпан.");if(Interlocked.CompareExchange(ref active,count+1,count)==count)break;}
        try{
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            using(var client=new TcpClient(AddressFamily.InterNetwork))
            using(var close=deadline.Token.Register(()=>client.Close())){
                deadline.CancelAfter(timeout);
                try{
                    await client.ConnectAsync(coreEndpoint.Address,coreEndpoint.Port).ConfigureAwait(false);deadline.Token.ThrowIfCancellationRequested();
                    await prepare.PrepareAsync(client.Client,deadline.Token).ConfigureAwait(false);
                    var stream=client.GetStream();var frame=new byte[frozen.Length+2];frame[0]=(byte)(frozen.Length>>8);frame[1]=(byte)frozen.Length;Buffer.BlockCopy(frozen,0,frame,2,frozen.Length);
                    await stream.WriteAsync(frame,0,frame.Length,deadline.Token).ConfigureAwait(false);
                    var length=await Read(stream,2,deadline.Token).ConfigureAwait(false);int size=length[0]*256+length[1];if(size<12)throw new InvalidDataException("Неполный заголовок DNS TCP.");
                    var response=await Read(stream,size,deadline.Token).ConfigureAwait(false);var answer=Packet.Parse(response,true);
                    if(question.Id!=answer.Id||question.Name!=answer.Name||question.Type!=answer.Type||question.Class!=answer.Class)throw new InvalidDataException("Ответ DNS не соответствует запросу.");
                    deadline.Token.ThrowIfCancellationRequested();return response;
                }catch(Exception)when(deadline.IsCancellationRequested){throw new OperationCanceledException("DNS-запрос отменён или превысил срок ожидания.",deadline.Token);}
            }
        }finally{Interlocked.Decrement(ref active);}
    }
    internal static int UdpPayloadLimit(byte[] query)=>Packet.Parse(query,false).UdpSize;
    internal static byte[] UdpAnswer(byte[] query,byte[] response)
    {
        var request=Packet.Parse(query,false);var answer=Packet.Parse(response,true);
        if(request.Id!=answer.Id||request.Name!=answer.Name||request.Type!=answer.Type||request.Class!=answer.Class)throw new InvalidDataException("Ответ DNS не соответствует запросу.");
        int limit=Math.Min(1232,request.UdpSize);if(response.Length<=limit){var copy=(byte[])response.Clone();if(answer.OptTtl!=null&&(copy[answer.OptClassOffset]*256+copy[answer.OptClassOffset+1])>1232){copy[answer.OptClassOffset]=4;copy[answer.OptClassOffset+1]=208;}return copy;}
        bool opt=request.OptTtl!=null&&answer.OptTtl!=null;
        var small=new byte[request.QuestionEnd+(opt?11:0)];Buffer.BlockCopy(response,0,small,0,12);Buffer.BlockCopy(query,12,small,12,request.QuestionEnd-12);
        small[2]|=2;small[3]&=0xdf;for(int i=6;i<12;i++)small[i]=0;
        if(opt){int at=request.QuestionEnd;small[11]=1;small[at+2]=41;small[at+3]=4;small[at+4]=208;Buffer.BlockCopy(answer.OptTtl,0,small,at+5,4);}
        return small;
    }
    static async Task<byte[]> Read(NetworkStream stream,int count,CancellationToken token){var data=new byte[count];int offset=0;while(offset<count){int got=await stream.ReadAsync(data,offset,count-offset,token).ConfigureAwait(false);if(got==0)throw new EndOfStreamException("Неполный ответ DNS TCP.");offset+=got;}return data;}
    sealed class Packet
    {
        public int Id,Type,Class,QuestionEnd,OptClassOffset,UdpSize=512;public string Name;public byte[] OptTtl;
        static int U16(byte[] data,int offset){if(offset<0||offset+2>data.Length)throw new InvalidDataException("Неполное поле DNS.");return data[offset]*256+data[offset+1];}
        static string NameAt(byte[] data,ref int offset)
        {
            int cursor=offset,resume=-1,hops=0;var name=new List<byte>();
            while(true){
                if(cursor>=data.Length||++hops>128)throw new InvalidDataException("Повреждённое имя DNS.");int label=data[cursor++];
                if((label&192)==192){if(cursor>=data.Length)throw new InvalidDataException("Неполный указатель DNS.");int target=((label&63)<<8)|data[cursor++];if(target<12||target>=cursor-2)throw new InvalidDataException("Некорректный указатель DNS.");if(resume<0)resume=cursor;cursor=target;continue;}
                if((label&192)!=0||cursor+label>data.Length||name.Count+label+1>255)throw new InvalidDataException("Некорректная метка DNS.");name.Add((byte)label);
                for(int i=0;i<label;i++){byte b=data[cursor++];name.Add((byte)(b>=65&&b<=90?b+32:b));}
                if(label==0){offset=resume<0?cursor:resume;return Convert.ToBase64String(name.ToArray());}
            }
        }
        public static Packet Parse(byte[] data,bool response)
        {
            if(data.Length<12||data.Length>65535)throw new InvalidDataException("Некорректный размер DNS.");int flags=U16(data,2),an=U16(data,6),ns=U16(data,8),ar=U16(data,10);
            if(((flags&0x8000)!=0)!=response||(flags&0x7840)!=0||(flags&0x0200)!=0||U16(data,4)!=1||an+ns+ar>4096||!response&&((flags&~0x0130)!=0||an!=0||ns!=0||ar>16))throw new InvalidDataException("Поддерживается один стандартный полный DNS-запрос/ответ.");
            int offset=12;string name=NameAt(data,ref offset);var result=new Packet{Id=U16(data,0),Name=name,Type=U16(data,offset),Class=U16(data,offset+2)};offset+=4;result.QuestionEnd=offset;
            if(result.Type==251||result.Type==252)throw new InvalidDataException("Передача DNS-зон не поддерживается шлюзом.");
            for(int i=0;i<an+ns+ar;i++){
                string owner=NameAt(data,ref offset);int type=U16(data,offset),recordClass=U16(data,offset+2),size=U16(data,offset+8);if(offset+10+size>data.Length)throw new InvalidDataException("Неполная запись DNS.");
                if(type==41){
                    if(i<an+ns||owner!="AA=="||result.OptTtl!=null)throw new InvalidDataException("Некорректная запись EDNS.");
                    result.OptClassOffset=offset+2;result.UdpSize=Math.Max(512,recordClass);result.OptTtl=data.Skip(offset+4).Take(4).ToArray();
                    if(!response&&(result.OptTtl[0]!=0||result.OptTtl[1]!=0))throw new InvalidDataException("Поддерживается EDNS версии0.");
                    int option=offset+10,end=option+size;while(option<end){if(end-option<4)throw new InvalidDataException("Неполная опция EDNS.");int length=U16(data,option+2);option+=4;if(length>end-option)throw new InvalidDataException("Неполные данные EDNS.");option+=length;}
                }
                offset+=10+size;
            }
            if(offset!=data.Length)throw new InvalidDataException("Лишние данные после DNS-пакета.");return result;
        }
    }
}

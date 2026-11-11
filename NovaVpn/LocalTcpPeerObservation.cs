using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NovaVpn;

// Read-only, best-effort local TCP peer observation, not an authorization token,
// WFP flow attestation or proof of an original app hidden behind another proxy.
// Unknown/inaccessible data must never be treated as an unlimited bypass.
public sealed class LocalTcpPeerObservation : IDisposable
{
    readonly object sync=new object();
    readonly Socket socket;
    readonly IPEndPoint local,remote;
    SafeProcessHandle process;
    public int ProcessId { get; }
    public long ProcessCreationFileTime { get; }
    public ApplicationBandwidthIdentity Identity { get; }
    public IPEndPoint LocalEndpoint=>Copy(local);
    public IPEndPoint PeerEndpoint=>Copy(remote);
    LocalTcpPeerObservation(Socket socket,IPEndPoint local,IPEndPoint remote,int pid,SafeProcessHandle process,long created,ApplicationBandwidthIdentity identity)
    {this.socket=socket;this.local=local;this.remote=remote;ProcessId=pid;this.process=process;ProcessCreationFileTime=created;Identity=identity;}
    public static LocalTcpPeerObservation TryObserve(Socket connectedSocket)
    {
        SafeProcessHandle held=null;
        try{
            if(!Endpoints(connectedSocket,out var local,out var remote))return null;
            int pid=Owner(remote,local);if(pid<=0)return null;
            held=OpenProcess(0x00101000,false,pid);if(held==null||held.IsInvalid)return null;
            if(!ReadIdentity(held,out long created,out var identity))return null;
            if(Owner(remote,local)!=pid||!Endpoints(connectedSocket,out var againLocal,out var againRemote)||!Same(local,againLocal)||!Same(remote,againRemote)||!Alive(held))return null;
            var result=new LocalTcpPeerObservation(connectedSocket,local,remote,pid,held,created,identity);held=null;return result;
        }catch(Exception error)when(Unavailable(error)){return null;}
        finally{held?.Dispose();}
    }
    public bool IsCurrent()
    {
        lock(sync){
            if(process==null)return false;
            try{
                return Endpoints(socket,out var nowLocal,out var nowRemote)&&Same(local,nowLocal)&&Same(remote,nowRemote)&&
                    ReadIdentity(process,out long created,out var identity)&&created==ProcessCreationFileTime&&
                    identity.UserSid==Identity.UserSid&&String.Equals(identity.ExecutablePath,Identity.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&Owner(remote,local)==ProcessId&&Alive(process);
            }catch(Exception error)when(Unavailable(error)){return false;}
        }
    }
    static bool Unavailable(Exception error)=>error is Win32Exception||error is IOException||error is SocketException||error is InvalidOperationException||error is ArgumentException||error is SecurityException||error is DllNotFoundException||error is EntryPointNotFoundException;
    static bool Endpoints(Socket socket,out IPEndPoint local,out IPEndPoint remote)
    {
        local=remote=null;if(socket==null||socket.SocketType!=SocketType.Stream||socket.ProtocolType!=ProtocolType.Tcp)return false;
        var a=socket.LocalEndPoint as IPEndPoint;var b=socket.RemoteEndPoint as IPEndPoint;
        if(a==null||b==null||a.Port==0||b.Port==0)return false;
        local=Copy(a);remote=Copy(b);return IPAddress.IsLoopback(local.Address)&&IPAddress.IsLoopback(remote.Address)&&local.AddressFamily==remote.AddressFamily;
    }
    static IPAddress Normalize(IPAddress address){if(address.IsIPv4MappedToIPv6)return address.MapToIPv4();return address.AddressFamily==AddressFamily.InterNetworkV6?new IPAddress(address.GetAddressBytes(),address.ScopeId):new IPAddress(address.GetAddressBytes());}
    static IPEndPoint Copy(IPEndPoint endpoint)=>new IPEndPoint(Normalize(endpoint.Address),endpoint.Port);
    static bool Same(IPEndPoint a,IPEndPoint b)=>a.Port==b.Port&&a.Address.Equals(b.Address);
    static bool Alive(SafeProcessHandle process)=>WaitForSingleObject(process,0)==258;
    internal static bool ReadIdentity(SafeProcessHandle process,out long created,out ApplicationBandwidthIdentity identity)
    {
        created=0;identity=null;if(!Alive(process))return false;
        if(!GetProcessTimes(process,out var creation,out var exit,out var kernel,out var user))return false;
        created=((long)creation.High<<32)|creation.Low;if(created<=0)return false;
        var path=new StringBuilder(32768);uint size=(uint)path.Capacity;if(!QueryFullProcessImageName(process,0,path,ref size))return false;
        if(!OpenProcessToken(process,8,out var token))return false;
        using(token)using(var account=new WindowsIdentity(token.DangerousGetHandle())){
            if(account.User==null)return false;identity=new ApplicationBandwidthIdentity(account.User.Value,path.ToString());
        }
        return Alive(process);
    }
    static int Owner(IPEndPoint source,IPEndPoint destination)
    {
        int family=source.AddressFamily==AddressFamily.InterNetwork?2:23;uint required=0;
        uint status=GetExtendedTcpTable(IntPtr.Zero,ref required,false,family,5,0);
        if(status!=122&&status!=0)throw new Win32Exception((int)status);
        for(int attempt=0;attempt<4;attempt++){
            if(required<4||required>16*1024*1024)throw new InvalidDataException("Некорректный размер таблицы TCP.");
            int capacity=(int)required;IntPtr memory=Marshal.AllocHGlobal(capacity);
            try{
                uint actual=(uint)capacity;status=GetExtendedTcpTable(memory,ref actual,false,family,5,0);
                if(status==122){required=actual;continue;}
                if(status!=0)throw new Win32Exception((int)status);
                if(actual<4||actual>capacity)throw new InvalidDataException("Размер таблицы TCP изменился.");
                var bytes=new byte[actual];Marshal.Copy(memory,bytes,0,bytes.Length);return FindOwner(bytes,family,source,destination);
            }finally{Marshal.FreeHGlobal(memory);}
        }
        throw new IOException("Таблица TCP меняется слишком быстро.");
    }
    internal static int FindOwner(byte[] bytes,int family,IPEndPoint source,IPEndPoint destination)
    {
        if(bytes==null||bytes.Length<4||bytes.Length>16*1024*1024||(family!=2&&family!=23)||source==null||destination==null)throw new InvalidDataException("Некорректная таблица TCP.");
        int rowSize=family==2?24:56;uint count=Little(bytes,0);if(count>(bytes.Length-4)/rowSize)throw new InvalidDataException("Таблица TCP обрезана.");
        var expectedSource=Copy(source);var expectedDestination=Copy(destination);int owner=0,matches=0;
        for(int index=0;index<count;index++){
            int at=4+index*rowSize;uint state=Little(bytes,at+(family==2?0:48));
            // FIN_WAIT and CLOSE_WAIT still permit the remaining half of a stream.
            if(state<5||state>8)continue;
            IPEndPoint local,remote;
            if(family==2){local=new IPEndPoint(Address(bytes,at+4,4),Port(bytes,at+8));remote=new IPEndPoint(Address(bytes,at+12,4),Port(bytes,at+16));}
            else{local=new IPEndPoint(Address(bytes,at,16,Network(bytes,at+16)),Port(bytes,at+20));remote=new IPEndPoint(Address(bytes,at+24,16,Network(bytes,at+40)),Port(bytes,at+44));}
            if(!Same(Copy(local),expectedSource)||!Same(Copy(remote),expectedDestination))continue;
            uint pid=Little(bytes,at+(family==2?20:52));if(pid==0||pid>Int32.MaxValue)return 0;owner=(int)pid;matches++;
        }
        return matches==1?owner:0;
    }
    static IPAddress Address(byte[] bytes,int offset,int size,long scope=0){var value=new byte[size];Buffer.BlockCopy(bytes,offset,value,0,size);return size==16?new IPAddress(value,scope):new IPAddress(value);}
    static int Port(byte[] b,int p)=>(b[p]<<8)|b[p+1];
    static uint Little(byte[] b,int p)=>(uint)(b[p]|b[p+1]<<8|b[p+2]<<16|b[p+3]<<24);
    static uint Network(byte[] b,int p)=>(uint)(b[p]<<24|b[p+1]<<16|b[p+2]<<8|b[p+3]);
    public void Dispose(){lock(sync){var held=process;process=null;held?.Dispose();}}
    [StructLayout(LayoutKind.Sequential)]struct FileTime{public uint Low,High;}
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref uint size,bool order,int family,int tableClass,uint reserved);
    [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")]static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessTimes(SafeProcessHandle process,out FileTime creation,out FileTime exit,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref uint size);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
}

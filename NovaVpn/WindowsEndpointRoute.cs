using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NovaVpn;

// Read-only route selection. It never adds routes or changes interface/DNS state.
public sealed class WindowsEndpointRoute
{
    readonly IPAddress endpoint, source;
    readonly string routeKey;
    public ulong InterfaceLuid { get; }
    public uint InterfaceIndex { get; }
    public Guid InterfaceId { get; }
    public string InterfaceName { get; }
    public IPAddress Endpoint => Copy(endpoint);
    public IPAddress SourceAddress => Copy(source);
    WindowsEndpointRoute(IPAddress endpoint, Selection selected, Guid id, string name)
    { this.endpoint=Copy(endpoint);source=Copy(selected.Source);InterfaceLuid=selected.Luid;InterfaceIndex=selected.Index;InterfaceId=id;InterfaceName=name;routeKey=selected.Key; }
    static IPAddress Copy(IPAddress ip) => ip.AddressFamily==AddressFamily.InterNetworkV6 ? new IPAddress(ip.GetAddressBytes(),ip.ScopeId) : new IPAddress(ip.GetAddressBytes());
    static uint U32(byte[] bytes,int offset) => BitConverter.ToUInt32(bytes,offset);
    static IPAddress Address(byte[] bytes,int offset)
    {
        int family=bytes[offset]|bytes[offset+1]<<8;
        if(family!=2&&family!=23)throw new InvalidDataException("Invalid route address family.");
        var address=new byte[family==2?4:16];Buffer.BlockCopy(bytes,offset+(family==2?4:8),address,0,address.Length);
        return family==2?new IPAddress(address):new IPAddress(address,U32(bytes,offset+24));
    }
    internal sealed class Selection
    {
        internal ulong Luid;internal uint Index;internal IPAddress Source;internal string Key;
    }
    internal static Selection Decode(IPAddress endpoint,byte[] row,byte[] bestSource)
    {
        if(endpoint==null||row==null||row.Length!=104||bestSource==null||bestSource.Length!=28)throw new InvalidDataException("Invalid best-route buffers.");
        var prefix=Address(row,12);var nextHop=Address(row,44);var source=Address(bestSource,0);
        int bits=row[40],length=endpoint.AddressFamily==AddressFamily.InterNetwork?4:16;
        if(prefix.AddressFamily!=endpoint.AddressFamily||nextHop.AddressFamily!=endpoint.AddressFamily||source.AddressFamily!=endpoint.AddressFamily||bits>length*8||row[72]>length*8)throw new InvalidDataException("Inconsistent best-route families/prefix.");
        byte[] target=endpoint.GetAddressBytes(),network=prefix.GetAddressBytes();
        for(int i=0;i<length;i++)
        {
            int mask=bits-i*8>=8?255:bits-i*8<=0?0:255<<(8-(bits-i*8))&255;
            if((target[i]&mask)!=network[i])throw new InvalidDataException("Best route does not contain the endpoint.");
        }
        ulong luid=BitConverter.ToUInt64(row,0);uint index=U32(row,8);
        if(luid==0||index==0||source.Equals(IPAddress.Any)||source.Equals(IPAddress.IPv6Any)||source.IsIPv6Multicast||source.AddressFamily==AddressFamily.InterNetwork&&source.GetAddressBytes()[0]>=224)throw new InvalidDataException("Missing best-route interface/source.");
        return new Selection{Luid=luid,Index=index,Source=source,Key=luid+"|"+index+"|"+prefix+"/"+bits+"|"+nextHop+"|"+source+"|"+U32(row,84)+"|"+U32(row,88)+"|"+row[72]+"|"+row[92]+":"+row[93]+":"+row[94]+":"+row[95]+"|"+U32(row,100)};
    }
    static Selection Query(IPAddress destination,ulong selectedLuid)
    {
        if(destination==null)throw new ArgumentNullException(nameof(destination));
        if(destination.IsIPv4MappedToIPv6)destination=destination.MapToIPv4();
        if(destination.Equals(IPAddress.Any)||destination.Equals(IPAddress.IPv6Any)||destination.Equals(IPAddress.Broadcast)||destination.IsIPv6Multicast||destination.AddressFamily==AddressFamily.InterNetwork&&destination.GetAddressBytes()[0]>=224)throw new ArgumentException("Unicast route endpoint required.");
        var address=new byte[28];bool ipv6=destination.AddressFamily==AddressFamily.InterNetworkV6;
        address[0]=(byte)(ipv6?23:2);Buffer.BlockCopy(destination.GetAddressBytes(),0,address,ipv6?8:4,ipv6?16:4);
        if(ipv6)Buffer.BlockCopy(BitConverter.GetBytes(checked((uint)destination.ScopeId)),0,address,24,4);
        IntPtr input=IntPtr.Zero,row=IntPtr.Zero,source=IntPtr.Zero,luid=IntPtr.Zero;
        try
        {
            input=Marshal.AllocHGlobal(28);row=Marshal.AllocHGlobal(104);source=Marshal.AllocHGlobal(28);
            Marshal.Copy(address,0,input,28);Marshal.Copy(new byte[104],0,row,104);Marshal.Copy(new byte[28],0,source,28);
            if(selectedLuid!=0){luid=Marshal.AllocHGlobal(8);Marshal.WriteInt64(luid,unchecked((long)selectedLuid));}
            uint error=GetBestRoute2(luid,0,IntPtr.Zero,input,0,row,source);
            if(error!=0)throw new IOException("No usable route to the VPN endpoint.",new System.ComponentModel.Win32Exception((int)error));
            var routeBytes=new byte[104];var sourceBytes=new byte[28];Marshal.Copy(row,routeBytes,0,104);Marshal.Copy(source,sourceBytes,0,28);
            return Decode(destination,routeBytes,sourceBytes);
        }
        finally{if(input!=IntPtr.Zero)Marshal.FreeHGlobal(input);if(row!=IntPtr.Zero)Marshal.FreeHGlobal(row);if(source!=IntPtr.Zero)Marshal.FreeHGlobal(source);if(luid!=IntPtr.Zero)Marshal.FreeHGlobal(luid);}
    }
    static NetworkInterface Interface(Selection selected,out Guid id)
    {
        ulong luid=selected.Luid;
        if(ConvertInterfaceLuidToGuid(ref luid,out id)!=0||id==Guid.Empty)throw new IOException("Selected route interface is missing.");
        Guid actualId=id;
        var matches=NetworkInterface.GetAllNetworkInterfaces().Where(n=>Guid.TryParse(n.Id,out var value)&&value==actualId).ToArray();
        if(matches.Length!=1||matches[0].OperationalStatus!=OperationalStatus.Up||string.IsNullOrEmpty(matches[0].Name)||matches[0].Name.Length>256||matches[0].Name.Any(char.IsControl))throw new IOException("Selected route interface is not ready.");
        var properties=matches[0].GetIPProperties();
        int index=selected.Source.AddressFamily==AddressFamily.InterNetwork?properties.GetIPv4Properties().Index:properties.GetIPv6Properties().Index;
        if(index!=selected.Index||!properties.UnicastAddresses.Any(a=>a.Address.Equals(selected.Source)))throw new IOException("Selected source address changed.");
        if(ConvertInterfaceGuidToLuid(ref actualId,out ulong back)!=0||back!=luid)throw new IOException("Selected interface identity changed.");
        return matches[0];
    }
    public static WindowsEndpointRoute Capture(IPAddress destination)
    {
        if(destination==null)throw new ArgumentNullException(nameof(destination));
        destination=destination.IsIPv4MappedToIPv6?destination.MapToIPv4():Copy(destination);
        var selected=Query(destination,0);var network=Interface(selected,out Guid id);
        var result=new WindowsEndpointRoute(destination,selected,id,network.Name);result.DemandBeforeLaunch();return result;
    }
    void Demand(ulong interfaceLuid)
    {
        var selected=Query(endpoint,interfaceLuid);var network=Interface(selected,out Guid id);
        if(selected.Key!=routeKey||id!=InterfaceId||network.Name!=InterfaceName)throw new IOException("VPN endpoint route changed; prepare a new connection.");
    }
    public void DemandBeforeLaunch()=>Demand(0);
    // Once a TUN is installed it may become the global best route. Revalidate
    // the deliberately selected upstream, not the TUN's new default route.
    public void DemandCurrent()=>Demand(InterfaceLuid);
    [DllImport("iphlpapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint GetBestRoute2(IntPtr luid,uint index,IntPtr source,IntPtr destination,uint options,IntPtr row,IntPtr bestSource);
    [DllImport("iphlpapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint ConvertInterfaceLuidToGuid(ref ulong luid,out Guid id);
    [DllImport("iphlpapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint ConvertInterfaceGuidToLuid(ref Guid id,out ulong luid);
}

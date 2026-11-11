using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
namespace NovaVpn;

// Address allocation only. This is not an authorization to create a NAT, expose
// a DNS listener, enable forwarding, or weaken TrafficGuard's forwarding deny.
public sealed class GatewayAddressPlan
{
    public string UlaGlobalId { get; }
    public string Ipv4Prefix { get; }
    public string Ipv4Gateway { get; }
    public string Ipv6Prefix { get; }
    public string Ipv6Gateway { get; }
    public string[] DnsAddresses => new[]{Ipv4Gateway,Ipv6Gateway};
    GatewayAddressPlan(Prefix v4,Prefix v6,GatewayUlaIdentity ula){UlaGlobalId=ula.GlobalId;Ipv4Prefix=v4.Text;Ipv6Prefix=v6.Text;Ipv4Gateway=Gateway(v4);Ipv6Gateway=Gateway(v6);}
    static string Gateway(Prefix prefix){var bytes=(byte[])prefix.Bytes.Clone();bytes[bytes.Length-1]=1;return new IPAddress(bytes).ToString();}
    sealed class Prefix
    {
        public readonly byte[] Bytes;
        public readonly int Bits;
        public string Text=>new IPAddress(Bytes)+"/"+Bits.ToString(CultureInfo.InvariantCulture);
        public Prefix(string text){
            if(string.IsNullOrEmpty(text)||text.Length>80||text.Any(char.IsWhiteSpace)||text.Contains("%"))throw new InvalidDataException("Неверная подсеть при подготовке шлюза.");
            var parts=text.Split('/');IPAddress address;int bits;
            if(parts.Length!=2||parts[0]!=parts[0].Trim()||!IPAddress.TryParse(parts[0],out address)||address.IsIPv4MappedToIPv6||address.AddressFamily==AddressFamily.InterNetworkV6&&address.ScopeId!=0||!int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out bits)||bits<0||bits>address.GetAddressBytes().Length*8)throw new InvalidDataException("Неверная подсеть при подготовке шлюза.");
            if(address.AddressFamily==AddressFamily.InterNetwork&&address.ToString()!=parts[0])throw new InvalidDataException("Нужна IPv4-подсеть в полном десятичном формате.");
            Bytes=address.GetAddressBytes();Bits=bits;
            for(int i=0;i<Bytes.Length;i++){int remaining=bits-i*8;Bytes[i]=(byte)(Bytes[i]&(remaining>=8?255:remaining<=0?0:255<<(8-remaining)));}
        }
        public bool Overlaps(Prefix other){if(Bytes.Length!=other.Bytes.Length)return false;int bits=Math.Min(Bits,other.Bits);for(int i=0;bits>0;i++,bits-=8){int mask=255<<Math.Max(0,8-bits)&255;if((Bytes[i]&mask)!=(other.Bytes[i]&mask))return false;}return true;}
    }
    public static GatewayAddressPlan Prepare(IEnumerable<string> occupiedPrefixes,IEnumerable<string> existingNatPrefixes) => Prepare(occupiedPrefixes,existingNatPrefixes,GatewayUlaIdentity.Create());
    public static GatewayAddressPlan Prepare(IEnumerable<string> occupiedPrefixes,IEnumerable<string> existingNatPrefixes,GatewayUlaIdentity ula)
    {
        if(occupiedPrefixes==null||existingNatPrefixes==null||ula==null)throw new ArgumentNullException("gateway inventory");
        var occupied=occupiedPrefixes.Take(8193).ToArray();var nat=existingNatPrefixes.Take(257).ToArray();
        if(occupied.Length>8192||nat.Length>256)throw new InvalidDataException("Слишком большой снимок сетей шлюза.");
        // Never silently reuse/delete a NAT owned by HNS, Docker, WSL or a user.
        // Adoption needs a separate ownership and rollback transaction.
        if(nat.Length!=0)throw new InvalidOperationException("В Windows уже существует NAT. NOVA не будет изменять его без отдельной проверки совместимости.");
        var networks=occupied.Select(p=>new Prefix(p)).ToArray();
        var v4=Enumerable.Range(0,256).Select(i=>new Prefix("10.92."+i.ToString(CultureInfo.InvariantCulture)+".0/24")).FirstOrDefault(p=>!networks.Any(n=>p.Overlaps(n)));
        var v6=Enumerable.Range(0,256).Select(i=>new Prefix(ula.Subnet((ushort)i))).FirstOrDefault(p=>!networks.Any(n=>p.Overlaps(n)));
        if(v4==null||v6==null)throw new InvalidOperationException("Не найдена свободная подсеть для обеих версий IP. Подготовка шлюза остановлена.");
        return new GatewayAddressPlan(v4,v6,ula);
    }
}

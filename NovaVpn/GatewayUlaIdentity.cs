using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
namespace NovaVpn;

// Persistent identity material for a future managed gateway, not a credential.
// Generate once for a gateway and retain GlobalId in its ownership journal.
public sealed class GatewayUlaIdentity
{
    readonly byte[] globalId;
    public string GlobalId=>BitConverter.ToString(globalId).Replace("-","").ToLowerInvariant();
    public string Prefix48=>Subnet(0).Replace("/64","/48");
    GatewayUlaIdentity(byte[] id){globalId=(byte[])id.Clone();}
    public static GatewayUlaIdentity Create(){var id=new byte[5];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(id);return new GatewayUlaIdentity(id);}
    public static GatewayUlaIdentity Parse(string globalId)
    {
        if(globalId==null||globalId.Length!=10||globalId.Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'))))throw new InvalidDataException("Неверный идентификатор IPv6-сети шлюза.");
        var id=new byte[5];for(int i=0;i<id.Length;i++)id[i]=byte.Parse(globalId.Substring(i*2,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        return new GatewayUlaIdentity(id);
    }
    public string Subnet(ushort subnet)
    {
        var bytes=new byte[16];bytes[0]=0xfd;Array.Copy(globalId,0,bytes,1,5);bytes[6]=(byte)(subnet>>8);bytes[7]=(byte)subnet;
        return new IPAddress(bytes)+"/64";
    }
}

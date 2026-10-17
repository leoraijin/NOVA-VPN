using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class SubscriptionIdentity
{
    [DataMember] public string id = Guid.NewGuid().ToString("N");
    [DataMember] public string name = "Новая идентичность";
    [DataMember] public string hwid = Guid.NewGuid().ToString("N");
    [DataMember] public string hwidHeader = "X-HWID";
    [DataMember] public string userAgent = "NOVA-VPN/1.0";
    [DataMember] public Dictionary<string,string> headers = new Dictionary<string,string>();
    public override string ToString() => name;
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(id) || id.Length > 64 || String.IsNullOrWhiteSpace(name) || name.Length > 100) throw new FormatException("Некорректное имя профиля.");
        HeaderPolicy.Value(hwid); HeaderPolicy.Value(userAgent); HeaderPolicy.Name(hwidHeader);
        if (String.IsNullOrWhiteSpace(hwid) || String.IsNullOrWhiteSpace(userAgent)) throw new FormatException("HWID и User-Agent обязательны.");
        if (headers == null || headers.Count > 16) throw new FormatException("Допустимо до 16 заголовков.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hwidHeader, "User-Agent" };
        foreach(var h in headers) { HeaderPolicy.Name(h.Key); HeaderPolicy.Value(h.Value); if (!seen.Add(h.Key)) throw new FormatException("Повторяющийся заголовок."); }
    }
    public Dictionary<string,string> RequestHeaders()
    {
        Validate(); var result = new Dictionary<string,string>(headers, StringComparer.OrdinalIgnoreCase);
        result.Add(hwidHeader,hwid); result.Add("User-Agent",userAgent); return result;
    }
}
public static class HeaderPolicy
{
    static readonly HashSet<string> Reserved = new HashSet<string>(new[] { "host", "content-length", "transfer-encoding", "connection", "proxy-authorization", "proxy-connection", "cookie", "set-cookie", "upgrade", "te", "trailer", "expect", "user-agent", "accept-encoding" }, StringComparer.OrdinalIgnoreCase);
    public static void Name(string s) { if (String.IsNullOrEmpty(s) || s.Length > 64 || !Regex.IsMatch(s,"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$") || Reserved.Contains(s)) throw new FormatException("Недопустимый HTTP-заголовок."); }
    public static void Value(string s) { if(s == null || s.Length > 1024 || s.Any(c => c < 32 || c > 126)) throw new FormatException("Значения заголовков должны содержать печатные ASCII-символы, не более 1024."); }
}
[DataContract]
public sealed class IdentityBinding
{
    [DataMember] public string url;
    [DataMember] public string identityId;
}
[DataContract]
public sealed class IdentityState
{
    [DataMember] public List<SubscriptionIdentity> identities = new List<SubscriptionIdentity>();
    [DataMember] public List<IdentityBinding> bindings = new List<IdentityBinding>();
    [DataMember] public Dictionary<string,string> platforms = new Dictionary<string,string>();
    public SubscriptionIdentity ForUrl(string url) { var id = bindings?.FirstOrDefault(b => String.Equals(b.url,url,StringComparison.Ordinal))?.identityId; return identities?.FirstOrDefault(i => i.id == id); }
    public void Validate()
    {
        if(identities == null || identities.Count > 100 || bindings == null || bindings.Count > 2000) throw new FormatException("Слишком много профилей.");
        var ids=new HashSet<string>(); foreach(var i in identities) { i.Validate(); if(!ids.Add(i.id)) throw new FormatException("Повторяющийся ID."); }
        var urls=new HashSet<string>(StringComparer.Ordinal); foreach(var b in bindings) { Uri u; if(!Uri.TryCreate(b.url,UriKind.Absolute,out u) || u.Scheme != "https" || !String.IsNullOrEmpty(u.UserInfo) || b.url.Length > 4096 || !ids.Contains(b.identityId) || !urls.Add(b.url)) throw new FormatException("Некорректная привязка подписки."); }
    }
}
public sealed class IdentityTransferDocument
{
    public string format = "nova-identity-v1";
    public IdentityState identity = new IdentityState();
}
public static class IdentityTransfer
{
    public const int MaxBytes = 8 * 1024 * 1024;
    const string Prefix = "NOVA-ID1:";
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("NVID1");
    static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 48 };
    public static string Export(IdentityState state, string password)
    {
        state.Validate(); return Seal(Json().Serialize(new IdentityTransferDocument { identity=state }),password);
    }
    public static IdentityState Import(string code,string password) => ParsePayload(Open(code,password));
    internal static IdentityState ParsePayload(string text)
    {
        var raw=Json().DeserializeObject(text) as Dictionary<string,object>;
        if(raw==null || !raw.ContainsKey("format") || !raw.ContainsKey("identity"))throw new FormatException("Неполный пакет.");
        var root=raw["identity"] as Dictionary<string,object>;
        if(root==null || !root.ContainsKey("identities") || !root.ContainsKey("bindings"))throw new FormatException("Неполный пакет.");
        var profiles=root["identities"] as object[];
        if(profiles==null)throw new FormatException("Некорректный список профилей.");
        foreach(var item in profiles){var p=item as Dictionary<string,object>;if(p==null || new[]{"id","name","hwid","hwidHeader","userAgent","headers"}.Any(k=>!p.ContainsKey(k)))throw new FormatException("Неполный профиль.");}
        var doc=Json().Deserialize<IdentityTransferDocument>(text);
        if(doc == null || doc.format != "nova-identity-v1" || doc.identity == null) throw new FormatException("Неизвестный формат профиля.");
        doc.identity.Validate(); return doc.identity;
    }
    public static string Seal(string json,string password)
    {
        Password(password); var plain=Encoding.UTF8.GetBytes(json); if(plain.Length > MaxBytes) throw new FormatException("Пакет больше 8 МБ.");
        byte[] salt=new byte[16],iv=new byte[16]; using(var rng=RandomNumberGenerator.Create()){rng.GetBytes(salt);rng.GetBytes(iv);}
        byte[] key=Keys(password,salt);
        try {
            byte[] cipher; using(var aes=Aes.Create()){ aes.Key=key.Take(32).ToArray(); aes.IV=iv; aes.Mode=CipherMode.CBC; aes.Padding=PaddingMode.PKCS7; using(var e=aes.CreateEncryptor())cipher=e.TransformFinalBlock(plain,0,plain.Length); }
            var body=Magic.Concat(salt).Concat(iv).Concat(cipher).ToArray(); byte[] tag; using(var h=new HMACSHA256(key.Skip(32).ToArray()))tag=h.ComputeHash(body);
            return Prefix+Convert.ToBase64String(body.Concat(tag).ToArray()).TrimEnd('=').Replace('+','-').Replace('/','_');
        } finally {Array.Clear(key,0,key.Length);Array.Clear(plain,0,plain.Length);}
    }
    public static string Open(string code,string password)
    {
        Password(password); code=(code??"").Trim(); if(!code.StartsWith(Prefix,StringComparison.Ordinal)||code.Length>12*1024*1024)throw new FormatException("Неизвестный или слишком большой пакет.");
        var encoded=code.Substring(Prefix.Length); if(!Regex.IsMatch(encoded,"^[A-Za-z0-9_-]+$"))throw new FormatException("Повреждённый пакет.");
        var raw=Convert.FromBase64String(encoded.Replace('-','+').Replace('_','/')+new string('=',(4-encoded.Length%4)%4));
        if(raw.Length<85 || raw.Length>MaxBytes+100 || !raw.Take(5).SequenceEqual(Magic) || (raw.Length-69)%16!=0)throw new FormatException("Повреждённый пакет.");
        var key=Keys(password,raw.Skip(5).Take(16).ToArray());
        try { byte[] expected; using(var h=new HMACSHA256(key.Skip(32).ToArray()))expected=h.ComputeHash(raw,0,raw.Length-32); int diff=0;for(int i=0;i<32;i++)diff|=expected[i]^raw[raw.Length-32+i]; if(diff!=0)throw new FormatException("Неверный пароль или повреждённый пакет.");
            using(var aes=Aes.Create()){aes.Key=key.Take(32).ToArray();aes.IV=raw.Skip(21).Take(16).ToArray();aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;using(var d=aes.CreateDecryptor())return new UTF8Encoding(false,true).GetString(d.TransformFinalBlock(raw,37,raw.Length-69));}
        }finally{Array.Clear(key,0,key.Length);}
    }
    static byte[] Keys(string password,byte[] salt){using(var k=new Rfc2898DeriveBytes(password,salt,210000,HashAlgorithmName.SHA256))return k.GetBytes(64);}
    static void Password(string p){if(p==null||p.Length<10||p.Length>256)throw new FormatException("Пароль должен содержать от 10 до 256 символов.");}
}

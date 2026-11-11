using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NovaVpn;

// A frozen package, not a deployed server or proof of artifact provenance.
// The future downloader must authenticate the expected release hash independently.
public sealed class VpsDeploymentDraft
{
    internal const int MaximumBinaryBytes = 64 * 1024 * 1024;
    const string Method = "2022-blake3-aes-256-gcm";
    readonly byte[] binary;
    readonly string host, password, profileId, config;
    public string Action { get; }
    public string BaselineRemovalOperationId { get; }
    public string InstallationId { get; }
    public string OperationId { get; }
    public int ExpectedRevision { get; }
    public string Architecture { get; }
    public string TargetIdentityKey { get; }
    public string BinarySha256 { get; }
    public string ConfigSha256 { get; }
    public int Port { get; }
    public bool Active { get; }
    public bool Enabled { get; }
    VpsDeploymentDraft(VpsSshTarget target, string installation, string operation, int revision, string architecture, byte[] artifact, string expectedHash, int port, bool active, bool enabled, string profileId, string action="deploy", string removalOperation=null)
    {
        if(target==null)throw new ArgumentNullException(nameof(target));
        ValidateIds(installation, operation, revision);
        if(profileId!=null&&!Regex.IsMatch(profileId,@"\A[0-9a-f]{32}\z"))throw new ArgumentException("Некорректный идентификатор профиля VPS.");
        this.profileId=profileId??Guid.NewGuid().ToString("N");
        if(port<1||port>65535)throw new ArgumentOutOfRangeException(nameof(port));
        if(architecture!="x86_64"&&architecture!="aarch64")throw new ArgumentException("Пока подготовлены серверные пакеты Linux x64 и ARM64.");
        if(artifact==null||artifact.Length<64||artifact.Length>MaximumBinaryBytes)throw new InvalidDataException("Некорректный размер серверного ядра.");
        if(expectedHash==null||!Regex.IsMatch(expectedHash,@"\A[0-9a-fA-F]{64}\z"))throw new ArgumentException("Нужна контрольная сумма серверного ядра из проверенного источника.");
        binary=(byte[])artifact.Clone();
        BinarySha256=Sha256(binary);
        if(!String.Equals(BinarySha256,expectedHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Контрольная сумма серверного ядра не совпала.");
        int machine=binary[18]|binary[19]<<8, type=binary[16]|binary[17]<<8;
        if(binary[0]!=0x7f||binary[1]!='E'||binary[2]!='L'||binary[3]!='F'||binary[4]!=2||binary[5]!=1||(type!=2&&type!=3)||machine!=(architecture=="x86_64"?62:183))throw new InvalidDataException("Ядро не соответствует выбранной архитектуре Linux.");
        Action=action;BaselineRemovalOperationId=removalOperation;
        InstallationId=installation;OperationId=operation;ExpectedRevision=revision;Architecture=architecture;Port=port;Active=active;Enabled=enabled;host=target.Host;TargetIdentityKey=target.IdentityKey;
        byte[] secret=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(secret);
        password=Convert.ToBase64String(secret);Array.Clear(secret,0,secret.Length);
        config=BuildServerConfig(password,port);
        ConfigSha256=Sha256(Encoding.UTF8.GetBytes(config));
    }
    public static VpsDeploymentDraft Create(VpsSshTarget target,string installation,string operation,int expectedRevision,string architecture,byte[] artifact,string expectedArtifactSha256,int port=443,bool active=true,bool enabled=true,string profileId=null)
        =>new VpsDeploymentDraft(target,installation,operation,expectedRevision,architecture,artifact,expectedArtifactSha256,port,active,enabled,profileId);
    public static VpsDeploymentDraft CreateUpdate(VpsSshTarget target,VpsOperationRecord previous,VpsServerStatus status,string operation,string architecture,byte[] artifact,string expectedArtifactSha256,int? port=null,bool? active=null,bool? enabled=null)
    {
        if(target==null||previous==null||status==null)throw new ArgumentNullException("VPS update context");
        previous.Validate();
        if(previous.Phase!="committed"||target.IdentityKey!=previous.TargetIdentityKey||!status.MatchesCandidate(previous)||status.Revision<previous.ExpectedRevision+1||operation==previous.OperationId)throw new InvalidOperationException("Текущая версия VPS не подтверждает исходный профиль для обновления.");
        return Create(target,previous.InstallationId,operation,status.Revision,architecture,artifact,expectedArtifactSha256,port??previous.VpnPort,active??status.ServiceActive,enabled??status.ServiceEnabled,previous.ProfileId);
    }
    public static VpsDeploymentDraft CreateReinstallation(VpsSshTarget target,VpsOperationRecord removed,VpsServerStatus status,string operation,string architecture,byte[] artifact,string expectedArtifactSha256,int? port=null,bool active=true,bool enabled=true)
    {
        if(target==null||removed==null||status==null)throw new ArgumentNullException("VPS reinstall context");
        removed.Validate();
        if(target.IdentityKey!=removed.TargetIdentityKey||!status.MatchesRemoval(removed)||operation==removed.OperationId)throw new InvalidOperationException("Удаление VPS не подтверждено для повторной установки.");
        return new VpsDeploymentDraft(target,removed.InstallationId,operation,status.Revision,architecture,artifact,expectedArtifactSha256,port??removed.VpnPort,active,enabled,removed.ProfileId,"reinstall",removed.OperationId);
    }
    internal static string BuildServerConfig(string password,int port)
    {
        return Serializer().Serialize(new Dictionary<string,object> {
            {"log",new Dictionary<string,object>{{"level","warn"}}},
            {"inbounds",new object[]{new Dictionary<string,object>{{"type","shadowsocks"},{"tag","nova-vps-in"},{"listen","::"},{"listen_port",port},{"method",Method},{"password",password}}}},
            {"outbounds",new object[]{new Dictionary<string,object>{{"type","direct"},{"tag","direct"}}}}
        });
    }
    public string ServerConfigJson=>config;
    public VpnProfile CreateClientProfile()=>BuildClientProfile(host,Port,password,profileId);
    internal static VpnProfile BuildClientProfile(string host,int port,string password,string profileId)
    {
        string authority=host.Contains(':')?"["+host+"]":host;
        string source="ss://"+Convert.ToBase64String(Encoding.UTF8.GetBytes(Method+":"+password)).TrimEnd('=').Replace('+','-').Replace('/','_')+"@"+authority+":"+port+"#NOVA%20VPS";
        return new VpnProfile{Id=profileId,Name="NOVA VPS",ProviderName="NOVA VPS",Protocol="shadowsocks",Server=host,Port=port,Method=Method,Password=password,Transport="tcp",Source=source};
    }
    // Contains server/client credentials. Send only over the pinned SSH stdin;
    // never use this as a command line, diagnostic message or ordinary export log.
    public string ExportRequestJson()=>Serializer().Serialize(new Dictionary<string,object>{
        {"schema",1},{"installation",InstallationId},{"operation",OperationId},{"expected_revision",ExpectedRevision},{"action",Action},
        {"active",Active},{"enabled",Enabled},{"architecture",Architecture},{"binary",Convert.ToBase64String(binary)},
        {"binary_sha256",BinarySha256},{"config",Convert.ToBase64String(Encoding.UTF8.GetBytes(config))},{"config_sha256",ConfigSha256}
    });
    public static string RollbackRequestJson(string installation,string operation,int expectedRevision,bool active,bool enabled)
    {
        ValidateIds(installation,operation,expectedRevision);
        return Serializer().Serialize(new Dictionary<string,object>{{"schema",1},{"installation",installation},{"operation",operation},{"expected_revision",expectedRevision},{"action","rollback"},{"active",active},{"enabled",enabled}});
    }
    public static string RemovalRequestJson(string installation,string operation,int expectedRevision)
    {
        ValidateIds(installation,operation,expectedRevision);
        if(expectedRevision==0)throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        return Serializer().Serialize(new Dictionary<string,object>{{"schema",1},{"installation",installation},{"operation",operation},{"expected_revision",expectedRevision},{"action","remove"}});
    }
    public static string AgentScript
    {
        get {using(var stream=typeof(VpsDeploymentDraft).Assembly.GetManifestResourceStream("NovaVpn.Assets.vps-managed-agent.py")) {
            if(stream==null)throw new InvalidDataException("Серверный обработчик отсутствует в сборке.");
            using(var reader=new StreamReader(stream,new UTF8Encoding(false,true)))return reader.ReadToEnd();
        }}
    }
    static void ValidateIds(string installation,string operation,int revision)
    {
        if(installation==null||operation==null||!Regex.IsMatch(installation,@"\A[0-9a-f]{32}\z")||!Regex.IsMatch(operation,@"\A[0-9a-f]{32}\z"))throw new ArgumentException("Некорректный идентификатор установки или операции.");
        if(revision<0||revision==int.MaxValue)throw new ArgumentOutOfRangeException(nameof(revision));
    }
    static JavaScriptSerializer Serializer()=>new JavaScriptSerializer{MaxJsonLength=96*1024*1024,RecursionLimit=32};
    static string Sha256(byte[] bytes){using(var hash=SHA256.Create())return String.Concat(hash.ComputeHash(bytes).Select(v=>v.ToString("x2")));}
    public override string ToString()=>"NOVA VPS: подготовленный пакет";
}

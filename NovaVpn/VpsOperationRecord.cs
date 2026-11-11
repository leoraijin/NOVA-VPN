using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;

namespace NovaVpn;

[DataContract]
public sealed class VpsOperationRecord
{
    [DataMember(IsRequired=true)] public int Schema { get; private set; }
    [DataMember(IsRequired=true)] public string Host { get; private set; }
    [DataMember(IsRequired=true)] public int SshPort { get; private set; }
    [DataMember(IsRequired=true)] public string User { get; private set; }
    [DataMember(IsRequired=true)] public string HostPublicKey { get; private set; }
    [DataMember(IsRequired=true)] public string TargetIdentityKey { get; private set; }
    [DataMember(IsRequired=true)] public string InstallationId { get; private set; }
    [DataMember(IsRequired=true)] public string OperationId { get; private set; }
    [DataMember(IsRequired=true)] public string RequestSha256 { get; private set; }
    [DataMember(IsRequired=true)] public string BinarySha256 { get; private set; }
    [DataMember(IsRequired=true)] public string ConfigSha256 { get; private set; }
    [DataMember(IsRequired=true)] public int ExpectedRevision { get; private set; }
    [DataMember(IsRequired=true)] public string Architecture { get; private set; }
    [DataMember(IsRequired=true)] public string Phase { get; private set; }
    [DataMember(IsRequired=true)] public string ProfileId { get; private set; }
    [DataMember(IsRequired=true)] public int VpnPort { get; private set; }
    [DataMember(IsRequired=true)] public string Password { get; private set; }
    [DataMember(EmitDefaultValue=false)] public string Action { get; private set; }
    [DataMember(EmitDefaultValue=false)] public string GenerationId { get; private set; }
    [DataMember(EmitDefaultValue=false)] public string RemovalOperationId { get; private set; }
    public string PayloadGeneration => GenerationId??OperationId;
    public string EffectiveAction => Action??"deploy";
    public bool IsTerminal => Phase=="committed"||Phase=="rolled-back"||Phase=="not-sent"||Phase=="removed";
    public VpsSshTarget Target => new VpsSshTarget(Host,SshPort,User,HostPublicKey);
    internal static VpsOperationRecord Prepared(VpsSshTarget target,VpsDeploymentDraft draft,VpsAgentFrame frame)
    {
        if(draft==null||draft.Action!="deploy")throw new ArgumentException("Нужен обычный пакет установки VPS.");
        return BuildPrepared(target,draft,frame);
    }
    internal static VpsOperationRecord PreparedReinstallation(VpsSshTarget target,VpsDeploymentDraft draft,VpsAgentFrame frame,VpsOperationRecord removed,VpsServerStatus status)
    {
        if(draft==null||removed==null||status==null||draft.Action!="reinstall"||!status.MatchesRemoval(removed)||draft.BaselineRemovalOperationId!=removed.OperationId||draft.ExpectedRevision!=status.Revision||draft.CreateClientProfile().Id!=removed.ProfileId)throw new InvalidOperationException("Исходное удаление не подтверждает повторную установку VPS.");
        var record=BuildPrepared(target,draft,frame);record.Schema=4;record.Action="reinstall";record.RemovalOperationId=removed.OperationId;record.Validate();return record;
    }
    static VpsOperationRecord BuildPrepared(VpsSshTarget target,VpsDeploymentDraft draft,VpsAgentFrame frame)
    {
        if(target==null||draft==null||frame==null||target.IdentityKey!=draft.TargetIdentityKey||target.IdentityKey!=frame.TargetIdentityKey||frame.Action!=draft.Action||!frame.IsForDraft(draft)||frame.OperationId!=draft.OperationId||frame.InstallationId!=draft.InstallationId||frame.ExpectedRevision!=draft.ExpectedRevision)throw new ArgumentException("Пакет и операция VPS не совпадают.");
        var profile=draft.CreateClientProfile();
        var record=new VpsOperationRecord{Schema=1,Host=target.Host,SshPort=target.Port,User=target.User,HostPublicKey=target.HostPublicKey,TargetIdentityKey=target.IdentityKey,InstallationId=draft.InstallationId,OperationId=draft.OperationId,RequestSha256=frame.RequestSha256,BinarySha256=draft.BinarySha256,ConfigSha256=draft.ConfigSha256,ExpectedRevision=draft.ExpectedRevision,Architecture=draft.Architecture,Phase="prepared",ProfileId=profile.Id,VpnPort=profile.Port,Password=profile.Password};
        record.Validate();return record;
    }
    internal static VpsOperationRecord PreparedRollback(VpsOperationRecord current,VpsOperationRecord previous,VpsServerStatus status,VpsAgentFrame frame)
    {
        if(current==null||previous==null||status==null||frame==null)throw new ArgumentNullException("VPS rollback context");
        current.Validate();previous.Validate();
        if(current.Phase!="committed"||previous.Phase!="committed"||!status.MatchesCandidate(current)||!status.PreviousAvailable||status.PreviousGeneration!=previous.PayloadGeneration||status.Revision<current.ExpectedRevision+1||status.Revision<previous.ExpectedRevision+1||previous.TargetIdentityKey!=current.TargetIdentityKey||previous.InstallationId!=current.InstallationId||previous.ProfileId!=current.ProfileId||frame.Action!="rollback"||frame.TargetIdentityKey!=current.TargetIdentityKey||frame.InstallationId!=current.InstallationId||frame.ExpectedRevision!=status.Revision||frame.OperationId==current.OperationId||frame.OperationId==previous.OperationId)throw new InvalidOperationException("Предыдущая версия VPS не подтверждена для отката.");
        var record=(VpsOperationRecord)previous.MemberwiseClone();
        record.Schema=2;record.Action="rollback";record.GenerationId=previous.PayloadGeneration;record.RemovalOperationId=null;
        record.OperationId=frame.OperationId;record.RequestSha256=frame.RequestSha256;record.ExpectedRevision=status.Revision;record.Phase="prepared";
        record.Validate();return record;
    }
    internal static VpsOperationRecord PreparedRemoval(VpsOperationRecord current,VpsServerStatus status,VpsAgentFrame frame)
    {
        if(current==null||status==null||frame==null)throw new ArgumentNullException("VPS removal context");
        current.Validate();
        if(current.Phase!="committed"||!status.MatchesCandidate(current)||status.HasPendingOperation||status.Revision<current.ExpectedRevision+1||frame.Action!="remove"||frame.TargetIdentityKey!=current.TargetIdentityKey||frame.InstallationId!=current.InstallationId||frame.ExpectedRevision!=status.Revision||frame.OperationId==current.OperationId)throw new InvalidOperationException("Текущая установка VPS не подтверждена для удаления.");
        var record=(VpsOperationRecord)current.MemberwiseClone();
        record.Schema=3;record.Action="remove";record.GenerationId=current.PayloadGeneration;record.RemovalOperationId=null;
        record.OperationId=frame.OperationId;record.RequestSha256=frame.RequestSha256;record.ExpectedRevision=status.Revision;record.Phase="prepared";
        record.Validate();return record;
    }
    internal VpsOperationRecord WithPhase(string phase){var copy=(VpsOperationRecord)MemberwiseClone();copy.Phase=phase;copy.Validate();return copy;}
    internal void Validate()
    {
        if((Schema!=1&&Schema!=2&&Schema!=3&&Schema!=4)||Schema==1&&(Action!=null||GenerationId!=null)||Schema==2&&(Action!="rollback"||!Hex(GenerationId,32)||GenerationId==OperationId)||Schema==3&&(Action!="remove"||!Hex(GenerationId,32)||GenerationId==OperationId||ExpectedRevision==0||!new[]{"prepared","dispatched","uncertain","not-sent","removed"}.ContainsOrdinal(Phase))||Schema==4&&(Action!="reinstall"||GenerationId!=null||ExpectedRevision<2||!Hex(RemovalOperationId,32)||RemovalOperationId==OperationId)||Schema!=4&&RemovalOperationId!=null||Schema!=3&&Phase=="removed"||Target.IdentityKey!=TargetIdentityKey||Host!=Target.Host||!Hex(InstallationId,32)||!Hex(OperationId,32)||!Hex(ProfileId,32)||!Hex(RequestSha256,64)||!Hex(BinarySha256,64)||!Hex(ConfigSha256,64)||ExpectedRevision<0||ExpectedRevision==int.MaxValue||VpnPort<1||VpnPort>65535||(Architecture!="x86_64"&&Architecture!="aarch64")||!new[]{"prepared","dispatched","uncertain","committed","rolled-back","not-sent","removed"}.ContainsOrdinal(Phase))throw new InvalidDataException("Некорректная запись операции VPS.");
        byte[] secret;
        try{secret=Convert.FromBase64String(Password??"");}catch(FormatException){throw new InvalidDataException("Повреждён ключ VPS.");}
        try{if(secret.Length!=32||Convert.ToBase64String(secret)!=Password)throw new InvalidDataException("Повреждён ключ VPS.");}finally{Array.Clear(secret,0,secret.Length);}
        using(var sha=SHA256.Create())if(String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(VpsDeploymentDraft.BuildServerConfig(Password,VpnPort))).Select(v=>v.ToString("x2")))!=ConfigSha256)throw new InvalidDataException("Ключ VPS не соответствует сохранённой конфигурации.");
    }
    internal static bool SameOperation(VpsOperationRecord a,VpsOperationRecord b)=>a!=null&&b!=null&&a.Schema==b.Schema&&a.EffectiveAction==b.EffectiveAction&&a.PayloadGeneration==b.PayloadGeneration&&a.RemovalOperationId==b.RemovalOperationId&&a.TargetIdentityKey==b.TargetIdentityKey&&a.InstallationId==b.InstallationId&&a.OperationId==b.OperationId&&a.RequestSha256==b.RequestSha256&&a.ConfigSha256==b.ConfigSha256&&a.BinarySha256==b.BinarySha256&&a.ExpectedRevision==b.ExpectedRevision&&a.ProfileId==b.ProfileId&&a.Password==b.Password&&a.VpnPort==b.VpnPort&&a.Architecture==b.Architecture;
    static bool Hex(string value,int count)=>value!=null&&Regex.IsMatch(value,@"\A[0-9a-f]{"+count+@"}\z");
    // Detached candidate for an isolated traffic check; never implicitly saved/activated.
    public VpnProfile CreateCandidateProfile(){Validate();if(EffectiveAction=="remove")throw new InvalidOperationException("Удаление VPS не является профилем подключения.");return VpsDeploymentDraft.BuildClientProfile(Host,VpnPort,Password,ProfileId);}
    public override string ToString()=>"NOVA VPS: запись операции";
}
internal static class VpsRecordStrings
{
    internal static bool ContainsOrdinal(this string[] values,string value){foreach(string item in values)if(item==value)return true;return false;}
}

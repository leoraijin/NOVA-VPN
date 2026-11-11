using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NovaVpn;

// File/service observation only. No property claims Internet/VPN traffic health.
public sealed class VpsServerStatus
{
    public string TargetIdentityKey { get; private set; }
    public string InstallationId { get; private set; }
    public string State { get; private set; }
    public int Revision { get; private set; }
    public string Generation { get; private set; }
    public string PreviousGeneration { get; private set; }
    public string OperationId { get; private set; }
    public string RequestSha256 { get; private set; }
    public string Architecture { get; private set; }
    public string BinarySha256 { get; private set; }
    public string ConfigSha256 { get; private set; }
    public string JournalPhase { get; private set; }
    public string PendingOperationId { get; private set; }
    public string PendingRequestSha256 { get; private set; }
    public bool PreviousAvailable { get; private set; }
    public bool ServiceExists { get; private set; }
    public bool ServiceActive { get; private set; }
    public bool ServiceEnabled { get; private set; }
    public bool HasPendingOperation=>State=="pending";
    // A rollback can reuse an older generation with a newer operation ID/hash.
    // Match payload identity, not the historical receipt alone; still no traffic proof.
    public bool MatchesCandidate(VpsOperationRecord candidate)
    {
        if(candidate==null)return false;candidate.Validate();
        return State=="installed"&&TargetIdentityKey==candidate.TargetIdentityKey&&InstallationId==candidate.InstallationId&&Generation==candidate.PayloadGeneration&&Architecture==candidate.Architecture&&BinarySha256==candidate.BinarySha256&&ConfigSha256==candidate.ConfigSha256;
    }
    // Required before reusing a terminal removal as a new installation baseline.
    // An absent service alone cannot attest which installation operation finished.
    public bool MatchesRemoval(VpsOperationRecord record)
    {
        if(record==null)return false;record.Validate();
        return State=="removed"&&record.EffectiveAction=="remove"&&record.Phase=="removed"&&
            TargetIdentityKey==record.TargetIdentityKey&&InstallationId==record.InstallationId&&
            OperationId==record.OperationId&&RequestSha256==record.RequestSha256&&
            Revision==record.ExpectedRevision+1;
    }
    internal static VpsServerStatus Parse(string text,int exitCode,VpsAgentFrame frame)
    {
        try{return ParseCore(text,exitCode,frame);}catch(Exception error)when(error is ArgumentException||error is InvalidCastException||error is KeyNotFoundException||error is NullReferenceException||error is InvalidOperationException){throw new InvalidDataException("Некорректное состояние VPS.");}
    }
    static VpsServerStatus ParseCore(string text,int exitCode,VpsAgentFrame frame)
    {
        if(frame==null||frame.Action!="status"||exitCode!=0||text==null||text.Length>8192)throw new InvalidDataException("Состояние VPS не подтверждено.");
        var serializer=new JavaScriptSerializer{MaxJsonLength=8192,RecursionLimit=8};
        var outer=serializer.DeserializeObject(text) as Dictionary<string,object>;
        if(outer==null||outer.Count!=2||!(outer["schema"] is int)||!Equals(outer["schema"],1))throw new InvalidDataException("Неизвестный формат состояния VPS.");
        var values=outer["result"] as Dictionary<string,object>;
        string[] names={"active","architecture","binary_sha256","config_sha256","enabled","generation","installation","journal","kind","operation","pending_operation","pending_request_sha256","previous","previous_available","request_sha256","revision","schema","service_exists","state"};
        if(values==null||values.Count!=names.Length||names.Any(name=>!values.ContainsKey(name))||!(values["schema"] is int)||!Equals(values["schema"],1)||!Equals(values["kind"],"status")||!(values["revision"] is int))throw new InvalidDataException("Неизвестные поля состояния VPS.");
        foreach(string name in new[]{"active","enabled","previous_available","service_exists"})if(!(values[name] is bool))throw new InvalidDataException("Некорректные флаги VPS.");
        foreach(string name in names.Except(new[]{"active","enabled","previous_available","service_exists","revision","schema"}))if(!(values[name] is string))throw new InvalidDataException("Некорректные поля VPS.");
        var canonical=new SortedDictionary<string,object>(StringComparer.Ordinal){{"result",new SortedDictionary<string,object>(values,StringComparer.Ordinal)},{"schema",1}};
        if(serializer.Serialize(canonical)+"\n"!=text)throw new InvalidDataException("Неоднозначный ответ состояния VPS.");
        var result=new VpsServerStatus{TargetIdentityKey=frame.TargetIdentityKey,InstallationId=(string)values["installation"],State=(string)values["state"],Revision=(int)values["revision"],Generation=(string)values["generation"],PreviousGeneration=(string)values["previous"],OperationId=(string)values["operation"],RequestSha256=(string)values["request_sha256"],Architecture=(string)values["architecture"],BinarySha256=(string)values["binary_sha256"],ConfigSha256=(string)values["config_sha256"],JournalPhase=(string)values["journal"],PendingOperationId=(string)values["pending_operation"],PendingRequestSha256=(string)values["pending_request_sha256"],PreviousAvailable=(bool)values["previous_available"],ServiceExists=(bool)values["service_exists"],ServiceActive=(bool)values["active"],ServiceEnabled=(bool)values["enabled"]};
        if(result.InstallationId!=frame.InstallationId||result.Revision<0||!new[]{"absent","reserved","installed","pending","removed"}.Contains(result.State)||!new[]{"none","prepared","staged","unit-installed","switched","activated","rollback-started","pointer-restored","service-restored","committed","rolled-back","removed"}.Contains(result.JournalPhase))throw new InvalidDataException("Состояние другой или неизвестной установки VPS.");
        if(result.PreviousGeneration!=""&&result.PreviousGeneration==result.Generation)throw new InvalidDataException("Поколение VPS не может ссылаться само на себя.");
        bool pending=!new[]{"none","committed","rolled-back","removed"}.Contains(result.JournalPhase), current=result.Generation!="";
        foreach(string value in new[]{result.Generation,result.PreviousGeneration,result.OperationId,result.PendingOperationId})if(value!=""&&!Hex(value,32))throw new InvalidDataException("Некорректный идентификатор VPS.");
        foreach(string value in new[]{result.RequestSha256,result.PendingRequestSha256,result.BinarySha256,result.ConfigSha256})if(value!=""&&!Hex(value,64))throw new InvalidDataException("Некорректная контрольная сумма VPS.");
        if((result.State=="removed")!=(result.JournalPhase=="removed"))throw new InvalidDataException("Противоречивая запись удаления VPS.");
        if(result.State=="removed"){
            if(result.Revision<2||result.OperationId==""||result.RequestSha256==""||current||result.PreviousGeneration!=""||result.Architecture!=""||result.BinarySha256!=""||result.ConfigSha256!=""||result.PreviousAvailable||result.PendingOperationId!=""||result.PendingRequestSha256!=""||result.ServiceExists||result.ServiceActive||result.ServiceEnabled)throw new InvalidDataException("Неполное подтверждение удалённого VPS.");
            return result;
        }
        if(pending!=result.HasPendingOperation||pending!=(result.PendingOperationId!="")||pending!=(result.PendingRequestSha256!="")||(result.ServiceActive||result.ServiceEnabled)&&!result.ServiceExists||result.PreviousAvailable&&(result.PreviousGeneration==""||pending))throw new InvalidDataException("Противоречивое состояние VPS.");
        if(current){if(result.JournalPhase=="none"||result.Revision==0||result.OperationId==""||result.RequestSha256==""||result.BinarySha256==""||result.ConfigSha256==""||!new[]{"x86_64","aarch64"}.Contains(result.Architecture)||!result.ServiceExists||result.State=="absent"||result.State=="reserved")throw new InvalidDataException("Неполное текущее поколение VPS.");}
        else if(result.Revision!=0||result.OperationId!=""||result.RequestSha256!=""||result.BinarySha256!=""||result.ConfigSha256!=""||result.Architecture!=""||result.PreviousGeneration!=""||result.PreviousAvailable||result.State=="installed")throw new InvalidDataException("Противоречивое пустое поколение VPS.");
        if((result.State=="absent"||result.State=="reserved")&&result.ServiceExists||result.State=="absent"&&result.JournalPhase!="none"||result.State=="reserved"&&!new[]{"none","rolled-back"}.Contains(result.JournalPhase))throw new InvalidDataException("Противоречивое состояние службы VPS.");
        return result;
    }
    static bool Hex(string value,int count)=>Regex.IsMatch(value,@"\A[0-9a-f]{"+count+@"}\z");
    public override string ToString()=>"NOVA VPS: состояние файлов и службы";
}

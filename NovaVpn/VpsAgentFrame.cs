using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class VpsAgentFrame
{
    readonly byte[] source, request;
    readonly VpsDeploymentDraft origin;
    internal bool IsForDraft(VpsDeploymentDraft draft)=>ReferenceEquals(origin,draft);
    public string TargetIdentityKey { get; }
    public string InstallationId { get; }
    public string OperationId { get; }
    public string RequestSha256 { get; }
    public string Action { get; }
    public string ReceiptRequestSha256 { get; }
    public int? ExpectedRevision { get; }
    public int SourceByteCount => source.Length;
    public string BootstrapCode { get; }
    public string RemoteCommand => "/usr/bin/python3 -I -c '" + BootstrapCode.Replace("'", "'\\''") + "'";
    VpsAgentFrame(VpsSshTarget target,string json,VpsDeploymentDraft origin=null)
    {
        if(target==null)throw new ArgumentNullException(nameof(target));
        this.origin=origin;
        var serializer=new JavaScriptSerializer{MaxJsonLength=96*1024*1024};
        var values=(Dictionary<string,object>)serializer.DeserializeObject(json);
        // These requests are factory-generated flat objects with ASCII primitive
        // fields. Ordinal sorted compact JSON equals the agent's Python canonical form.
        if(values.Any(pair=>pair.Key.Any(c=>c>127)||pair.Value is string text&&text.Any(c=>c>127))||values.Values.Any(v=>!(v is string)&&!(v is int)&&!(v is bool)))throw new InvalidDataException("Некорректное содержимое пакета VPS.");
        InstallationId=(string)values["installation"];Action=(string)values["action"];
        OperationId=values.ContainsKey("operation")?(string)values["operation"]:null;
        ExpectedRevision=values.ContainsKey("expected_revision")?(int?)values["expected_revision"]:null;
        request=Encoding.UTF8.GetBytes(json);
        if(request.Length>96*1024*1024)throw new InvalidDataException("Пакет VPS слишком большой.");
        RequestSha256=Hash(Encoding.ASCII.GetBytes(serializer.Serialize(new SortedDictionary<string,object>(values,StringComparer.Ordinal))));
        ReceiptRequestSha256=values.ContainsKey("original_request_sha256")?(string)values["original_request_sha256"]:RequestSha256;
        source=new UTF8Encoding(false,true).GetBytes(VpsDeploymentDraft.AgentScript);
        if(source.Length==0||source.Length>128*1024)throw new InvalidDataException("Некорректный серверный обработчик.");
        TargetIdentityKey=target.IdentityKey;
        BootstrapCode="import hashlib,sys;s=sys.stdin.buffer.read("+source.Length.ToString(CultureInfo.InvariantCulture)+");(len(s)=="+source.Length.ToString(CultureInfo.InvariantCulture)+" and hashlib.sha256(s).hexdigest()==\""+Hash(source)+"\") or sys.exit(90);exec(compile(s,\"<nova-vps-agent>\",\"exec\"))";
    }
    public static VpsAgentFrame Deployment(VpsSshTarget target,VpsDeploymentDraft draft)
    {
        if(draft==null||target==null||draft.Action!="deploy"||draft.TargetIdentityKey!=target.IdentityKey)throw new ArgumentException("Пакет подготовлен для другого SSH-сервера.");
        return new VpsAgentFrame(target,draft.ExportRequestJson(),draft);
    }
    public static VpsAgentFrame Reinstallation(VpsSshTarget target,VpsDeploymentDraft draft)
    {
        if(draft==null||target==null||draft.Action!="reinstall"||draft.TargetIdentityKey!=target.IdentityKey)throw new ArgumentException("Пакет повторной установки подготовлен для другого сервера.");
        return new VpsAgentFrame(target,draft.ExportRequestJson(),draft);
    }
    public static VpsAgentFrame ReinstallationRecovery(VpsSshTarget target,string installation,string operation,int revision,string requestHash)
    {
        VpsDeploymentDraft.RemovalRequestJson(installation,operation,revision);
        if(requestHash==null||!Regex.IsMatch(requestHash,@"\A[0-9a-f]{64}\z"))throw new ArgumentException("Некорректный хэш операции повторной установки.");
        return new VpsAgentFrame(target,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"schema",1},{"installation",installation},{"action","recover-reinstall"},{"operation",operation},{"expected_revision",revision},{"original_request_sha256",requestHash}}));
    }
    public static VpsAgentFrame Rollback(VpsSshTarget target,string installation,string operation,int revision,bool active,bool enabled)
        =>new VpsAgentFrame(target,VpsDeploymentDraft.RollbackRequestJson(installation,operation,revision,active,enabled));
    public static VpsAgentFrame Removal(VpsSshTarget target,string installation,string operation,int revision)
        =>new VpsAgentFrame(target,VpsDeploymentDraft.RemovalRequestJson(installation,operation,revision));
    public static VpsAgentFrame RemovalRecovery(VpsSshTarget target,string installation,string operation,int revision,string requestHash)
    {
        var original=Removal(target,installation,operation,revision);
        if(requestHash!=original.RequestSha256)throw new ArgumentException("Хэш удаления не соответствует сохранённой операции.");
        return new VpsAgentFrame(target,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"schema",1},{"installation",installation},{"action","recover-remove"},{"operation",operation},{"expected_revision",revision},{"original_request_sha256",requestHash}}));
    }
    public static VpsAgentFrame RemovalRecovery(VpsSshTarget target,string installation)=>Query(target,installation,"recover-remove");
    public static VpsAgentFrame Recovery(VpsSshTarget target,string installation,string operation,int revision,string requestHash)
    {
        VpsDeploymentDraft.RollbackRequestJson(installation,operation,revision,false,false);
        if(requestHash==null||!Regex.IsMatch(requestHash,@"\A[0-9a-f]{64}\z"))throw new ArgumentException("Некорректный хэш операции VPS.");
        return new VpsAgentFrame(target,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"schema",1},{"installation",installation},{"action","recover-operation"},{"operation",operation},{"expected_revision",revision},{"original_request_sha256",requestHash}}));
    }
    public static VpsAgentFrame Recovery(VpsSshTarget target,string installation)=>Query(target,installation,"recover");
    public static VpsAgentFrame Status(VpsSshTarget target,string installation)=>Query(target,installation,"status");
    static VpsAgentFrame Query(VpsSshTarget target,string installation,string action)
    {
        if(installation==null||!Regex.IsMatch(installation,@"\A[0-9a-f]{32}\z"))throw new ArgumentException("Некорректный идентификатор установки.");
        return new VpsAgentFrame(target,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"schema",1},{"installation",installation},{"action",action}}));
    }
    public async Task WriteAsync(Stream destination,CancellationToken cancellation)
    {
        if(destination==null)throw new ArgumentNullException(nameof(destination));
        foreach(byte[] buffer in new[]{source,request})
            for(int offset=0;offset<buffer.Length;offset+=Math.Min(32768,buffer.Length-offset)) {
                cancellation.ThrowIfCancellationRequested();
                await destination.WriteAsync(buffer,offset,Math.Min(32768,buffer.Length-offset),cancellation).ConfigureAwait(false);
            }
    }
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return String.Concat(sha.ComputeHash(bytes).Select(v=>v.ToString("x2")));}
    public override string ToString()=>"NOVA VPS: защищённый запрос";
}

public sealed class VpsAgentResponse
{
    public string TargetIdentityKey { get; private set; }
    public string State { get; private set; }
    public VpsServerStatus ServerStatus { get; private set; }
    public string OperationId { get; private set; }
    public string RequestSha256 { get; private set; }
    public string ErrorCode { get; private set; }
    public int Revision { get; private set; }
    // A terminal operation receipt is not current server health or reachability.
    public bool IsTerminalReceipt => State=="committed"||State=="rolled-back"||State=="removed"||State=="not-sent";
    public bool Matches(VpsAgentFrame request)=>request!=null&&IsTerminalReceipt&&TargetIdentityKey==request.TargetIdentityKey&&OperationId==request.OperationId&&RequestSha256==request.ReceiptRequestSha256;
    public static VpsAgentResponse Parse(string text,int exitCode,VpsAgentFrame request)
    {
        if(request==null)throw new ArgumentNullException(nameof(request));
        if(text==null||text.Length>8192)throw new InvalidDataException("Некорректный ответ VPS.");
        // The reviewed agent emits canonical ASCII JSON. Requiring that exact wire
        // grammar rejects duplicate keys, escaped aliases, extra fields and banners.
        var error=Regex.Match(text,@"\A\{""error"":""([a-z][a-z0-9_]{0,63})"",""schema"":1\}\n\z",RegexOptions.CultureInvariant);
        if(error.Success&&(exitCode==2||exitCode==3))return new VpsAgentResponse{TargetIdentityKey=request.TargetIdentityKey,ErrorCode=error.Groups[1].Value};
        if(request.Action=="status")return new VpsAgentResponse{TargetIdentityKey=request.TargetIdentityKey,ServerStatus=VpsServerStatus.Parse(text,exitCode,request)};
        var receipt=Regex.Match(text,@"\A\{""result"":\{""operation"":""([0-9a-f]{32})"",""request_sha256"":""([0-9a-f]{64})"",""revision"":(0|[1-9][0-9]{0,9}),""schema"":1,""state"":""(committed|rolled-back|removed|not-sent)""\},""schema"":1\}\n\z",RegexOptions.CultureInvariant);
        int revision;
        if(exitCode==0&&receipt.Success&&Int32.TryParse(receipt.Groups[3].Value,NumberStyles.None,CultureInfo.InvariantCulture,out revision)) {
            var result=new VpsAgentResponse{TargetIdentityKey=request.TargetIdentityKey,OperationId=receipt.Groups[1].Value,RequestSha256=receipt.Groups[2].Value,Revision=revision,State=receipt.Groups[4].Value};
            bool removal=request.Action=="remove"||request.Action=="recover-remove";
            if((result.State=="removed"||result.State=="not-sent")!=removal)throw new InvalidDataException("Квитанция не соответствует действию VPS.");
            if((result.State=="committed"||result.State=="removed")&&revision==0)throw new InvalidDataException("Некорректная ревизия VPS.");
            if(request.ExpectedRevision.HasValue&&revision!=((result.State=="committed"||result.State=="removed")?request.ExpectedRevision.Value+1:request.ExpectedRevision.Value))throw new InvalidDataException("Ревизия VPS не соответствует запросу.");
            if((request.Action!="recover"&&request.Action!="recover-remove"||request.ExpectedRevision.HasValue)&&!result.Matches(request))throw new InvalidDataException("Ответ VPS относится к другой операции.");
            return result;
        }
        var idle=Regex.Match(text,@"\A\{""result"":\{""revision"":(0|[1-9][0-9]{0,9}),""state"":""(idle|absent)""\},""schema"":1\}\n\z",RegexOptions.CultureInvariant);
        if(exitCode==0&&request.Action=="recover"&&idle.Success&&Int32.TryParse(idle.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out revision)&&(idle.Groups[2].Value!="absent"||revision==0))return new VpsAgentResponse{TargetIdentityKey=request.TargetIdentityKey,Revision=revision,State=idle.Groups[2].Value};
        throw new InvalidDataException("Состояние установки VPS не подтверждено.");
    }
    public override string ToString()=>"NOVA VPS: ответ обработчика";
}

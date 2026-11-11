using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NovaVpn;

// Dedicated current-user store. Never silently reset/fall back after corruption.
public sealed class VpsOperationStore : IDisposable
{
    const int Limit=64*1024, MaximumHistory=512;
    static readonly byte[] Marker=Encoding.ASCII.GetBytes("NOVA-VPS-STORE/1\n");
    static readonly byte[] Entropy=Encoding.UTF8.GetBytes("NOVA-VPS-operation-store/1");
    readonly string directory,path,next;
    readonly Action<string> checkpoint;
    FileStream lease;
    readonly object sync=new object();
    int observedRevision=-1;
    public VpsOperationStore(string directory):this(directory,null){}
    internal VpsOperationStore(string directory,Action<string> checkpoint)
    {
        this.directory=Path.GetFullPath(directory);this.checkpoint=checkpoint??(_=>{});
        PrivateRuntimeFiles.PrepareDirectory(this.directory);
        path=Path.Combine(this.directory,"operation.bin");next=Path.Combine(this.directory,"operation.next");
        lease=OpenPrivate(Path.Combine(this.directory,"operation.lock"),FileMode.OpenOrCreate,FileOptions.None);
    }
    public VpsOperationRecord Read(){lock(sync)return ReadCore();}
    VpsOperationRecord ReadCore()
    {
        Open();var current=ReadFile(path);var interrupted=ReadFile(next);
        var candidate=PendingCandidate(directory,current,interrupted);
        if(interrupted==null){
            if(current==null&&Initialized())throw new InvalidDataException("Ранее сохранённая запись VPS отсутствует.");
            if(current!=null)Initialize();return current;
        }
        Promote();var recovered=ReadFile(path)??throw new InvalidDataException("Запись VPS не сохранена.");Initialize();return recovered;
    }
    static VpsOperationRecord PendingCandidate(string directory,VpsOperationRecord current,VpsOperationRecord interrupted)
    {
        if(interrupted==null)return current;
        if(current==null){if(interrupted.Phase!="prepared")throw new InvalidDataException("Неполная история операции VPS.");}
        else if(VpsOperationRecord.SameOperation(current,interrupted)){
            if(!Transition(current.Phase,interrupted.Phase))throw new InvalidDataException("Противоречивое состояние VPS.");
        }else{
            if(!current.IsTerminal||current.Phase=="removed"&&(interrupted.EffectiveAction!="reinstall"||interrupted.RemovalOperationId!=current.OperationId)||interrupted.Phase!="prepared"||current.TargetIdentityKey!=interrupted.TargetIdentityKey||current.InstallationId!=interrupted.InstallationId||current.ProfileId!=interrupted.ProfileId||current.OperationId==interrupted.OperationId||interrupted.ExpectedRevision<KnownRevision(current)||File.Exists(Path.Combine(directory,"history-"+interrupted.OperationId+".bin")))throw new InvalidDataException("Конфликт незавершённых операций VPS.");
            if(!Equal(current,ReadFile(Path.Combine(directory,"history-"+current.OperationId+".bin"))))throw new InvalidDataException("Предыдущая запись VPS не сохранена.");
        }
        return interrupted;
    }
    // Inventory reads never create files, promote .next, repair markers or change ACLs.
    public static VpsSavedTarget ReadSavedTarget(string directory)
    {
        directory=Path.GetFullPath(directory);CheckInventoryPath(directory);
        using(var held=OpenPrivate(Path.Combine(directory,"operation.lock"),FileMode.Open,FileOptions.None)){
            var current=ReadFile(Path.Combine(directory,"operation.bin"));var next=ReadFile(Path.Combine(directory,"operation.next"));
            var record=PendingCandidate(directory,current,next);
            if(record==null){if(Initialized(held))throw new InvalidDataException("Ранее сохранённая запись VPS отсутствует.");return null;}
            if(Path.GetFileName(directory)!=record.TargetIdentityKey)throw new InvalidDataException("Запись VPS находится в каталоге другого сервера.");
            return new VpsSavedTarget(record.Target,record.VpnPort,record.Phase,next!=null);
        }
    }
    internal static void CheckInventoryPath(string directory)
    {
        for(var parent=new DirectoryInfo(Path.GetFullPath(directory));parent!=null;parent=parent.Parent)
            if(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Каталог сохранённых VPS не должен быть ссылкой.");
    }
    public VpsOperationRecord Prepare(VpsSshTarget target,VpsDeploymentDraft draft,VpsAgentFrame frame){lock(sync)return PrepareCore(target,draft,frame);}
    VpsOperationRecord PrepareCore(VpsSshTarget target,VpsDeploymentDraft draft,VpsAgentFrame frame)
    {
        Open();return SavePrepared(VpsOperationRecord.Prepared(target,draft,frame));
    }
    VpsOperationRecord SavePrepared(VpsOperationRecord record)
    {
        var current=Read();
        if(current!=null) {
            if(!current.IsTerminal||current.Phase=="removed"&&(record.EffectiveAction!="reinstall"||record.RemovalOperationId!=current.OperationId))throw new InvalidOperationException("Сначала восстановите операцию; удалённую установку нельзя заменить этой историей.");
            if(current.TargetIdentityKey!=record.TargetIdentityKey||current.InstallationId!=record.InstallationId||current.OperationId==record.OperationId)throw new InvalidOperationException("Хранилище принадлежит другой установке или операция уже использована.");
            if(current.ProfileId!=record.ProfileId)throw new InvalidOperationException("Обновление VPS должно сохранить идентификатор существующего профиля.");
            if(record.ExpectedRevision<Math.Max(KnownRevision(current),observedRevision))throw new InvalidOperationException("Состояние VPS устарело для новой операции.");
            Archive(current);
        }
        if(File.Exists(HistoryPath(record.OperationId)))throw new InvalidOperationException("Идентификатор операции VPS уже использован.");
        Save(record);return ReadRequired();
    }
    public VpsOperationRecord PrepareRollback(VpsServerStatus status,VpsAgentFrame frame)
    {
        lock(sync){
            Open();var current=FindCommittedCandidate(status)??throw new InvalidOperationException("Текущая конфигурация VPS не подтверждена.");
            if(!status.PreviousAvailable||String.IsNullOrEmpty(status.PreviousGeneration))throw new InvalidOperationException("Предыдущая версия VPS недоступна.");
            var previous=ReadFile(HistoryPath(status.PreviousGeneration))??throw new InvalidOperationException("Ключ предыдущей версии VPS отсутствует в локальной истории.");
            return SavePrepared(VpsOperationRecord.PreparedRollback(current,previous,status,frame));
        }
    }
    public VpsOperationRecord FindRemovedBase(VpsServerStatus status)
    {
        lock(sync){
            var latest=Read();if(latest==null||!latest.IsTerminal||status==null||status.Revision<Math.Max(KnownRevision(latest),observedRevision))return null;
            var removed=latest.Phase=="removed"?latest:latest.EffectiveAction=="reinstall"&&(latest.Phase=="rolled-back"||latest.Phase=="not-sent")?ReadFile(HistoryPath(latest.RemovalOperationId)):null;
            if(removed==null||removed.ProfileId!=latest.ProfileId||removed.TargetIdentityKey!=latest.TargetIdentityKey||removed.InstallationId!=latest.InstallationId||!status.MatchesRemoval(removed))return null;
            observedRevision=status.Revision;return removed;
        }
    }
    public VpsOperationRecord ConfirmRemoved(VpsServerStatus status)
    {
        lock(sync)return FindRemovedBase(status)??throw new InvalidOperationException("Состояние сервера не подтверждает сохранённое удаление VPS.");
    }
    public VpsOperationRecord PrepareReinstallation(VpsSshTarget target,VpsDeploymentDraft draft,VpsAgentFrame frame,VpsServerStatus status)
    {
        lock(sync){
            var removed=FindRemovedBase(status)??throw new InvalidOperationException("Удалённая установка VPS не подтверждена.");
            return SavePrepared(VpsOperationRecord.PreparedReinstallation(target,draft,frame,removed,status));
        }
    }
    public VpsOperationRecord PrepareRemoval(VpsServerStatus status,VpsAgentFrame frame)
    {
        lock(sync){
            Open();var current=FindCommittedCandidate(status)??throw new InvalidOperationException("Текущая установка VPS не подтверждена.");
            return SavePrepared(VpsOperationRecord.PreparedRemoval(current,status,frame));
        }
    }
    // Resolves a stored key for observed current payload; this is not traffic proof.
    public VpsOperationRecord FindCommittedCandidate(VpsServerStatus status)
    {
        lock(sync){
            var latest=Read();
            if(latest==null||!latest.IsTerminal||status==null||status.State!="installed"||status.HasPendingOperation||status.TargetIdentityKey!=latest.TargetIdentityKey||status.InstallationId!=latest.InstallationId)return null;
            int knownRevision=KnownRevision(latest);
            if(status.Revision<Math.Max(knownRevision,observedRevision))return null;
            observedRevision=status.Revision;
            var candidate=latest.PayloadGeneration==status.Generation&&latest.Phase=="committed"?latest:ReadFile(HistoryPath(status.Generation));
            if(candidate==null||candidate.Phase!="committed"||candidate.ProfileId!=latest.ProfileId||!status.MatchesCandidate(candidate))return null;
            return candidate;
        }
    }
    // Locate only a committed base belonging to this slot/installation. Local
    // aliases/proxy policy are merged separately and are never inferred here.
    public VpsOperationRecord FindCommittedProfileBase(VpnProfile profile)
    {
        lock(sync){
            var latest=Read();if(profile==null||latest==null)return null;
            if(profile.Id!=latest.ProfileId)throw new InvalidOperationException("Профиль относится к другой установке VPS.");
            var paths=Directory.EnumerateFiles(directory,"history-*.bin").Take(MaximumHistory+1).ToArray();
            if(paths.Length>MaximumHistory)throw new InvalidDataException("История VPS превышает допустимый размер.");
            var records=new[]{latest}.Concat(paths.OrderBy(p=>p,StringComparer.Ordinal).Select(ReadFile));
            foreach(var record in records){
                if(record==null||record.TargetIdentityKey!=latest.TargetIdentityKey||record.InstallationId!=latest.InstallationId||record.ProfileId!=latest.ProfileId)throw new InvalidDataException("История VPS относится к другой установке.");
                if(record.Phase=="committed"&&VpsProfilePreparation.SameBase(profile,record.CreateCandidateProfile()))return record;
            }
            throw new InvalidOperationException("Исходные параметры профиля не найдены в подтверждённой истории VPS.");
        }
    }
    public VpsOperationRecord CancelPrepared(string operation)
    {
        lock(sync){
            // ReadCore promotes an interrupted durable dispatch marker first.
            // Only an intent that is still prepared may be cancelled locally.
            var current=ReadRequired();
            if(current.OperationId!=operation||current.Phase!="prepared")throw new InvalidOperationException("Операция уже могла быть отправлена. Требуется проверка журнала сервера.");
            return Change(operation,"not-sent");
        }
    }
    public VpsOperationRecord MarkDispatched(string operation){lock(sync)return Change(operation,"dispatched");}
    public VpsOperationRecord MarkUncertain(string operation){lock(sync)return Change(operation,"uncertain");}
    public VpsOperationRecord AcceptReceipt(VpsAgentResponse response){lock(sync)return AcceptReceiptCore(response);}
    VpsOperationRecord AcceptReceiptCore(VpsAgentResponse response)
    {
        Open();var current=ReadRequired();
        if(response==null||!response.IsTerminalReceipt||response.TargetIdentityKey!=current.TargetIdentityKey||response.OperationId!=current.OperationId||response.RequestSha256!=current.RequestSha256||(response.State=="removed"||response.State=="not-sent")!=(current.EffectiveAction=="remove")||response.Revision!=((response.State=="committed"||response.State=="removed")?current.ExpectedRevision+1:current.ExpectedRevision))throw new InvalidDataException("Квитанция не подтверждает эту операцию VPS.");
        return Change(current.OperationId,response.State);
    }
    VpsOperationRecord Change(string operation,string phase)
    {
        var current=ReadRequired();
        if(current.OperationId!=operation||!Transition(current.Phase,phase))throw new InvalidOperationException("Состояние операции VPS изменилось.");
        var changed=current.WithPhase(phase);Save(changed);return ReadRequired();
    }
    static int KnownRevision(VpsOperationRecord record)=>(record.Phase=="committed"||record.Phase=="removed")?record.ExpectedRevision+1:record.ExpectedRevision;
    static bool Transition(string before,string after)=>before==after||before=="prepared"&&(after=="dispatched"||after=="uncertain"||after=="committed"||after=="rolled-back"||after=="removed"||after=="not-sent")||(before=="dispatched"||before=="uncertain")&&(after=="uncertain"||after=="committed"||after=="rolled-back"||after=="removed"||after=="not-sent");
    VpsOperationRecord ReadRequired(){Open();return Read()??throw new InvalidOperationException("Запись операции VPS отсутствует.");}
    string HistoryPath(string operation)=>Path.Combine(directory,"history-"+operation+".bin");
    void Archive(VpsOperationRecord record)
    {
        string history=HistoryPath(record.OperationId);var existing=ReadFile(history);
        if(existing!=null){if(!Equal(existing,record))throw new InvalidDataException("История VPS противоречит текущей записи.");return;}
        if(Directory.EnumerateFiles(directory,"history-*.bin").Take(MaximumHistory).Count()>=MaximumHistory)throw new InvalidOperationException("История VPS заполнена. Нужен проверенный экспорт перед очисткой.");
        byte[] cipher=Encode(record);string pending=Path.Combine(directory,"history-"+record.OperationId+".next");
        // Unlike the operation intent, an incomplete history copy is reconstructible
        // from the still-current authenticated terminal record before replacement.
        using(var stream=OpenPrivate(pending,FileMode.OpenOrCreate,FileOptions.WriteThrough)){
            stream.SetLength(0);checkpoint("history-copy-open");stream.Write(cipher,0,cipher.Length);stream.Flush(true);
        }
        if(!Equal(record,ReadFile(pending)))throw new IOException("История VPS не подтверждена.");
        checkpoint("history-copy-flushed");File.Move(pending,history);
        if(!Equal(record,ReadFile(history)))throw new IOException("История VPS не подтверждена.");
        checkpoint("history-flushed");
    }
    void Save(VpsOperationRecord record)
    {
        record.Validate();byte[] cipher=Encode(record);
        using(var stream=OpenPrivate(next,FileMode.CreateNew,FileOptions.WriteThrough)){stream.Write(cipher,0,cipher.Length);stream.Flush(true);}
        if(!Equal(record,ReadFile(next)))throw new IOException("Запись VPS не подтверждена.");
        checkpoint("intent-flushed");Promote();
        if(!Equal(record,ReadFile(path)))throw new IOException("Запись VPS не подтверждена.");
        checkpoint("intent-readback");
    }
    void Promote(){if(File.Exists(path))File.Replace(next,path,null);else File.Move(next,path);checkpoint("intent-replaced");}
    static bool Equal(VpsOperationRecord a,VpsOperationRecord b)
    {
        if(a==null||b==null)return a==b;
        byte[] x=Serialize(a),y=Serialize(b);try{return x.SequenceEqual(y);}finally{Array.Clear(x,0,x.Length);Array.Clear(y,0,y.Length);}
    }
    static byte[] Serialize(VpsOperationRecord record){using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(VpsOperationRecord)).WriteObject(stream,record);if(stream.Length>Limit)throw new InvalidDataException("Запись VPS слишком большая.");return stream.ToArray();}}
    static byte[] Encode(VpsOperationRecord record){byte[] plain=Serialize(record);try{var cipher=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser);if(cipher.Length>Limit)throw new InvalidDataException("Запись VPS слишком большая.");return cipher;}finally{Array.Clear(plain,0,plain.Length);}}
    static VpsOperationRecord ReadFile(string file)
    {
        byte[] cipher;
        try{using(var stream=OpenPrivate(file,FileMode.Open,FileOptions.None)){
            if(stream.Length<16||stream.Length>Limit)throw new InvalidDataException("Некорректный размер записи VPS.");
            cipher=new byte[(int)stream.Length];int at=0;while(at<cipher.Length){int n=stream.Read(cipher,at,cipher.Length-at);if(n==0)throw new EndOfStreamException();at+=n;}
        }}catch(FileNotFoundException){return null;}
        byte[] plain=ProtectedData.Unprotect(cipher,Entropy,DataProtectionScope.CurrentUser);
        try{
            if(plain.Length==0||plain.Length>Limit)throw new InvalidDataException("Повреждена запись VPS.");
            VpsOperationRecord record;using(var stream=new MemoryStream(plain))record=(VpsOperationRecord)new DataContractJsonSerializer(typeof(VpsOperationRecord)).ReadObject(stream);
            if(record==null)throw new InvalidDataException("Пустая запись VPS.");record.Validate();
            byte[] canonical=Serialize(record);try{if(!canonical.SequenceEqual(plain))throw new InvalidDataException("Неоднозначный формат записи VPS.");}finally{Array.Clear(canonical,0,canonical.Length);}
            return record;
        }finally{Array.Clear(plain,0,plain.Length);}
    }
    bool Initialized()=>Initialized(lease);
    static bool Initialized(FileStream lease)
    {
        Check(lease);if(lease.Length==0)return false;
        if(lease.Length!=Marker.Length)throw new InvalidDataException("Повреждён признак хранилища VPS.");
        byte[] value=new byte[Marker.Length];lease.Position=0;int at=0;while(at<value.Length){int n=lease.Read(value,at,value.Length-at);if(n==0)throw new EndOfStreamException();at+=n;}
        if(!value.SequenceEqual(Marker))throw new InvalidDataException("Повреждён признак хранилища VPS.");return true;
    }
    void Initialize(){try{if(Initialized())return;}catch(InvalidDataException){}
        // Called only after an authenticated current/next record was read.
        lease.Position=0;lease.SetLength(0);lease.Write(Marker,0,Marker.Length);lease.Flush(true);
    }
    void Open(){if(lease==null)throw new ObjectDisposedException(nameof(VpsOperationStore));Check(lease);}

    static SecurityIdentifier User(){using(var identity=WindowsIdentity.GetCurrent())return identity.User;}
    static FileStream OpenPrivate(string file,FileMode mode,FileOptions options)
    {
        try{var attributes=File.GetAttributes(file);if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException("Запись VPS не должна быть ссылкой или каталогом.");}
        catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        var user=User();var security=new FileSecurity();security.SetOwner(user);security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,AccessControlType.Allow));
        var stream=new FileStream(file,mode,FileSystemRights.FullControl,FileShare.None,4096,options,security);
        try{
            Check(stream);var resolved=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(stream.SafeFileHandle,resolved,(uint)resolved.Capacity,0);
            if(length==0||length>=resolved.Capacity)throw new IOException("Не удалось проверить путь записи VPS.");
            string actual=resolved.ToString();if(actual.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))actual=@"\\"+actual.Substring(8);else if(actual.StartsWith(@"\\?\",StringComparison.Ordinal))actual=actual.Substring(4);
            if(!String.Equals(Path.GetFullPath(file),actual,StringComparison.OrdinalIgnoreCase))throw new IOException("Путь записи VPS перенаправлен.");
            return stream;
        }catch{stream.Dispose();throw;}
    }
    [StructLayout(LayoutKind.Sequential)]struct Information{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;public uint Volume,High,Low,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint count,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle file,out Information info);
    static void Check(FileStream stream)
    {
        Information info;if(!GetFileInformationByHandle(stream.SafeFileHandle,out info)||info.Links!=1||(info.Attributes&((uint)FileAttributes.ReparsePoint|(uint)FileAttributes.Directory))!=0)throw new IOException("Недоверенный файл записи VPS.");
        var security=stream.GetAccessControl();var user=User();string system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null).Value;
        if(!security.GetOwner(typeof(SecurityIdentifier)).Equals(user)||!security.AreAccessRulesProtected)throw new UnauthorizedAccessException("Недоверенный владелец записи VPS.");
        bool userFull=false,systemFull=false;
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier))){
            if(rule.AccessControlType!=AccessControlType.Allow||rule.FileSystemRights!=FileSystemRights.FullControl||rule.IdentityReference.Value!=user.Value&&rule.IdentityReference.Value!=system)throw new UnauthorizedAccessException("Недоверенные права записи VPS.");
            if(rule.IdentityReference.Value==user.Value)userFull=true;if(rule.IdentityReference.Value==system)systemFull=true;
        }
        if(!userFull||!systemFull)throw new UnauthorizedAccessException("Неполные права записи VPS.");
    }
    public void Dispose(){lock(sync){var owned=lease;lease=null;owned?.Dispose();}}
}

// Public inventory metadata deliberately excludes VPN credentials and source config.
public sealed class VpsSavedTarget
{
    public VpsSshTarget Target { get; }
    public int VpnPort { get; }
    public string Phase { get; }
    public bool HasInterruptedWrite { get; }
    public string Label=>Target.User+"@"+Target.Host+":"+Target.Port;
    internal VpsSavedTarget(VpsSshTarget target,int port,string phase,bool interrupted){Target=target;VpnPort=port;Phase=phase;HasInterruptedWrite=interrupted;}
    public override string ToString()=>Label;
}

using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace NovaVpn;

public sealed class VpsManagementWindow : Window
{
    readonly Brush inputBrush,borderBrush;
    readonly AppState state;
    readonly string corePath, storage, runtime;
    readonly bool preview;
    readonly Func<bool> canChange;
    readonly Action<AppState> persist;
    readonly CancellationToken lifetime;
    readonly Func<VpsSshTarget,string,string,CancellationToken,Task<VpsPreflightReport>> inspect;
    readonly Func<VpsSshTarget,VpsAgentFrame,string,string,CancellationToken,Task<VpsAgentResponse>> exchange;
    readonly TextBox host,sshPort,user,publicKey,identity,artifact,digest,vpnPort;
    readonly StackPanel fields;
    readonly ComboBox savedTargets;
    readonly Button loadSaved;
    readonly TextBlock status;
    readonly Button check,deploy,rollback,recover,save,remove,stop;
    internal Func<string,bool> ConfirmRemoval { get; set; }
    internal Func<string,bool> ConfirmReinstallation { get; set; }
    VpsOperationStore store;
    VpsOperationCoordinator coordinator;
    string sessionIdentity;
    VpsPreflightReport report;
    CancellationTokenSource operation;
    bool busy,closePending;
    public bool ProfilesChanged { get; private set; }

    public VpsManagementWindow(AppState state,string corePath,string storage,string runtime,VisualPreferences visual,bool preview,CancellationToken lifetime,Func<bool> canChange,Action<AppState> persist)
        :this(state,corePath,storage,runtime,visual,preview,lifetime,canChange,persist,
            (t,k,r,c)=>new VpsSshPreflightRunner().InspectAsync(t,k,r,c),
            (t,f,k,r,c)=>new VpsSshAgentRunner().ExecuteAsync(t,f,k,r,c)){}
    internal VpsManagementWindow(AppState state,string corePath,string storage,string runtime,VisualPreferences visual,bool preview,CancellationToken lifetime,Func<bool> canChange,Action<AppState> persist,
        Func<VpsSshTarget,string,string,CancellationToken,Task<VpsPreflightReport>> inspect,
        Func<VpsSshTarget,VpsAgentFrame,string,string,CancellationToken,Task<VpsAgentResponse>> exchange)
    {
        this.state=state??throw new ArgumentNullException(nameof(state));this.corePath=corePath;this.storage=Path.GetFullPath(storage);this.runtime=Path.GetFullPath(runtime);
        this.preview=preview;this.lifetime=lifetime;this.canChange=canChange??throw new ArgumentNullException(nameof(canChange));this.persist=persist??throw new ArgumentNullException(nameof(persist));
        this.inspect=inspect??throw new ArgumentNullException(nameof(inspect));this.exchange=exchange??throw new ArgumentNullException(nameof(exchange));
        var palette=ThemePalette.Create(visual??new VisualPreferences());inputBrush=new SolidColorBrush(palette.Input);borderBrush=new SolidColorBrush(palette.Border);
        Title="Мой VPS";Width=Math.Min(820,SystemParameters.WorkArea.Width);Height=Math.Min(850,SystemParameters.WorkArea.Height);MinWidth=Math.Min(540,Width);MinHeight=Math.Min(500,Height);
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(palette.Bg);Foreground=new SolidColorBrush(palette.Text);FontSize=12*(visual?.TextScale??1);
        var root=new DockPanel{Margin=new Thickness(20)};
        var bottom=new StackPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        status=new TextBlock{Text=preview?"Предпросмотр: операции отключены.":"Начните с проверки SSH. Подключение использует выбранный файл ключа; запрос пароля не открывается.",TextWrapping=TextWrapping.Wrap};bottom.Children.Add(status);
        var actions=new WrapPanel{Margin=new Thickness(0,10,0,0)};bottom.Children.Add(actions);
        Func<string,Button> button=label=>{var b=new Button{Content=label,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,7,7)};actions.Children.Add(b);return b;};
        ConfirmRemoval=message=>MessageBox.Show(this,message,"Удалить NOVA с VPS?",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
        ConfirmReinstallation=message=>MessageBox.Show(this,message,"Повторно установить NOVA?",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
        check=button("Проверить SSH");check.Click+=async delegate{await RunAsync(InspectAsync,"Проверяем SSH…");};
        deploy=button("Установить / обновить");deploy.Click+=async delegate{await RunAsync(DeployAsync,"Применяем пакет VPS…");};
        rollback=button("Вернуть предыдущую версию");rollback.Click+=async delegate{await RunAsync(RollbackAsync,"Возвращаем предыдущую версию VPS…");};
        recover=button("Восстановить операцию");recover.Click+=async delegate{await RunAsync(RecoverAsync,"Восстанавливаем операцию VPS…");};
        save=button("Проверить и сохранить профиль");save.Click+=async delegate{await RunAsync(SaveAsync,"Проверяем HTTPS через VPS…");};
        remove=button("Удалить NOVA с VPS");remove.Click+=async delegate{await RunAsync(RemoveAsync,"Подготавливаем удаление VPS…");};
        stop=button("Отменить");stop.Click+=delegate{operation?.Cancel();};
        var close=button("Закрыть");close.Click+=delegate{Close();};
        fields=new StackPanel();var scroll=new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(scroll);
        fields.Children.Add(new TextBlock{Text="VPN на своём сервере",FontSize=FontSize*1.7,FontWeight=FontWeights.SemiBold});
        Note("Linux x64 или ARM64 с systemd и доступом root. Установка создаёт отдельный сервис NOVA и меняет его конфигурацию. Для обновления нужна сохранённая на этом ПК история установки. Правила облачного и серверного брандмауэра настраиваются у провайдера.");
        fields.Children.Add(new TextBlock{Text="Сохранённые серверы",Margin=new Thickness(0,12,0,4)});
        savedTargets=new ComboBox{DisplayMemberPath="Label",Padding=new Thickness(7),MaxDropDownHeight=250};fields.Children.Add(savedTargets);
        loadSaved=new Button{Content="Обновить список",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,5,0,0)};fields.Children.Add(loadSaved);
        loadSaved.Click+=async delegate{await RunAsync(LoadSavedAsync,"Читаем сохранённые серверы…");};
        savedTargets.SelectionChanged+=delegate{if(!busy&&!preview&&savedTargets.SelectedItem is VpsSavedTarget chosen)SelectSaved(chosen);};
        host=Field("Адрес сервера","");sshPort=Field("Порт SSH","22");user=Field("Пользователь Linux","root");
        publicKey=Field("Публичный Ed25519-ключ сервера (ssh-ed25519 …)","");
        Note("Возьмите публичный ключ сервера в доверенной консоли провайдера. Он закрепляет подлинность SSH-соединения; автоматически принимать новый ключ клиент не будет.");
        identity=Field("Файл вашего закрытого SSH-ключа","");Browse(identity,"Выбрать SSH-ключ");
        artifact=Field("Серверное ядро sing-box для Linux","");Browse(artifact,"Выбрать серверное ядро");
        digest=Field("SHA-256 ядра из доверенного источника","");vpnPort=Field("Порт VPN","443");
        Note("Выберите распакованный Linux-бинарник нужной архитектуры и независимо полученную контрольную сумму. При обновлении меняется ключ VPN. Откат возвращает конфигурацию и ключ предыдущей версии. Профиль добавляется отдельной кнопкой только после успешной HTTPS-проверки; текущее подключение автоматически не переключается.");
        Note("«Восстановить операцию» отменяет ещё не отправленную подготовку локально; для уже отправленной операции проверяет журнал сервера и при необходимости возвращает его к согласованному состоянию. Потеря SSH-ответа или отмена не означает, что установка на сервере остановилась. Повторная установка до восстановления блокируется.");
        foreach(var field in new[]{host,sshPort,user,publicKey,identity})field.TextChanged+=delegate{report=null;Controls();};
        Content=root;Controls();
        Closing+=(s,e)=>{if(busy){e.Cancel=true;closePending=true;operation?.Cancel();status.Text="Ожидаем завершения операции. Состояние установки будет сохранено.";}};
        Closed+=delegate{store?.Dispose();operation?.Dispose();};
    }
    TextBox Field(string label,string value){fields.Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,4)});var box=new TextBox{Text=value,Padding=new Thickness(7),MaxLength=4096,Background=inputBrush,Foreground=Foreground,BorderBrush=borderBrush};fields.Children.Add(box);return box;}
    void Note(string text)=>fields.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,4)});
    void Browse(TextBox field,string title){var b=new Button{Content=title,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,5,0,0)};b.Click+=delegate{if(preview)return;var dialog=new OpenFileDialog{Title=title,CheckFileExists=true,Multiselect=false};if(dialog.ShowDialog(this)==true)field.Text=dialog.FileName;};fields.Children.Add(b);}
    VpsSshTarget Target(bool requireIdentity=true){int port;if(!Int32.TryParse(sshPort.Text,out port))throw new ArgumentException("Укажите порт SSH числом.");if(requireIdentity&&!File.Exists(identity.Text))throw new FileNotFoundException("Выберите существующий файл SSH-ключа.");return new VpsSshTarget(host.Text.Trim(),port,user.Text.Trim(),publicKey.Text.Trim());}
    void Session(VpsSshTarget target){if(store!=null&&sessionIdentity==target.IdentityKey)return;store?.Dispose();store=null;coordinator=null;sessionIdentity=null;store=new VpsOperationStore(Path.Combine(storage,target.IdentityKey));sessionIdentity=target.IdentityKey;coordinator=new VpsOperationCoordinator(store,(t,f,c)=>exchange(t,f,identity.Text,runtime,c));}
    void ReleaseSession(){store?.Dispose();store=null;coordinator=null;sessionIdentity=null;report=null;}
    async Task LoadSavedAsync(CancellationToken token)
    {
        ReleaseSession();int unavailable=0;
        var targets=await Task.Run(()=>{
            var found=new List<VpsSavedTarget>();VpsOperationStore.CheckInventoryPath(storage);
            if(!Directory.Exists(storage))return found;
            foreach(string directory in Directory.EnumerateDirectories(storage).OrderBy(p=>p,StringComparer.Ordinal)){
                token.ThrowIfCancellationRequested();if(!Regex.IsMatch(Path.GetFileName(directory),@"\A[0-9a-f]{64}\z"))continue;
                try{var entry=VpsOperationStore.ReadSavedTarget(directory);if(entry!=null)found.Add(entry);}
                catch(Exception error)when(!(error is OperationCanceledException)){unavailable++;}
            }
            return found;
        },token);
        token.ThrowIfCancellationRequested();savedTargets.ItemsSource=targets.OrderBy(t=>t.Label,StringComparer.OrdinalIgnoreCase).ToArray();
        status.Text=targets.Count==0?"Сохранённых серверов не найдено.":"Сохранённых серверов: "+targets.Count+". Выберите сервер; сетевые проверки ещё не выполнялись.";
        if(unavailable>0)status.Text+=" Недоступных записей: "+unavailable+". Они не изменены; повреждённая или занятая история не подменяется другой записью.";
    }
    void SelectSaved(VpsSavedTarget chosen)
    {
        ReleaseSession();host.Text=chosen.Target.Host;sshPort.Text=chosen.Target.Port.ToString();user.Text=chosen.Target.User;publicKey.Text=chosen.Target.HostPublicKey;vpnPort.Text=chosen.VpnPort.ToString();
        identity.Text="";artifact.Text="";digest.Text="";
        status.Text="Параметры сервера загружены. Выберите файл SSH-ключа для сетевых операций. Отпечаток сервера: "+chosen.Target.HostFingerprint+(chosen.HasInterruptedWrite?". Есть незавершённая локальная запись; используйте восстановление операции.":"");Controls();
    }
    void RequireChecked(){if(report==null)throw new InvalidOperationException("Сначала проверьте SSH с текущими параметрами.");}
    async Task InspectAsync(CancellationToken token){
        var target=Target();report=null;var observed=await inspect(target,identity.Text,runtime,token);token.ThrowIfCancellationRequested();
        report=observed??throw new InvalidOperationException("Проверка SSH не вернула результат.");
        string directory=Path.Combine(storage,target.IdentityKey);
        if(Directory.Exists(directory)){Session(target);if(store.Read()!=null){vpnPort.Text=store.Read().VpnPort.ToString();var response=await coordinator.InspectAsync(token);if(response?.ServerStatus==null)throw new InvalidOperationException("Состояние VPS не подтверждено.");if(store.Read().Phase=="removed"||response.ServerStatus.State=="removed")store.ConfirmRemoved(response.ServerStatus);status.Text=Describe(response.ServerStatus);return;}}
        status.Text=report.PlatformReady?"SSH проверен · "+report.Architecture+". "+(report.ExistingUnit=="absent"?"Можно подготовить установку.":"Сервис уже существует. Для управления нужна история этой установки."):"SSH доступен, но для установки нужны Linux x64/ARM64, root, systemd и необходимые утилиты.";
    }
    async Task DeployAsync(CancellationToken token){
        RequireChecked();if(!report.PlatformReady)throw new InvalidOperationException("Сервер не прошёл проверку требований.");if(!canChange())throw new InvalidOperationException("Перед обновлением отключите VPN, игровой режим и локальные прокси.");
        var target=Target();Session(target);var known=store.Read();VpsServerStatus current=null;VpsOperationRecord previous=null,removed=null;
        if(known!=null){
            if(!known.IsTerminal)throw new InvalidOperationException("Сначала восстановите незавершённую операцию VPS.");
            current=(await coordinator.InspectAsync(token))?.ServerStatus;token.ThrowIfCancellationRequested();previous=store.FindCommittedCandidate(current);
            bool initialRetry=(known.Phase=="not-sent"||known.Phase=="rolled-back")&&known.ExpectedRevision==0&&current!=null&&(current.State=="absent"||current.State=="reserved")&&current.Revision==0&&current.Generation==""&&!current.ServiceExists&&!current.HasPendingOperation;
            if(current?.State=="removed")removed=store.ConfirmRemoved(current);
            if(previous==null&&removed==null&&!initialRetry)throw new InvalidOperationException("Текущая конфигурация VPS не подтверждена.");
        }else if(report.ExistingUnit!="absent")throw new InvalidOperationException("Существующий сервис не принадлежит сохранённой установке. Он не будет заменён.");
        int port;if(!Int32.TryParse(vpnPort.Text,out port)||port<1||port>65535)throw new ArgumentException("Укажите порт VPN от 1 до 65535.");
        // Freeze UI inputs before background file/hash work; workers never read controls.
        string artifactPath=artifact.Text,expectedHash=digest.Text.Trim(),architecture=report.Architecture,operationId=Guid.NewGuid().ToString("N");
        var draft=await Task.Run(()=>{
            var bytes=ReadArtifact(artifactPath,token);token.ThrowIfCancellationRequested();
            return removed!=null?VpsDeploymentDraft.CreateReinstallation(target,removed,current,operationId,architecture,bytes,expectedHash,port:port):previous==null?VpsDeploymentDraft.Create(target,known?.InstallationId??Guid.NewGuid().ToString("N"),operationId,0,architecture,bytes,expectedHash,port,profileId:known?.ProfileId):VpsDeploymentDraft.CreateUpdate(target,previous,current,operationId,architecture,bytes,expectedHash,port:port);
        },token);
        token.ThrowIfCancellationRequested();
        if(removed!=null&&!ConfirmReinstallation("Повторно установить NOVA на "+target.Host+":"+target.Port+"?\n\nБудет создан новый ключ VPN. Сохранённый локальный профиль не переключится автоматически: после установки проверьте и сохраните новый профиль отдельной кнопкой.")){status.Text="Повторная установка отменена. Пакет не отправлен.";return;}
        token.ThrowIfCancellationRequested();RequireChecked();
        if(Target().IdentityKey!=target.IdentityKey)throw new InvalidOperationException("Сервер изменился. Пакет не отправлен.");
        if(!canChange())throw new InvalidOperationException("Подключение изменилось. Установка не отправлена.");
        Func<bool> canDispatch=()=>report!=null&&sessionIdentity==target.IdentityKey&&Target().IdentityKey==target.IdentityKey&&canChange();
        var response=removed!=null?await coordinator.ReinstallAsync(target,draft,canDispatch,token):await coordinator.DeployAsync(target,draft,canDispatch,token);
        if(removed!=null&&response?.State=="rolled-back"){status.Text="Повторная установка отменена сервером. VPS остался удалённым; можно повторить установку. Локальный профиль не изменён.";return;}
        status.Text=response?.IsTerminalReceipt==true&&response.State=="committed"?"Пакет установлен. Теперь проверьте и добавьте профиль. Установка сервиса ещё не подтверждает прохождение VPN-трафика.":"Операция не подтверждена. Используйте восстановление; профиль не изменён.";
    }
    static byte[] ReadArtifact(string path,CancellationToken token){
        token.ThrowIfCancellationRequested();var info=new FileInfo(path);
        if(!info.Exists||info.Length<64||info.Length>VpsDeploymentDraft.MaximumBinaryBytes)throw new InvalidOperationException("Выберите Linux-ядро допустимого размера.");
        using(var stream=new FileStream(info.FullName,FileMode.Open,FileAccess.Read,FileShare.Read)){
            // Validate the opened file too: a path-length precheck alone can race replacement/growth.
            if(stream.Length<64||stream.Length>VpsDeploymentDraft.MaximumBinaryBytes)throw new InvalidOperationException("Выберите Linux-ядро допустимого размера.");
            var bytes=new byte[(int)stream.Length];int offset=0;
            while(offset<bytes.Length){token.ThrowIfCancellationRequested();int count=stream.Read(bytes,offset,Math.Min(32768,bytes.Length-offset));if(count==0)throw new EndOfStreamException("Файл ядра изменился во время чтения.");offset+=count;}
            token.ThrowIfCancellationRequested();if(stream.ReadByte()!=-1)throw new IOException("Файл ядра изменился во время чтения.");return bytes;
        }
    }
    async Task RemoveAsync(CancellationToken token){
        RequireChecked();if(!canChange())throw new InvalidOperationException("Перед удалением отключите VPN, игровой режим и локальные прокси.");
        var target=Target();Session(target);var known=store.Read()??throw new InvalidOperationException("Сохранённой установки VPS нет.");
        if(!known.IsTerminal||known.Phase=="removed")throw new InvalidOperationException("Установка удалена или требует восстановления операции.");
        string message="Удалить сервис NOVA и его конфигурации с "+target.Host+":"+target.Port+"?\n\nПосле отправки удаление необратимо: при обрыве связи восстановление операции продолжит удаление. SSH и другие сервисы не удаляются. Локальный VPN-профиль и история сохранятся; профиль этого сервера перестанет работать.";
        if(!ConfirmRemoval(message)){status.Text="Удаление отменено. Команда удаления не отправлена.";return;}
        token.ThrowIfCancellationRequested();RequireChecked();
        if(Target().IdentityKey!=target.IdentityKey)throw new InvalidOperationException("Сервер изменился. Удаление не отправлено.");
        var response=await coordinator.RemoveAsync(canChange,token);
        if(response?.State=="removed"){report=null;status.Text="NOVA удалена с VPS. Локальный профиль сохранён, но больше не работает. История сохранена для проверки операции.";}
        else if(response?.State=="not-sent")status.Text="Удаление отменено сервером до начала. Сервис и локальный профиль сохранены.";
        else status.Text="Удаление не подтверждено. Восстановите операцию; повторная установка заблокирована.";
    }
    async Task RollbackAsync(CancellationToken token){RequireChecked();if(!canChange())throw new InvalidOperationException("Перед откатом отключите VPN, игровой режим и локальные прокси.");Session(Target());var response=await coordinator.RollbackAsync(token);if(response?.IsTerminalReceipt==true&&response.State=="committed")vpnPort.Text=store.Read().VpnPort.ToString();status.Text=response?.IsTerminalReceipt==true&&response.State=="committed"?"Предыдущая версия VPS возвращена. Проверьте и сохраните профиль: ключ сервера мог измениться. Подключение автоматически не переключалось.":"Откат не подтверждён. Восстановите состояние операции; профиль не изменён.";}
    async Task RecoverAsync(CancellationToken token){
        Session(Target(false));var known=store.Read()??throw new InvalidOperationException("Сохранённой операции для этого сервера нет.");
        if(known.Phase=="prepared"){await coordinator.CancelPreparedAsync(token);status.Text="Неотправленная подготовка отменена локально. Ключ и история сохранены; проверьте SSH для новой попытки установки.";return;}
        if(known.Phase=="not-sent"){status.Text="Эта подготовка не отправлялась. Проверьте SSH для новой попытки установки.";return;}
        Target();if(!canChange())throw new InvalidOperationException("Перед восстановлением отключите VPN и использующие профиль подключения.");
        var response=await coordinator.ReconcileAsync(token);if(response?.State=="not-sent"){status.Text="Удаление отменено сервером до начала. Запоздалая команда не будет выполнена; сервис сохранён.";return;}if(response?.State=="removed"){report=null;status.Text="Удаление NOVA с VPS подтверждено. Локальный профиль и история сохранены.";return;}status.Text=response?.IsTerminalReceipt==true?"Операция согласована с журналом сервера. Проверьте SSH и профиль заново.":"Терминальное состояние операции не подтверждено. История сохранена; повторная установка не выполнялась.";
    }
    async Task SaveAsync(CancellationToken token){RequireChecked();Session(Target());var record=store.Read()??throw new InvalidOperationException("Сохранённой установки VPS нет.");var matches=state.Profiles.Where(p=>p.Id==record.ProfileId).ToArray();if(matches.Length>1)throw new InvalidOperationException("Конфликт идентификаторов профилей.");var previous=matches.Length==0?null:store.FindCommittedProfileBase(matches[0]);await coordinator.VerifyAndSaveProfileAsync(state,previous,corePath,runtime,canChange,persist,token);ProfilesChanged=true;status.Text="Профиль VPS проверен и сохранён. Выберите его в списке серверов для подключения.";}
    static string Describe(VpsServerStatus s)=>s.State=="removed"?"NOVA удалена с VPS · версия "+s.Revision+". Отсутствие сервиса подтверждено сервером. Локальный профиль сохранён; история нужна для проверки удаления.":"Состояние VPS: "+s.State+" · версия "+s.Revision+" · сервис "+(s.ServiceActive?"работает":"остановлен")+(s.HasPendingOperation?". Требуется восстановление операции.":". Для подтверждения трафика проверьте профиль.");
    async Task RunAsync(Func<CancellationToken,Task> action,string message){if(busy||preview)return;busy=true;operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime);status.Text=message;Controls();try{await action(operation.Token);}catch(OperationCanceledException){status.Text="Операция отменена. Если установка была отправлена, сначала восстановите её состояние.";}catch(Exception error){status.Text=LogSanitizer.Sanitize(error.Message);}finally{busy=false;operation.Dispose();operation=null;Controls();if(closePending)Close();}}
    void Controls(){if(check==null)return;fields?.SetCurrentValue(IsEnabledProperty,!busy&&!preview);check.IsEnabled=!busy&&!preview;deploy.IsEnabled=rollback.IsEnabled=save.IsEnabled=remove.IsEnabled=!busy&&!preview&&report!=null;recover.IsEnabled=!busy&&!preview;stop.IsEnabled=busy;}
}

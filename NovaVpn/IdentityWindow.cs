using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.IO;
using System.Threading.Tasks;
namespace NovaVpn;
public sealed class IdentityWindow : Window
{
    public AppState FullRestore { get; private set; }
    public IdentityState Result { get; private set; }
    readonly ComboBox profiles = new ComboBox { MinHeight=32 };
    readonly TextBox name=new TextBox(), hwid=new TextBox(), ua=new TextBox(), header=new TextBox(), extras=new TextBox { AcceptsReturn=true,MinHeight=80 }, urls=new TextBox { AcceptsReturn=true,MinHeight=60 }, code=new TextBox { AcceptsReturn=true,MinHeight=80,MaxHeight=140,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    readonly PasswordBox password=new PasswordBox { MinHeight=30 };
    readonly TextBlock status=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8) };
    readonly StackPanel panel=new StackPanel { Margin=new Thickness(24) };
    string selected;
    public IdentityWindow(IdentityState original, VisualPreferences visual = null, AppState appState = null, Func<bool> canRestore = null)
    {
        var preferences=visual??new VisualPreferences();var palette=ThemePalette.Create(preferences);
        Background=new System.Windows.Media.SolidColorBrush(palette.Bg);Foreground=new System.Windows.Media.SolidColorBrush(palette.Text);FontFamily=VisualDesign.InterfaceFont(preferences);FontSize=13*preferences.TextScale;
        Title="Идентификация подписок";Width=Math.Min(680,SystemParameters.WorkArea.Width);Height=Math.Min(780,SystemParameters.WorkArea.Height);MinWidth=Math.Min(480,Width);MinHeight=Math.Min(500,Height);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Result=new System.Web.Script.Serialization.JavaScriptSerializer {MaxJsonLength=IdentityTransfer.MaxBytes,RecursionLimit=64}.Deserialize<IdentityState>(new System.Web.Script.Serialization.JavaScriptSerializer {MaxJsonLength=IdentityTransfer.MaxBytes,RecursionLimit=64}.Serialize(original??new IdentityState()));
        foreach(var kind in new[]{typeof(TextBox),typeof(PasswordBox),typeof(ComboBox),typeof(ComboBoxItem),typeof(Button)}) {
            var style=new Style(kind,Application.Current?.TryFindResource(kind==typeof(ComboBox)?(object)"NovaSelectorStyle":kind) as Style);style.Setters.Add(new Setter(Control.BackgroundProperty,new System.Windows.Media.SolidColorBrush(palette.Surface)));style.Setters.Add(new Setter(Control.ForegroundProperty,new System.Windows.Media.SolidColorBrush(palette.Text)));style.Setters.Add(new Setter(Control.BorderBrushProperty,new System.Windows.Media.SolidColorBrush(palette.Border)));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,6,8,6)));if(kind==typeof(Button))style.Setters.Add(new Setter(Control.TemplateProperty,VisualDesign.ButtonTemplate(preferences,4)));Resources[kind]=style;
        }
        Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock {Text="Хранение и перенос идентичностей",FontSize=24*preferences.TextScale,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="Параметры хранятся локально и пока не отправляются провайдерам. Перенос зашифрован паролем; передайте пароль отдельно.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12)});
        panel.Children.Add(profiles);profiles.SelectionChanged+=(s,e)=>LoadSelected();
        var row=new WrapPanel();panel.Children.Add(row);
        AddButton(row,"Создать",()=>{var p=new SubscriptionIdentity();Result.identities.Add(p);Refresh(p.id);});
        AddButton(row,"Удалить",()=>{if(selected==null)return;Result.identities.RemoveAll(i=>i.id==selected);Result.bindings.RemoveAll(b=>b.identityId==selected);Refresh(null);});
        Field("Название",name);Field("Виртуальный HWID",hwid);Field("Имя заголовка HWID",header);Field("User-Agent",ua);
        var presets=new ComboBox {ItemsSource=new[]{"NOVA Android","NOVA Windows"},MinHeight=32};presets.SelectionChanged+=(s,e)=>{if(presets.SelectedIndex==0)ua.Text="NOVA-VPN-Android";else if(presets.SelectedIndex==1)ua.Text="NOVA-VPN/2.0";};Field("Шаблон User-Agent (или своё значение выше)",presets);
        Field("Дополнительные заголовки — Имя: значение, по одному на строку",extras);
        Field("Привязки подписок — HTTPS-ссылки, по одной на строку (пока не используются для запросов)",urls);
        AddButton(panel,"Сохранить профиль в список",()=>{Commit();Refresh(selected);status.Text="Профиль подготовлен. Нажмите «Применить», чтобы сохранить изменения.";});
        Field("Пароль переноса (10–256 символов)",password);
        Field("Код переноса",code);
        var transfer=new WrapPanel();panel.Children.Add(transfer);
        var export=new Button {Content="Экспорт выбранного",Margin=new Thickness(0,4,8,4),Padding=new Thickness(12,8,12,8)};transfer.Children.Add(export);
        export.Click+=async(s,e)=>{export.IsEnabled=false;try{Commit();var p=Result.identities.Single(i=>i.id==selected);var doc=new IdentityState{identities=new List<SubscriptionIdentity>{p}};string pass=password.Password;code.Text=await Task.Run(()=>IdentityTransfer.Export(doc,pass));status.Text="Код содержит выбранную идентичность без ссылок подписок.";}catch(Exception ex){status.Text=ex.Message;}finally{export.IsEnabled=true;}};
        var import=new Button {Content="Импорт кода",Margin=new Thickness(0,4,8,4),Padding=new Thickness(12,8,12,8)};transfer.Children.Add(import);
        import.Click+=async(s,e)=>{import.IsEnabled=false;try{var text=code.Text;var pass=password.Password;var incoming=await Task.Run(()=>IdentityTransfer.Import(text,pass));if(MessageBox.Show(this,"Импортировать профили: "+String.Join(", ",incoming.identities.Select(i=>i.name))+"? Совпадающие ID будут заменены.",Title,MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;var next=new IdentityState { platforms=Result.platforms, identities=Result.identities.Where(i=>!incoming.identities.Any(p=>p.id==i.id)).Concat(incoming.identities).ToList(),bindings=Result.bindings.Where(b=>!incoming.bindings.Any(x=>x.url==b.url)).Concat(incoming.bindings).ToList() };next.Validate();Result=next;Refresh(incoming.identities.FirstOrDefault()?.id);status.Text="Импорт подготовлен. Нажмите «Применить».";}catch(Exception ex){status.Text=ex.Message;}finally{import.IsEnabled=true;}};
        AddButton(transfer,"Открыть файл",()=>{var d=new OpenFileDialog{Filter="NOVA identity|*.novaid|Текст|*.txt"};if(d.ShowDialog(this)==true){if(new FileInfo(d.FileName).Length>12*1024*1024)throw new FormatException("Файл слишком большой.");code.Text=File.ReadAllText(d.FileName);}});
        AddButton(transfer,"Сохранить код в файл",()=>{if(!code.Text.StartsWith("NOVA-ID1:"))throw new FormatException("Сначала создайте код переноса.");var d=new SaveFileDialog{Filter="NOVA identity|*.novaid",FileName="identity.novaid"};if(d.ShowDialog(this)==true)File.WriteAllText(d.FileName,code.Text);});
        AddButton(transfer,"Показать QR",()=>IdentityQr.Show(this,code.Text));
        AddButton(transfer,"Прочитать QR из PNG",()=>{var d=new OpenFileDialog{Filter="PNG|*.png"};if(d.ShowDialog(this)==true)code.Text=IdentityQr.Read(d.FileName);});
        if(appState!=null) {
            var fullExport=new Button {Content="Полный перенос: экспорт",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,12,0,4)};panel.Children.Add(fullExport);
            fullExport.Click+=async(s,e)=>{fullExport.IsEnabled=false;try{if(selected!=null)Commit();var copy=FullTransfer.Copy(appState);copy.SubscriptionIdentities=Result;var pass=password.Password;code.Text=await Task.Run(()=>FullTransfer.Export(copy,pass));status.Text="Полный пакет зашифрован. Сохраните код в файл; большой пакет не помещается в QR.";}catch(Exception ex){status.Text=ex.Message;}finally{fullExport.IsEnabled=true;}};
            var fullImport=new Button {Content="Полный перенос: восстановить",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,4,0,4)};panel.Children.Add(fullImport);
            fullImport.Click+=async(s,e)=>{fullImport.IsEnabled=false;try{if(canRestore!=null&&!canRestore())throw new InvalidOperationException("Полное восстановление доступно только при отключённом VPN и Zapret. Текущее соединение не изменено.");var text=code.Text;var pass=password.Password;var restored=await Task.Run(()=>FullTransfer.Import(text,pass));if(MessageBox.Show(this,"Заменить профили и настройки полным пакетом? Профилей: "+restored.Profiles.Count+". Настройки другой платформы сохраняются для обратного переноса; пути и приложения другой ОС не применяются. Автоподключение будет выключено.",Title,MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;if(canRestore!=null&&!canRestore())throw new InvalidOperationException("Состояние соединения изменилось; импорт отменён.");FullRestore=restored;Result=restored.SubscriptionIdentities;DialogResult=true;}catch(Exception ex){status.Text=ex.Message;}finally{fullImport.IsEnabled=true;}};
        }
        panel.Children.Add(status);
        var bottom=new WrapPanel();panel.Children.Add(bottom);
        AddButton(bottom,"Применить",()=>{if(selected!=null)Commit();Result.Validate();DialogResult=true;});
        var cancel=new Button{Content="Отмена",IsCancel=true,Margin=new Thickness(8),Padding=new Thickness(12,8,12,8)};bottom.Children.Add(cancel);
        Refresh(null);
    }
    void Field(string label,Control value){panel.Children.Add(new TextBlock {Text=label,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,4)});value.MinHeight=Math.Max(30,value.MinHeight);panel.Children.Add(value);}
    void AddButton(Panel target,string text,Action action){var b=new Button{Content=text,Margin=new Thickness(0,4,8,4),Padding=new Thickness(12,8,12,8)};b.Click+=(s,e)=>{try{action();}catch(Exception ex){status.Text=ex.Message;}};target.Children.Add(b);}
    void Refresh(string id){profiles.ItemsSource=null;profiles.ItemsSource=Result.identities;profiles.SelectedItem=Result.identities.FirstOrDefault(i=>i.id==id)??Result.identities.FirstOrDefault();LoadSelected();}
    void LoadSelected(){var p=profiles.SelectedItem as SubscriptionIdentity;selected=p?.id;name.Text=p?.name??"";hwid.Text=p?.hwid??"";ua.Text=p?.userAgent??"";header.Text=p?.hwidHeader??"X-HWID";extras.Text=p==null?"":String.Join("\n",p.headers.Select(h=>h.Key+": "+h.Value));urls.Text=p==null?"":String.Join("\n",Result.bindings.Where(b=>b.identityId==p.id).Select(b=>b.url));}
    void Commit(){if(selected==null)throw new FormatException("Сначала создайте профиль.");var h=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var line in extras.Text.Split('\n').Where(l=>!String.IsNullOrWhiteSpace(l))){int colon=line.IndexOf(':');if(colon<=0)throw new FormatException("Заголовок: Имя: значение.");h.Add(line.Substring(0,colon).Trim(),line.Substring(colon+1).Trim());}var p=new SubscriptionIdentity{id=selected,name=name.Text.Trim(),hwid=hwid.Text.Trim(),hwidHeader=header.Text.Trim(),userAgent=ua.Text.Trim(),headers=h};p.Validate();var links=urls.Text.Split('\n').Select(u=>u.Trim()).Where(u=>u.Length>0).Distinct().Select(u=>new IdentityBinding{url=u,identityId=p.id}).ToList();var next=new IdentityState{platforms=Result.platforms,identities=Result.identities.Where(i=>i.id!=p.id).Concat(new[]{p}).ToList(),bindings=Result.bindings.Where(b=>b.identityId!=p.id&&!links.Any(l=>l.url==b.url)).Concat(links).ToList()};next.Validate();Result=next;}
}

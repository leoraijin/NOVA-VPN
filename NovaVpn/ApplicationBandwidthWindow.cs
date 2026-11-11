using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace NovaVpn;

public sealed class ApplicationBandwidthWindow : Window
{
    sealed class Row {public Border Card;public string Sid;public int Burst;public TextBox Path,Upload,Download;}
    public ApplicationBandwidthPolicyEdit Accepted{get;private set;}
    readonly List<Row> rows=new List<Row>();
    readonly StackPanel list;
    readonly CheckBox enabled;
    readonly TextBlock message;
    readonly ScrollViewer scroll;
    readonly Button add;
    readonly string currentSid;
    readonly bool preview;
    readonly ThemePalette palette;
    public ApplicationBandwidthWindow(AppState state,VisualPreferences visual,bool preview)
    {
        if(state==null)throw new ArgumentNullException(nameof(state));this.preview=preview;
        using(var user=WindowsIdentity.GetCurrent())currentSid=user.User.Value;
        palette=ThemePalette.Create(visual??new VisualPreferences());Title="Лимиты приложений";Width=Math.Min(760,SystemParameters.WorkArea.Width);Height=Math.Min(760,SystemParameters.WorkArea.Height);MinWidth=Math.Min(520,Width);MinHeight=Math.Min(480,Height);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(palette.Bg);Foreground=new SolidColorBrush(palette.Text);FontSize=13*(visual?.TextScale??1);
        var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(palette.Surface2)));style.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));style.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(palette.Border)));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));style.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Hand));
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));border.SetBinding(Border.BackgroundProperty,new Binding("Background"){RelativeSource=RelativeSource.TemplatedParent});border.SetBinding(Border.BorderBrushProperty,new Binding("BorderBrush"){RelativeSource=RelativeSource.TemplatedParent});border.SetBinding(Border.BorderThicknessProperty,new Binding("BorderThickness"){RelativeSource=RelativeSource.TemplatedParent});
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetBinding(FrameworkElement.MarginProperty,new Binding("Padding"){RelativeSource=RelativeSource.TemplatedParent});presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);style.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(Button)){VisualTree=border}));var unavailable=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};unavailable.Setters.Add(new Setter(UIElement.OpacityProperty,0.5));style.Triggers.Add(unavailable);Resources.Add(typeof(Button),style);
        var root=new Grid{Margin=new Thickness(20)};root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var fields=new StackPanel();fields.Children.Add(new TextBlock{Text="Скорость для каждого приложения",FontSize=FontSize*1.35,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        fields.Children.Add(new TextBlock{Text="Правила действуют в SOCKS-прокси NOVA. Основной VPN, HTTP-прокси и обычный трафик пока не ограничиваются. Все соединения приложения делят общий лимит.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,10)});
        enabled=new CheckBox{Name="BandwidthEnabled",Content=new TextBlock{Text="Применять сохранённые лимиты к SOCKS-входам",TextWrapping=TextWrapping.Wrap},Foreground=Foreground,IsChecked=state.ApplicationLimitsEnabled,IsEnabled=!preview,Margin=new Thickness(0,4,0,10)};fields.Children.Add(enabled);
        fields.Children.Add(new TextBlock{Text="Мбит/с; 0 — без ограничения. Укажите лимит хотя бы в одном направлении. Правила можно менять во время работы. Для входов, запущенных без лимитов, сначала нужна остановка.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        list=new StackPanel{Name="BandwidthRows"};fields.Children.Add(list);
        add=new Button{Name="AddBandwidthRule",Content="Добавить приложение",Padding=new Thickness(12,7,12,7),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,8)};
        add.Click+=delegate{if(preview)return;AddRow(null);scroll.ScrollToEnd();};fields.Children.Add(add);
        message=new TextBlock{Name="BandwidthMessage",Text=preview?"Предпросмотр: сохранение отключено.":"Сохранение правил само не запускает VPN или локальные прокси.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,6)};fields.Children.Add(message);
        scroll=new ScrollViewer{Name="BandwidthScroll",Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};root.Children.Add(scroll);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        var cancel=new Button{Content="Отмена",Name="CancelBandwidth",IsCancel=true,Padding=new Thickness(18,9,18,9),Margin=new Thickness(0,0,10,0)};cancel.Click+=delegate{DialogResult=false;};
        var save=new Button{Content="Сохранить",Name="SaveBandwidth",IsEnabled=!preview,Padding=new Thickness(18,9,18,9)};
        save.Click+=delegate{if(preview)return;try{
            var edits=rows.Select(row=>new ApplicationBandwidthRule{UserSid=row.Sid,ExecutablePath=row.Path.Text.Trim(),UploadBytesPerSecond=ApplicationBandwidthPolicyEdit.ParseMegabits(row.Upload.Text),DownloadBytesPerSecond=ApplicationBandwidthPolicyEdit.ParseMegabits(row.Download.Text),BurstBytes=row.Burst}).ToList();
            var result=new ApplicationBandwidthPolicyEdit(enabled.IsChecked==true,edits);
            if(result.Enabled&&!result.Policies.Any(p=>p.Identity.UserSid==currentSid))throw new ArgumentException("Нет правил для текущего пользователя Windows. Привяжите нужное приложение к этому пользователю или оставьте лимиты выключенными.");
            Accepted=result;DialogResult=true;
        }catch(Exception error)when(error is ArgumentException||error is InvalidDataException||error is IOException||error is NotSupportedException){message.Text=error.Message;message.Foreground=new SolidColorBrush(palette.Warning);scroll.ScrollToEnd();}};
        actions.Children.Add(cancel);actions.Children.Add(save);Grid.SetRow(actions,1);root.Children.Add(actions);Content=root;
        foreach(var row in state.ApplicationBandwidthRules??new List<ApplicationBandwidthRule>())AddRow(row,true);
        UpdateAdd();
    }
    void UpdateAdd(){add.IsEnabled=!preview&&rows.Count<ApplicationBandwidthPolicySnapshot.MaximumRules;}
    TextBox Input(string name,string value)=>new TextBox{Name=name,Text=value??"",Padding=new Thickness(6),Background=new SolidColorBrush(palette.Input),Foreground=Foreground,CaretBrush=Foreground,IsReadOnly=preview,MaxLength=32768};
    void AddRow(ApplicationBandwidthRule saved,bool loading=false)
    {
        if(!loading&&rows.Count>=ApplicationBandwidthPolicySnapshot.MaximumRules)return;
        var row=new Row{Sid=saved==null?currentSid:saved.UserSid,Burst=saved!=null&&saved.BurstBytes>=1&&saved.BurstBytes<=ApplicationBandwidthBudget.MaximumBurstBytes?saved.BurstBytes:65536};
        var content=new StackPanel();var title=new TextBlock{Text="Приложение",FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};content.Children.Add(title);
        row.Path=Input("BandwidthPath",saved?.ExecutablePath);row.Path.Margin=new Thickness(0,6,8,0);row.Path.ToolTip="Полный путь к .exe-файлу приложения";
        Action titleChanged=()=>{try{title.Text=String.IsNullOrWhiteSpace(row.Path.Text)?"Приложение":System.IO.Path.GetFileName(row.Path.Text);}catch(ArgumentException){title.Text="Приложение";}};row.Path.TextChanged+=delegate{titleChanged();};titleChanged();
        var pathLine=new Grid();pathLine.ColumnDefinitions.Add(new ColumnDefinition());pathLine.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});pathLine.Children.Add(row.Path);
        var browse=new Button{Name="BrowseBandwidthApp",Content="Выбрать…",Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,6,0,0),IsEnabled=!preview};Grid.SetColumn(browse,1);pathLine.Children.Add(browse);content.Children.Add(pathLine);
        browse.Click+=delegate{if(preview)return;var picker=new OpenFileDialog{Title="Выберите приложение",Filter="Приложения (*.exe)|*.exe",CheckFileExists=true,Multiselect=false};if(picker.ShowDialog(this)==true)row.Path.Text=picker.FileName;};
        var account=new TextBlock{Name="BandwidthUser",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};content.Children.Add(account);
        var rebind=new Button{Name="RebindBandwidthUser",Content="Использовать текущего пользователя",Padding=new Thickness(8,5,8,5),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=!preview,Margin=new Thickness(0,5,0,2)};content.Children.Add(rebind);
        Action accountChanged=()=>{bool own=String.Equals(row.Sid,currentSid,StringComparison.OrdinalIgnoreCase);account.Text=own?"Пользователь Windows: текущий":"Другой пользователь Windows: правило не применяется к текущему пользователю.";account.ToolTip=row.Sid;rebind.Visibility=own?Visibility.Collapsed:Visibility.Visible;};accountChanged();rebind.Click+=delegate{if(preview)return;row.Sid=currentSid;accountChanged();};
        var rates=new Grid{Margin=new Thickness(0,10,0,8)};rates.ColumnDefinitions.Add(new ColumnDefinition());rates.ColumnDefinitions.Add(new ColumnDefinition());
        var upload=new StackPanel{Margin=new Thickness(0,0,8,0)};upload.Children.Add(new TextBlock{Text="Отдача, Мбит/с",TextWrapping=TextWrapping.Wrap});row.Upload=Input("BandwidthUpload",ApplicationBandwidthPolicyEdit.FormatMegabits(saved?.UploadBytesPerSecond??0));row.Upload.MaxLength=32;upload.Children.Add(row.Upload);rates.Children.Add(upload);
        var download=new StackPanel();download.Children.Add(new TextBlock{Text="Загрузка, Мбит/с",TextWrapping=TextWrapping.Wrap});row.Download=Input("BandwidthDownload",ApplicationBandwidthPolicyEdit.FormatMegabits(saved?.DownloadBytesPerSecond??0));row.Download.MaxLength=32;download.Children.Add(row.Download);Grid.SetColumn(download,1);rates.Children.Add(download);content.Children.Add(rates);
        var remove=new Button{Name="RemoveBandwidthRule",Content="Удалить правило",IsEnabled=!preview,Padding=new Thickness(8,5,8,5),HorizontalAlignment=HorizontalAlignment.Left};remove.Click+=delegate{if(preview)return;rows.Remove(row);list.Children.Remove(row.Card);UpdateAdd();};content.Children.Add(remove);
        row.Card=new Border{Child=content,Padding=new Thickness(12),Margin=new Thickness(0,0,0,10),Background=new SolidColorBrush(palette.Surface),BorderBrush=new SolidColorBrush(palette.Border),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8)};rows.Add(row);list.Children.Add(row.Card);UpdateAdd();
    }
}

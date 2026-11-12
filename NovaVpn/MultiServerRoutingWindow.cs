using System;using System.Collections.Generic;using System.Linq;using System.IO;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using Microsoft.Win32;
namespace NovaVpn;

public sealed class MultiServerRoutingWindow : Window
{
 public RoutingSettings Accepted {get;private set;}
 internal void FitToWorkArea(Size workArea)
 {
  Width = Math.Min(900, workArea.Width);
  Height = Math.Min(720, workArea.Height);
  MinWidth = Math.Min(650, Width);
  MinHeight = Math.Min(480, Height);
 }

 readonly RoutingSettings source;readonly VpnProfile[] profiles;readonly List<MultiServerRuleRecord> rules;
 readonly ListBox apps=new ListBox{SelectionMode=SelectionMode.Extended,HorizontalContentAlignment=HorizontalAlignment.Stretch};
 readonly ListBox secondaryApps=new ListBox{SelectionMode=SelectionMode.Extended,HorizontalContentAlignment=HorizontalAlignment.Stretch};
 readonly ComboBox secondary=new ComboBox{MinWidth=240};
 readonly ListBox sites=new ListBox{HorizontalContentAlignment=HorizontalAlignment.Stretch};
 readonly ComboBox primary=new ComboBox{MinWidth=240};readonly ComboBox target=new ComboBox{MinWidth=240};
 readonly TextBox search=new TextBox{MinWidth=180};readonly TextBox domain=new TextBox{MinWidth=220};
 readonly CheckBox subdomains=new CheckBox{Content="Включая поддомены",IsChecked=true};
 readonly CheckBox ipv6=new CheckBox{Content="Разрешить IPv6 через назначенные серверы"};
 readonly CheckBox acknowledgement = new CheckBox { Content = new TextBlock
  { Text = "Я проверил переход: остальные приложения будут через основной сервер", TextWrapping = TextWrapping.Wrap } };
 readonly TextBlock error=new TextBlock{Foreground=Brushes.OrangeRed,TextWrapping=TextWrapping.Wrap};
 const string SavedRuleSource = "Сохранённое правило";
 readonly HashSet<MultiServerRuleRecord> unconfirmedApps = new HashSet<MultiServerRuleRecord>();
 readonly List<ApplicationEntry> catalog;readonly HashSet<MultiServerRuleRecord> unbound=new HashSet<MultiServerRuleRecord>();string primaryId;string secondaryId;bool selecting;
 public MultiServerRoutingWindow(RoutingSettings routing,IEnumerable<VpnProfile> profiles,IEnumerable<ApplicationEntry> entries,string selectedId,VisualPreferences visual,bool zapretActive=false)
  : this(routing,profiles,entries,selectedId,visual,zapretActive,null) { }
 internal MultiServerRoutingWindow(RoutingSettings routing,IEnumerable<VpnProfile> profiles,IEnumerable<ApplicationEntry> entries,string selectedId,VisualPreferences visual,bool zapretActive,Window owner)
 {
  source=RoutingOverlayService.Clone(routing);this.profiles=profiles.ToArray();catalog=entries.ToList();
  if(source.MigratedPolicy!=null)
   throw new NotSupportedException("Преобразуйте перенесённую политику перед переходом.");
  rules=source.MultiServer?.Copy().Rules??new List<MultiServerRuleRecord>();primaryId=source.MultiServer?.RequiresRebinding==true?null:source.MultiServer?.DefaultProfileId??selectedId;
  secondaryId = source.MultiServer?.RequiresRebinding == true ? null : source.MultiServer?.SecondaryProfileId;
  if (source.MultiServer?.RequiresRebinding == true)
  {
   foreach (var rule in rules)
   {
    if (rule.Target == "Profile") unbound.Add(rule);
    if (rule.ApplicationPath != null) unconfirmedApps.Add(rule);
   }
  }
  if (source.MultiServer == null) rules = InitialRules(source);
  foreach(var rule in rules.Where(r=>r.ApplicationPath!=null&&!catalog.Any(a=>ApplicationRoutingService.SamePath(a.ExecutablePath,r.ApplicationPath))))catalog.Add(new ApplicationEntry{Name=Path.GetFileName(rule.ApplicationPath),ExecutablePath=rule.ApplicationPath,Source=SavedRuleSource});
  var colors=ThemePalette.Create(visual??new VisualPreferences());Title="Серверы для приложений и сайтов";WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(colors.Bg);Foreground=new SolidColorBrush(colors.Text);
  if (owner != null) Owner = owner;
  FitToWorkArea(WindowsWindowBounds.WorkAreaSize(owner));
  var preferences=visual??new VisualPreferences();FontFamily=VisualDesign.InterfaceFont(preferences);FontSize=13*preferences.TextScale;error.Foreground=new SolidColorBrush(colors.Danger);
  // Keep control chrome and text in the same palette as the editor surface.
  foreach(var kind in new[]{typeof(TextBox),typeof(ComboBox),typeof(ComboBoxItem),typeof(Button),typeof(CheckBox),typeof(ListBox),typeof(ListBoxItem),typeof(TabControl),typeof(TabItem)}){
   var basis=Application.Current?.TryFindResource(kind==typeof(ComboBox)?(object)"NovaSelectorStyle":kind) as Style;
   var style=new Style(kind,basis);style.Setters.Add(new Setter(Control.ForegroundProperty,new SolidColorBrush(colors.Text)));
   style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(kind==typeof(ListBoxItem)?Colors.Transparent:colors.Input)));
   style.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(colors.Border)));
   if(kind==typeof(TabItem)){
    style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,7,12,7)));
    style.Setters.Add(new Setter(Control.TemplateProperty,(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type TabItem}'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' Padding='{TemplateBinding Padding}'><ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/></Border></ControlTemplate>")));
   }
   if(kind==typeof(ListBoxItem)||kind==typeof(TabItem)){
    var selected=new Trigger{Property=System.Windows.Controls.Primitives.Selector.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(colors.SoftSelection)));style.Triggers.Add(selected);
   }
   var disabled=new Trigger{Property=IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(Control.ForegroundProperty,new SolidColorBrush(colors.Muted)));style.Triggers.Add(disabled);
   Resources[kind]=style;
  }
  ScrollViewer.SetHorizontalScrollBarVisibility(apps,ScrollBarVisibility.Disabled);ScrollViewer.SetHorizontalScrollBarVisibility(sites,ScrollBarVisibility.Disabled);
  System.Windows.Automation.AutomationProperties.SetName(primary,"Основной сервер");System.Windows.Automation.AutomationProperties.SetName(target,"Назначение сервера");
  System.Windows.Automation.AutomationProperties.SetName(search,"Поиск сервера");System.Windows.Automation.AutomationProperties.SetName(domain,"Домен сайта");
  var layout=new DockPanel{Margin=new Thickness(18)};Content=layout;
  var body = new DockPanel();
  var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);body.Children.Add(top);
  top.Children.Add(new TextBlock{Text="Правила сайтов важнее приложений. Остальной трафик — через основной сервер.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
  if(zapretActive)top.Children.Add(new TextBlock
  {
   Text="Явные правила сайтов имеют приоритет над исключениями Zapret, в том числе перенесённые правила.",
   TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)
  });
  if(source.AdvancedRules.Count>0)top.Children.Add(new TextBlock
  {
   Text="Блокировки по портам сохранены. Остальные правила портов действуют для трафика без назначения.",
   TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)
  });
  if(source.DirectProcesses.Count>0 || source.ProxyProcesses.Count>0)top.Children.Add(new TextBlock
  {
   Text="Прежние правила по имени .exe сохраняются. Назначения по полному пути и правила портов применяются раньше.",
   TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)
  });
  top.Children.Add(new TextBlock{Text="Основной сервер"});top.Children.Add(primary);
  top.Children.Add(new TextBlock{Text="VPN-2"});top.Children.Add(secondary);
  System.Windows.Automation.AutomationProperties.SetName(secondary,"VPN-2");
  ipv6.IsChecked=source.EnableIpv6;top.Children.Add(ipv6);
  top.Children.Add(new TextBlock{Text="При выключенном IPv6 эта семья трафика блокируется внутри TUN.",TextWrapping=TextWrapping.Wrap});
  top.Children.Add(new TextBlock{Text="Поиск сервера: название, страна или транспорт",Margin=new Thickness(0,8,0,0)});top.Children.Add(search);
  var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);layout.Children.Add(bottom);
  var needsAck=source.MultiServer==null&&(rules.Count>0||source.Mode=="Direct"||source.DefaultRoute=="Direct");acknowledgement.IsChecked=!needsAck;acknowledgement.Visibility=needsAck?Visibility.Visible:Visibility.Collapsed;bottom.Children.Add(acknowledgement);
  bottom.Children.Add(error);var actions=new WrapPanel();bottom.Children.Add(actions);AddButton(actions,"Отмена",()=>{DialogResult=false;});
  AddButton(actions,"Применить",Save);
  var tabs=new TabControl{Margin=new Thickness(0,12,0,12)};body.Children.Add(tabs);
  // Keep enough room for the active list and its commands even in a short window.
  double minBodyHeight = 460 * Math.Max(1, preferences.TextScale);
  body.Height = minBodyHeight;
  var viewport = new ScrollViewer
  {
   Content = body,
   VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
   HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
  };
  viewport.SizeChanged += (_, args) => body.Height = Math.Max(minBodyHeight, args.NewSize.Height);
  layout.Children.Add(viewport);
  var appPanel=new DockPanel();var appActions=new WrapPanel();DockPanel.SetDock(appActions,Dock.Bottom);appPanel.Children.Add(appActions);appPanel.Children.Add(apps);
  var sitePanel=new DockPanel();var siteActions=new WrapPanel();DockPanel.SetDock(siteActions,Dock.Bottom);sitePanel.Children.Add(siteActions);sitePanel.Children.Add(sites);
  tabs.Items.Add(new TabItem{Header="Приложения",Content=appPanel});tabs.Items.Add(new TabItem{Header="Сайты",Content=sitePanel});
  var secondaryPanel = new DockPanel();
  var secondaryActions = new WrapPanel();
  DockPanel.SetDock(secondaryActions, Dock.Bottom);
  secondaryPanel.Children.Add(secondaryActions);
  secondaryPanel.Children.Add(secondaryApps);
  ScrollViewer.SetHorizontalScrollBarVisibility(secondaryApps, ScrollBarVisibility.Disabled);
  tabs.Items.Add(new TabItem { Header = "Приложения через VPN-2", Content = secondaryPanel });
  AddButton(secondaryActions, "Через VPN-2", () => AssignSecondaryApps(true));
  AddButton(secondaryActions, "Вернуть основной VPN", () => AssignSecondaryApps(false));
  var common=new WrapPanel{Margin=new Thickness(0,6,0,6)};bottom.Children.Insert(0,common);common.Children.Add(new TextBlock{Text="Назначение: ",VerticalAlignment=VerticalAlignment.Center});common.Children.Add(target);
  AddButton(appActions,"Добавить .exe",()=>{var file=new OpenFileDialog{Filter="Приложения (*.exe)|*.exe"};if(file.ShowDialog(this)==true){catalog.Add(new ApplicationEntry{Name=Path.GetFileName(file.FileName),ExecutablePath=file.FileName});Render();}});
  AddButton(appActions, "Назначить выбранным", () =>
  {
   var chosen = Choice();
   if (chosen == null) { error.Text = "Выберите назначение."; return; }
   foreach (ListBoxItem item in apps.SelectedItems.Cast<ListBoxItem>())
   {
    var entry = (ApplicationEntry)item.Tag;
    var rule = FindAppRule(entry.ExecutablePath);
    if (rule == null)
    {
     rule = new MultiServerRuleRecord { ApplicationPath = entry.ExecutablePath };
     rules.Add(rule);
    }
    rule.Target = chosen.Kind.ToString();
    rule.ProfileId = chosen.ProfileId;
    unbound.Remove(rule);
   }
   error.Text = "";
   Render();
  });
  if (unconfirmedApps.Count != 0) AddButton(appActions, "Указать путь к .exe", () =>
  {
   if (apps.SelectedItems.Count != 1 || !(apps.SelectedItem is ListBoxItem item) ||
       !unconfirmedApps.Contains(FindAppRule(((ApplicationEntry)item.Tag).ExecutablePath)))
   {
    error.Text = "Выберите одно импортированное правило приложения.";
    return;
   }
   var entry = (ApplicationEntry)item.Tag;
   var file = new OpenFileDialog { Filter = "Приложения (*.exe)|*.exe" };
   if (file.ShowDialog(this) != true) return;
   try
   {
    RebindApplicationPath(entry.ExecutablePath, file.FileName);
    error.Text = "";
    Render();
   }
   catch (ArgumentException ex) { error.Text = ex.Message; }
  });
  AddButton(appActions,"Убрать назначение",()=>{foreach(ListBoxItem item in apps.SelectedItems.Cast<ListBoxItem>().ToArray()){var entry=(ApplicationEntry)item.Tag;rules.RemoveAll(r=>r.ApplicationPath!=null&&ApplicationRoutingService.SamePath(r.ApplicationPath,entry.ExecutablePath));}Render();});
  siteActions.Children.Add(domain);siteActions.Children.Add(subdomains);
  AddButton(siteActions, "Добавить сайт", () =>
  {
   try
   {
    var host = MultiServerRule.NormalizeDomainSelector(domain.Text);
    var existing = rules.Where(rule => rule.Domain == host).ToArray();
    var chosen = Choice() ?? throw new ArgumentException("Выберите назначение.");
    foreach (var rule in existing) { rules.Remove(rule); unbound.Remove(rule); }
    rules.Add(new MultiServerRuleRecord
    {
     Domain = host, IncludeSubdomains = !host.StartsWith(".", StringComparison.Ordinal) && subdomains.IsChecked == true,
     Target = chosen.Kind.ToString(), ProfileId = chosen.ProfileId
    });
    domain.Text = "";
    error.Text = existing.Length > 0 ? "Этот домен уже был в списке. Назначение обновлено." : "";
    Render();
   }
   catch (ArgumentException ex) { error.Text = ex.Message; }
  });
  AddButton(siteActions,"Изменить сервер",()=>{if(sites.SelectedItem is ListBoxItem item&&Choice() is ServerRouteTarget chosen){var rule=(MultiServerRuleRecord)item.Tag;rule.Target=chosen.Kind.ToString();rule.ProfileId=chosen.ProfileId;unbound.Remove(rule);Render();}});
  AddButton(siteActions,"Удалить",()=>{if(sites.SelectedItem is ListBoxItem item){rules.Remove((MultiServerRuleRecord)item.Tag);Render();}});
  primary.SelectionChanged+=delegate{if(!selecting&&primary.SelectedItem is ComboBoxItem item){primaryId=(string)item.Tag;FillServers();}};
  secondary.SelectionChanged += delegate
  {
   if (selecting || !(secondary.SelectedItem is ComboBoxItem item)) return;
   var previousId = secondaryId;
   secondaryId = (string)item.Tag;
   if (previousId != null)
    foreach (var rule in rules.Where(rule => rule.Target == "Profile" && rule.ProfileId == previousId && !unbound.Contains(rule)))
     rule.ProfileId = secondaryId;
   FillServers();
   Render();
  };
  search.TextChanged+=delegate{FillServers();};FillServers();Render();
 }
 void AssignSecondaryApps(bool assign)
 {
  if (assign && !profiles.Any(p => p.Id == secondaryId && Compatible(p)))
  { error.Text = "Сначала выберите доступный конфиг VPN-2."; return; }
  foreach (ListBoxItem item in secondaryApps.SelectedItems.Cast<ListBoxItem>())
  {
   var entry = (ApplicationEntry)item.Tag;
   var rule = FindAppRule(entry.ExecutablePath);
   if (!assign && (secondaryId == null || rule?.Target != "Profile" || rule.ProfileId != secondaryId || unbound.Contains(rule))) continue;
   if (rule == null)
   {
    rule = new MultiServerRuleRecord { ApplicationPath = entry.ExecutablePath };
    rules.Add(rule);
   }
   rule.Target = assign ? "Profile" : "Default";
   rule.ProfileId = assign ? secondaryId : null;
   unbound.Remove(rule);
  }
  error.Text = "";
  Render();
 }
 static void AddButton(Panel panel,string text,Action click){var button=new Button{Content=text,Padding=new Thickness(12,7,12,7),Margin=new Thickness(4)};button.Click+=delegate{click();};panel.Children.Add(button);}
 MultiServerRuleRecord FindAppRule(string path) => rules.FirstOrDefault(rule =>
  rule.ApplicationPath != null && ApplicationRoutingService.SamePath(rule.ApplicationPath, path));

 void RebindApplicationPath(string importedPath, string selectedPath)
 {
  var rule = FindAppRule(importedPath);
  if (rule == null || !unconfirmedApps.Contains(rule)) throw new ArgumentException("Выберите импортированное правило приложения.");
  var path = ApplicationRoutingService.NormalizePath(selectedPath);
  if (path.Length == 0) throw new ArgumentException("Выберите полный путь к .exe файлу.");
  var other = FindAppRule(path);
  if (other != null && other != rule) throw new ArgumentException("Для этого приложения уже есть правило.");

  if (!ApplicationRoutingService.SamePath(importedPath, path))
   catalog.RemoveAll(entry => entry.Source == SavedRuleSource && ApplicationRoutingService.SamePath(entry.ExecutablePath, importedPath));
  if (!catalog.Any(entry => ApplicationRoutingService.SamePath(entry.ExecutablePath, path)))
   catalog.Add(new ApplicationEntry { Name = Path.GetFileName(path), ExecutablePath = path, Source = SavedRuleSource });
  rule.ApplicationPath = path;
  unconfirmedApps.Remove(rule);
 }

 ServerRouteTarget Choice() => target.SelectedItem is ComboBoxItem item && item.IsEnabled ? item.Tag as ServerRouteTarget : null;
 string Label(MultiServerRuleRecord r) {
  if (r != null && unconfirmedApps.Contains(r)) return "Укажите путь к .exe после импорта";
  if(r!=null&&unbound.Contains(r))return "Назначьте сервер после импорта";
  if(r==null||r.Target=="Default")return "Основной сервер";
  if(r.Target=="Direct")return "Напрямую";
  if(r.Target=="Block")return "Блокировать";
  var profile=profiles.FirstOrDefault(p=>p.Id==r.ProfileId);
  if (r.ProfileId == secondaryId && profile != null && !profile.RetiredFromSubscription) return "VPN-2 · " + profile.Name;
  return profile==null?"Сервер недоступен — трафик блокируется":profile.RetiredFromSubscription?
   profile.Name+" · удалён из подписки — трафик блокируется":profile.Name;
 }
 void FillServers()
 {
  var previousTarget = target.Items.Count == 0 ? new ServerRouteTarget(ServerRouteKind.Default) : Choice();
  string query = search.Text.Trim();
  selecting = true;
  try
  {
   primary.Items.Clear();
   secondary.Items.Clear();
   secondary.IsEnabled = source.MultiServer?.RequiresRebinding != true;
   target.Items.Clear();
   target.Items.Add(new ComboBoxItem { Content = "Основной сервер", Tag = new ServerRouteTarget(ServerRouteKind.Default) });
   target.Items.Add(new ComboBoxItem { Content = "Напрямую", Tag = new ServerRouteTarget(ServerRouteKind.Direct) });
   target.Items.Add(new ComboBoxItem { Content = "Блокировать", Tag = new ServerRouteTarget(ServerRouteKind.Block) });
   if (secondaryId != null)
    target.Items.Add(new ComboBoxItem { Content = "Через VPN-2", Tag = new ServerRouteTarget(ServerRouteKind.Profile, secondaryId),
     IsEnabled = profiles.Any(p => p.Id == secondaryId && Compatible(p)) });
   foreach (var profile in profiles.OrderBy(p => p.Name))
   {
    string country = ServerGroupingService.CountryCode(profile);
    string label = profile.Name + " · " + profile.ProtocolLabel + " · " + profile.Transport;
    if (country != "NV") label += " · " + ServerGroupingService.CountryLabel(profile);
    bool matches = (label + " " + country).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    if (!matches && profile.Id != primaryId && profile.Id != secondaryId) continue;

    bool compatible = Compatible(profile);
    label += profile.RetiredFromSubscription ? " · Удалён из подписки" : compatible ? "" : " · Несовместим";
    var main = new ComboBoxItem { Content = label, Tag = profile.Id, IsEnabled = compatible && profile.Id != secondaryId };
    primary.Items.Add(main);
    if (profile.Id == primaryId) primary.SelectedItem = main;
    if (profile.Id != primaryId)
    {
     var second = new ComboBoxItem { Content = label, Tag = profile.Id, IsEnabled = compatible };
     secondary.Items.Add(second);
     if (profile.Id == secondaryId) secondary.SelectedItem = second;
    }
    if (matches && profile.Id != secondaryId) target.Items.Add(new ComboBoxItem { Content = label, Tag = new ServerRouteTarget(ServerRouteKind.Profile, profile.Id), IsEnabled = compatible });
   }
   if (primaryId != null && primary.SelectedItem == null)
   {
    var missing = new ComboBoxItem { Content = "Сервер недоступен — трафик блокируется", Tag = primaryId, IsEnabled = false };
    primary.Items.Add(missing);
    primary.SelectedItem = missing;
   }
   if (secondaryId != null && secondary.SelectedItem == null)
   {
    var missing = new ComboBoxItem { Content = "VPN-2 недоступен", Tag = secondaryId, IsEnabled = false };
    secondary.Items.Add(missing);
    secondary.SelectedItem = missing;
   }
   if (previousTarget != null)
    target.SelectedItem = target.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled &&
     item.Tag is ServerRouteTarget route && route.Kind == previousTarget.Kind && route.ProfileId == previousTarget.ProfileId);
  }
  finally { selecting = false; }
 }
 internal static bool Compatible(VpnProfile profile) =>
  !profile.RetiredFromSubscription && profile.Protocol != "sing-box" && !CorporateProxy.Enabled(profile);
 void Render(){apps.Items.Clear();sites.Items.Clear();secondaryApps.Items.Clear();foreach(var entry in catalog.GroupBy(a=>a.ExecutablePath,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).OrderBy(a=>a.Name)){var rule=rules.FirstOrDefault(r=>r.ApplicationPath!=null&&ApplicationRoutingService.SamePath(r.ApplicationPath,entry.ExecutablePath));apps.Items.Add(new ListBoxItem{Content=new TextBlock{Text=entry.Name+" — "+Label(rule),TextWrapping=TextWrapping.Wrap},Tag=entry,ToolTip=entry.ExecutablePath,Padding=new Thickness(7)});
  secondaryApps.Items.Add(new ListBoxItem{Content=new TextBlock{Text=entry.Name+" — "+Label(rule),
   TextWrapping=TextWrapping.Wrap},Tag=entry,ToolTip=entry.ExecutablePath,Padding=new Thickness(7)});
  }foreach(var rule in rules.Where(r=>r.Domain!=null))sites.Items.Add(new ListBoxItem{Content=new TextBlock{Text=rule.DomainLabel+" — "+Label(rule),TextWrapping=TextWrapping.Wrap},Tag=rule,Padding=new Thickness(7)});}
 internal static List<MultiServerRuleRecord> InitialRules(RoutingSettings source)
 {
   var rules = new List<MultiServerRuleRecord>();
   foreach(var path in source.DirectProcessPaths)rules.Add(new MultiServerRuleRecord{ApplicationPath=path,Target="Direct"});
   foreach(var path in source.ProxyProcessPaths.Where(p=>!source.DirectProcessPaths.Any(d=>ApplicationRoutingService.SamePath(d,p))))rules.Add(new MultiServerRuleRecord{ApplicationPath=path,Target="Default"});
   var seen = new HashSet<string>(StringComparer.Ordinal);
   void ImportSites(IEnumerable<string> values, string targetKind)
   {
    foreach (string value in values)
    {
     string trimmed = (value ?? "").Trim();
     if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
     string selector = MultiServerRule.NormalizeDomainSelector(RoutingInspector.NormalizeDomainRule(trimmed));
     if (seen.Add(selector)) rules.Add(new MultiServerRuleRecord { Domain = selector, Target = targetKind });
    }
   }
   ImportSites(source.DirectDomains, "Direct");
   ImportSites(source.ProxyDomains, "Default");
   return rules;
 }
 internal static RoutingSettings CreateCandidate(RoutingSettings source, MultiServerSettings assignments, bool enableIpv6)
 {
  var updated = RoutingOverlayService.Clone(source);
  updated.MultiServer = assignments;
  assignments.Validate();
  updated.EnableIpv6 = enableIpv6;
  updated.DirectDomains.Clear();
  updated.ProxyDomains.Clear();
  updated.DirectProcessPaths.Clear();
  updated.ProxyProcessPaths.Clear();
  updated.Mode = "Smart";
  updated.DefaultRoute = "Proxy";
  updated.DnsMode = "Secure";
  return updated;
 }
 RoutingSettings BuildCandidate(){
  if(acknowledgement.IsChecked!=true)throw new InvalidOperationException("Подтвердите проверку перехода на новую схему.");
  bool preservingMissing=source.MultiServer!=null&&!source.MultiServer.RequiresRebinding&&primaryId==source.MultiServer.DefaultProfileId&&
   !profiles.Any(p=>p.Id==primaryId&&!p.RetiredFromSubscription);
  if(!preservingMissing&&!profiles.Any(p=>p.Id==primaryId&&!p.RetiredFromSubscription&&p.Protocol!="sing-box"&&!CorporateProxy.Enabled(p)))throw new InvalidOperationException("Выберите доступный совместимый основной сервер.");
  if (unconfirmedApps.Any(rules.Contains)) throw new InvalidOperationException("Укажите путь к .exe для каждого импортированного правила приложения.");
  if(unbound.Any(r=>rules.Contains(r)))throw new InvalidOperationException("Заново назначьте сервер для каждого импортированного правила.");
  return CreateCandidate(source, new MultiServerSettings { Revision = (source.MultiServer?.Revision ?? 0) + 1,
   DefaultProfileId = primaryId, SecondaryProfileId = secondaryId, Rules = rules.Select(r => r.Copy()).ToList() }, ipv6.IsChecked == true);
 }
 void Save(){try{Accepted=BuildCandidate();DialogResult=true;}catch(Exception e){error.Text=LogSanitizer.Sanitize(e.Message);}}
}
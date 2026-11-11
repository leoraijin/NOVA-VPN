using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Reflection;using System.Windows;using System.Windows.Controls;using NovaVpn;
class MultiServerEditorTests {
 static int count;static void Check(bool ok){if(!ok)throw new Exception("Editor assertion "+(count+1));count++;}
 static readonly DateTime When=new DateTime(2026,11,6,0,0,0,DateTimeKind.Utc);
 static VpnProfile Node(string id)=>new VpnProfile{Id=id,Name=id,Protocol="vless",Server=id+".example.test",Port=443,UserId="11111111-1111-4111-8111-111111111111",Security="none",Transport="tcp",AddedAt=When};
 static AppState State(){var s=StateStore.CreateEphemeral();s.Profiles=new List<VpnProfile>{Node("fr"),Node("us")};s.Routing=new RoutingSettings();s.SelectedProfileId="fr";return s;}
 static RoutingSettings Candidate(){return new RoutingSettings{MultiServer=new MultiServerSettings{DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{new MultiServerRuleRecord{ApplicationPath=@"C:\Apps\ChatGPT.exe",Target="Profile",ProfileId="us"}}}};}
 static bool Refuses(Action work){try{work();return false;}catch(Exception){return true;}}
 static object Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);
 static async System.Threading.Tasks.Task CheckPostPublishRollback(string directory)
 {
  var state = State();
  state.Routing = Candidate();
  state.Routing.MultiServer.Revision = 2;
  state.LastConnectedProfileId = "fr";
  var candidate = RoutingOverlayService.Clone(state.Routing);
  candidate.MultiServer.Revision = 3;
  candidate.MultiServer.Rules[0].ProfileId = "fr";
  string root = Path.Combine(directory, "post-publish-routing-fixture");
  object store = null;
  bool armed = false, observedPublished = false;
  long runtimeRevision = 2;
  object InvokeStore(string name, params object[] values)
  {
   try { return store.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(store, values); }
   catch (TargetInvocationException ex) when (ex.InnerException != null)
   {
    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
    throw;
   }
  }
  void Save(AppState value) => InvokeStore("Save", value);
  var checkpoint = new Action<string>(stage =>
  {
   if (!armed || stage != "state-published") return;
   armed = false;
   var disk = (AppState)InvokeStore("Load");
   observedPublished = disk.Routing.MultiServer.Revision == 3 &&
    disk.Routing.MultiServer.Rules[0].ProfileId == "fr" && runtimeRevision == 3;
   throw new IOException("Fixture failure after publication");
  });
  store = typeof(StateStore).GetMethod("OpenDirectory", BindingFlags.Static | BindingFlags.NonPublic)
   .Invoke(null, new object[] { root, checkpoint });
  Save(state);
  string fingerprint = RoutingMigrationService.Fingerprint(state.Routing);
  const double newerScale = 1.27;
  var result = await RoutingApplyTransaction.ApplyAsync(state, () => true, () => System.Threading.Tasks.Task.CompletedTask,
   () =>
   {
    runtimeRevision = 3;
    state.Visual.TextScale = newerScale;
    Save(state);
    armed = true;
    return System.Threading.Tasks.Task.CompletedTask;
   },
   () => MultiServerRoutingCommitPlan.PrepareConnected(state, candidate, "fr").Apply(() => true, Save), Save,
   () => { runtimeRevision = 2; return System.Threading.Tasks.Task.CompletedTask; });
  Check(observedPublished);
  Check(!result.Applied && result.PreviousRestored && result.Error is IOException && result.PersistenceRollbackError == null);
  Check(runtimeRevision == 2);
  var restored = (AppState)InvokeStore("Load");
  Check(RoutingMigrationService.Fingerprint(state.Routing) == fingerprint &&
   RoutingMigrationService.Fingerprint(restored.Routing) == fingerprint);
  Check(state.Visual.TextScale == newerScale && restored.Visual.TextScale == newerScale);
  Check(!Directory.EnumerateFiles(root, "*.next-*").Any());
 }

 [STAThread] static void Main(string[] args){
  var s=State();var next=Candidate();var before=FullStateRestorePlan.Fingerprint(s);var p=MultiServerRoutingCommitPlan.Prepare(s,next);
  Check(FullStateRestorePlan.Fingerprint(s)==before);AppState saved=null;
  p.Apply(()=>true,c=>{Check(s.Routing.MultiServer==null);saved=c;});Check(s.Routing.MultiServer.Rules[0].ProfileId=="us");Check(saved.Routing.MultiServer.Rules[0].ProfileId=="us");
  next.MultiServer.Rules[0].ProfileId="fr";Check(s.Routing.MultiServer.Rules[0].ProfileId=="us");
  s=State();before=FullStateRestorePlan.Fingerprint(s);p=MultiServerRoutingCommitPlan.Prepare(s,Candidate());Check(Refuses(()=>p.Apply(()=>true,c=>{throw new IOException("fixture");})));Check(FullStateRestorePlan.Fingerprint(s)==before);
  s=State();p=MultiServerRoutingCommitPlan.Prepare(s,Candidate());s.SelectedProfileId="us";bool wrote=false;Check(Refuses(()=>p.Apply(()=>true,c=>wrote=true)));Check(!wrote);
  s=State();p=MultiServerRoutingCommitPlan.Prepare(s,Candidate());Check(Refuses(()=>p.Apply(()=>false,c=>wrote=true)));
  s=State();p=MultiServerRoutingCommitPlan.Prepare(s,Candidate());Check(Refuses(()=>p.Apply(()=>true,c=>c.Routing.MultiServer.DefaultProfileId="us")));Check(s.Routing.MultiServer==null);
  var routes=Candidate();var window=new MultiServerRoutingWindow(routes,new[]{Node("fr"),Node("us")},new[]{new ApplicationEntry{Name="ChatGPT",ExecutablePath=@"C:\Apps\ChatGPT.exe"}},"fr",new VisualPreferences());
  try {
   Check(window.Accepted==null);var records=(List<MultiServerRuleRecord>)Field(window,"rules");records[0].ProfileId="fr";Check(routes.MultiServer.Rules[0].ProfileId=="us");
   Check(((ListBox)Field(window,"apps")).SelectionMode==SelectionMode.Extended);Check(((ComboBox)Field(window,"primary")).Items.Count==2);Check(((ComboBox)Field(window,"target")).Items.Count==5);
  }finally{window.Close();}
  var french=Node("fr");french.Name="Paris Node";french.DeclaredCountryCode="FR";
  var american=Node("us");american.Name="New York Node";american.DeclaredCountryCode="US";american.Transport="grpc";
  window=new MultiServerRoutingWindow(Candidate(),new[]{french,american},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {
   var primary=(ComboBox)Field(window,"primary");
   var target=(ComboBox)Field(window,"target");
   var search=(TextBox)Field(window,"search");
   var sites=(ListBox)Field(window,"sites");
   var domain=(TextBox)Field(window,"domain");
   ComboBoxItem Target(ServerRouteKind kind,string id=null)=>target.Items.Cast<ComboBoxItem>()
    .Single(item=>item.Tag is ServerRouteTarget route&&route.Kind==kind&&route.ProfileId==id);
   var add=((DockPanel)sites.Parent).Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
    .Single(button=>Equals(button.Content,"Добавить сайт"));
   search.Text=" США ";
   Check(primary.Items.Count==2 && Equals(((ComboBoxItem)primary.SelectedItem).Tag,"fr"));
   Check(target.Items.Count==4 && Target(ServerRouteKind.Profile,"us").Content.ToString().Contains("США"));
   target.SelectedItem=Target(ServerRouteKind.Profile,"us");
   search.Text="gRpC";
   Check(((ServerRouteTarget)((ComboBoxItem)target.SelectedItem).Tag).ProfileId=="us");
   search.Text="Франция";
   Check(primary.Items.Count==1 && Equals(((ComboBoxItem)primary.SelectedItem).Tag,"fr"));
   Check(target.Items.Count==4 && target.SelectedItem==null);
   domain.Text="restricted.example.test";
   add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   Check(sites.Items.Count==0 && ((TextBlock)Field(window,"error")).Text.Contains("назначение"));
   target.SelectedItem=Target(ServerRouteKind.Block);
   add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   var candidate=(RoutingSettings)typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
   Check(candidate.MultiServer.Compile(new[]{"fr","us"}).Decide(@"C:\Apps\ChatGPT.exe","restricted.example.test").Effective.Kind==ServerRouteKind.Block);
   Check(candidate.MultiServer.Compile(new[]{"fr","us"}).Decide(@"C:\Apps\ChatGPT.exe","allowed.example.test").Effective.ProfileId=="us");
   search.Text="Paris Node";
   Check(((ServerRouteTarget)((ComboBoxItem)target.SelectedItem).Tag).Kind==ServerRouteKind.Block);
  }finally{window.Close();}
  var legacy=new RoutingSettings();legacy.DirectProcessPaths.Add(@"C:\Apps\ChatGPT.exe");legacy.ProxyDomains.Add("example.com");
  window=new MultiServerRoutingWindow(legacy,new[]{Node("fr"),Node("us")},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {var records=(List<MultiServerRuleRecord>)Field(window,"rules");Check(records.Count==2);Check(records.Any(r=>r.ApplicationPath!=null&&r.Target=="Direct"));Check(((CheckBox)Field(window,"acknowledgement")).IsChecked==false);Check(legacy.MultiServer==null&&legacy.ProxyDomains.Count==1);}finally{window.Close();}
  var transferred=Candidate();transferred.MultiServer.RequiresRebinding=true;
  window=new MultiServerRoutingWindow(transferred,new[]{Node("fr"),Node("us")},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try{Check(Field(window,"primaryId")==null);Check((int)Field(window,"unbound").GetType().GetProperty("Count").GetValue(Field(window,"unbound"))==1);Check(transferred.MultiServer.RequiresRebinding);}finally{window.Close();}
  var importedPaths=Candidate();
  importedPaths.MultiServer.RequiresRebinding=true;
  importedPaths.MultiServer.Rules.Add(new MultiServerRuleRecord{ApplicationPath=@"C:\Apps\Local.exe",Target="Direct"});
  window=new MultiServerRoutingWindow(importedPaths,new[]{Node("fr"),Node("us")},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {
   var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
   var buildCandidate=typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",flags);
   var bindPath=typeof(MultiServerRoutingWindow).GetMethod("RebindApplicationPath",flags);
   var main=(ComboBox)Field(window,"primary");
   main.SelectedItem=main.Items.Cast<ComboBoxItem>().Single(item=>Equals(item.Tag,"fr"));
   var apps=(ListBox)Field(window,"apps");
   var app=apps.Items.Cast<ListBoxItem>().Single(item=>((ApplicationEntry)item.Tag).Name=="ChatGPT.exe");
   Check(((TextBlock)app.Content).Text.Contains("Укажите путь"));
   Check(Refuses(()=>bindPath.Invoke(window,new object[]{@"C:\Apps\ChatGPT.exe",@"C:\Apps\Local.exe"})));
   bindPath.Invoke(window,new object[]{@"C:\Apps\ChatGPT.exe",@"D:\Installed\ChatGPT.exe"});
   Check(Refuses(()=>buildCandidate.Invoke(window,null)));
   var records=(List<MultiServerRuleRecord>)Field(window,"rules");
   Check(records[0].ProfileId=="us" && records[0].ApplicationPath==@"D:\Installed\ChatGPT.exe");
   bindPath.Invoke(window,new object[]{@"C:\Apps\Local.exe",@"C:\Apps\Local.exe"});
   Check(Refuses(()=>buildCandidate.Invoke(window,null)));
   typeof(MultiServerRoutingWindow).GetMethod("Render",flags).Invoke(window,null);
   apps.SelectedItem=apps.Items.Cast<ListBoxItem>().Single(item=>((ApplicationEntry)item.Tag).ExecutablePath==@"D:\Installed\ChatGPT.exe");
   var target=(ComboBox)Field(window,"target");
   target.SelectedItem=target.Items.Cast<ComboBoxItem>().Single(item=>item.Tag is ServerRouteTarget route&&route.ProfileId=="us");
   var panel=(DockPanel)apps.Parent;
   panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button=>Equals(button.Content,"Назначить выбранным"))
    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   var candidate=(RoutingSettings)buildCandidate.Invoke(window,null);
   var policy=candidate.MultiServer.Compile(new[]{"fr","us"});
   Check(!candidate.MultiServer.RequiresRebinding && policy.Decide(@"D:\Installed\ChatGPT.exe",null).Effective.ProfileId=="us");
   Check(policy.Decide(@"C:\Apps\Local.exe",null).Effective.Kind==ServerRouteKind.Direct);
   Check(importedPaths.MultiServer.Rules[0].ApplicationPath==@"C:\Apps\ChatGPT.exe" && importedPaths.MultiServer.RequiresRebinding);
  }finally{window.Close();}
  s=State();s.LastConnectedProfileId="fr";var connected=Candidate();connected.MultiServer.DefaultProfileId="us";
  var connectedPlan=MultiServerRoutingCommitPlan.PrepareConnected(s,connected,"us");
  Check(s.SelectedProfileId=="fr"&&s.LastConnectedProfileId=="fr");
  connectedPlan.Apply(()=>true,c=>{Check(c.SelectedProfileId=="us"&&c.LastConnectedProfileId=="us");Check(s.SelectedProfileId=="fr");});
  Check(s.SelectedProfileId=="us"&&s.FallbackRootProfileId=="us"&&s.LastConnectedProfileId=="us");
  Check(Refuses(()=>MultiServerRoutingCommitPlan.PrepareConnected(State(),connected,"fr")));
  var retiredUs=Node("us");retiredUs.RetiredFromSubscription=true;
  window=new MultiServerRoutingWindow(Candidate(),new[]{Node("fr"),retiredUs},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {
   var primaryItems=((ComboBox)Field(window,"primary")).Items.Cast<ComboBoxItem>().ToArray();
   Check(!primaryItems.Single(i=>Equals(i.Tag,"us")).IsEnabled);
   Check(primaryItems.Single(i=>Equals(i.Tag,"us")).Content.ToString().Contains("Удалён из подписки"));
   Check(!((ComboBox)Field(window,"target")).Items.Cast<ComboBoxItem>().Single(i=>i.Tag is ServerRouteTarget t&&t.Kind==ServerRouteKind.Profile&&t.ProfileId=="us").IsEnabled);
   Check(((TextBlock)((ListBoxItem)((ListBox)Field(window,"apps")).Items[0]).Content).Text.Contains("трафик блокируется"));
  }finally{window.Close();}
  var retiredFr=Node("fr");retiredFr.RetiredFromSubscription=true;
  window=new MultiServerRoutingWindow(Candidate(),new[]{retiredFr,Node("us")},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {
   var candidate=(RoutingSettings)typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
   Check(candidate.MultiServer.DefaultProfileId=="fr");Check(candidate.MultiServer.Rules[0].ProfileId=="us");
   Check(!((ComboBoxItem)((ComboBox)Field(window,"primary")).SelectedItem).IsEnabled);
  }finally{window.Close();}
  window=new MultiServerRoutingWindow(Candidate(),new[]{Node("us")},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {
   Check(((ComboBoxItem)((ComboBox)Field(window,"primary")).SelectedItem).Content.ToString().Contains("трафик блокируется"));
   var candidate=(RoutingSettings)typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);Check(candidate.MultiServer.DefaultProfileId=="fr");
  }finally{window.Close();}
  window=new MultiServerRoutingWindow(new RoutingSettings(),new[]{retiredFr},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {((CheckBox)Field(window,"acknowledgement")).IsChecked=true;Check(Refuses(()=>typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null)));}finally{window.Close();}
  window=new MultiServerRoutingWindow(transferred,new[]{retiredFr,Node("us")},Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
  try {Check(Refuses(()=>typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null)));}finally{window.Close();}
  var defaultState = StateStore.CreateEphemeral();
  defaultState.Profiles = new List<VpnProfile>{Node("fr"),Node("us")};
  defaultState.SelectedProfileId = "fr";
  defaultState.Routing.DirectDomains.Add("exact.example.test");
  defaultState.Routing.ProxyDomains.AddRange(new[]{".local","*.example.test"});
  defaultState.Routing.AdvancedRules.AddRange(new[]{"direct|tcp|8443","block|udp|443","proxy|tcp|8445:8447","direct|tcp|8445:8448"});
  defaultState.Routing.DirectProcesses.AddRange(new[]{"Other.exe","Pinned.exe"});
  defaultState.Routing.ProxyProcesses.Add("Other.exe");
  var legacyFingerprint = RoutingMigrationService.Fingerprint(defaultState.Routing);
  window = new MultiServerRoutingWindow(defaultState.Routing, defaultState.Profiles, Array.Empty<ApplicationEntry>(), "fr", new VisualPreferences());
  try {
   ((CheckBox)Field(window,"acknowledgement")).IsChecked=true;
   var candidate=(RoutingSettings)typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
   var policy=candidate.MultiServer.Compile(new[]{"fr","us"});
   Check(policy.Decide(null,"localhost").Effective.Kind==ServerRouteKind.Direct &&
    policy.Decide(null,"sub.localhost").Effective.ProfileId=="fr");
   Check(policy.Decide(null,"printer.local").Effective.Kind==ServerRouteKind.Direct &&
    policy.Decide(null,"local").Effective.ProfileId=="fr");
   Check(policy.Decide(null,"exact.example.test").Effective.Kind==ServerRouteKind.Direct &&
    policy.Decide(null,"child.exact.example.test").Effective.ProfileId=="fr");
   Check(candidate.MultiServer.Rules.Count(rule=>rule.Domain==".local")==1 &&
    candidate.MultiServer.Rules.Single(rule=>rule.Domain==".local").Target=="Direct");
   Check(candidate.AdvancedRules.SequenceEqual(defaultState.Routing.AdvancedRules));
   const string pinnedApp = @"C:\Apps\Pinned.exe";
   candidate.MultiServer.Rules.Add(new MultiServerRuleRecord{ApplicationPath=pinnedApp,Target="Profile",ProfileId="us"});
   var advancedPlan=CompiledRoutingPlan.CreateFor(defaultState.Profiles[0],candidate,VpnEngine.SingBox,defaultState.Profiles);
   Check(advancedPlan.Explain("unassigned.example.net",@"C:\Apps\Other.exe","tcp",8443).Route=="direct" &&
    advancedPlan.Explain("unassigned.example.net",pinnedApp,"tcp",8443).ProfileId=="us");
   Check(advancedPlan.Explain("unassigned.example.net",@"C:\Apps\Other.exe","tcp",8445).ProfileId=="fr" &&
    advancedPlan.Explain("unassigned.example.net",@"C:\Apps\Other.exe","tcp",8448).Route=="direct");
   Check(advancedPlan.Explain("exact.example.test",pinnedApp,"udp",443).Route=="block" &&
    advancedPlan.Explain("unassigned.example.net",pinnedApp,"tcp",443).ProfileId=="us");
   Check(candidate.DirectProcesses.SequenceEqual(defaultState.Routing.DirectProcesses) &&
    candidate.ProxyProcesses.SequenceEqual(defaultState.Routing.ProxyProcesses));
   Check(advancedPlan.Explain("unassigned.example.net",@"C:\Apps\Other.exe","tcp",8449).Route=="direct" &&
    advancedPlan.Explain("unassigned.example.net",pinnedApp,"tcp",8449).ProfileId=="us");
   Check(advancedPlan.Explain("unassigned.example.net",@"C:\Apps\OTHER.exe","tcp",8449).ProfileId=="fr");
   Check(advancedPlan.Explain("unassigned.example.net",@"C:\Apps\Other.exe","tcp",8445).ProfileId=="fr");
   File.WriteAllText(Path.Combine(args[0],"legacy-defaults.json"), MultiServerConfigBuilder.Build(candidate,defaultState.Profiles,
    Path.Combine(args[0],"legacy-fakeip.db")));
   MultiServerRoutingCommitPlan.Prepare(defaultState,candidate).Apply(()=>true,_=>{});
   MultiServerRoutingCommitPlan.PrepareDisable(defaultState).Apply(()=>true,_=>{});
   Check(RoutingMigrationService.Fingerprint(defaultState.Routing)==legacyFingerprint);
  } finally {window.Close();}
  var coexist = State();
  coexist.Routing.ProxyDomains.Add("assets.youtube.com");
  var ordinaryBefore = RoutingMigrationService.Fingerprint(coexist.Routing);
  var assigned = Candidate();
  assigned.MultiServer.Rules.Add(new MultiServerRuleRecord{Domain="pinned.youtube.com",Target="Profile",ProfileId="fr"});
  assigned.MultiServer.Rules.Add(new MultiServerRuleRecord{Domain="blocked.youtube.com",Target="Block"});
  MultiServerRoutingCommitPlan.Prepare(coexist,assigned).Apply(()=>true,_=>{});
  var assignedBefore = RoutingMigrationService.Fingerprint(coexist.Routing);
  coexist.Zapret.Mode="VPN+Zapret";
  Check(ZapretRoutingTransitionService.SetActive(coexist,true)>0 &&
   coexist.Routing.ZapretDirectDomains.Contains("*.youtube.com") && !coexist.Zapret.RoutingChangesApplied);
  Check(ZapretRoutingTransitionService.SetActive(coexist,true)==0 && coexist.Routing.DirectProcesses.Count==0);
  Check(Refuses(()=>FullStateRestorePlan.PrepareSettingsOnly(coexist,State())));
  var portableRestore=FullStateRestorePlan.Prepare(coexist,State());
  Check(Refuses(()=>portableRestore.Apply(coexist,()=>true,_=>{})));
  var plan=CompiledRoutingPlan.CreateFor(coexist.Profiles[0],coexist.Routing,VpnEngine.SingBox,coexist.Profiles);
  Check(plan.Explain("assets.youtube.com",@"C:\Apps\ChatGPT.exe","tcp",443).Route=="direct");
  Check(plan.Explain("pinned.youtube.com",@"C:\Apps\ChatGPT.exe","tcp",443).ProfileId=="fr" &&
   plan.Explain("blocked.youtube.com",@"C:\Apps\ChatGPT.exe","tcp",443).Route=="block");
  Check(plan.Explain("other.example.test",@"C:\Apps\ChatGPT.exe","tcp",443).ProfileId=="us");
  Check(ZapretRoutingTransitionService.SetActive(coexist,false)>0 &&
   RoutingMigrationService.Fingerprint(coexist.Routing)==assignedBefore);
  ZapretRoutingTransitionService.SetActive(coexist,true);
  MultiServerRoutingCommitPlan.PrepareDisable(coexist).Apply(()=>true,savedState=>
   Check(savedState.Zapret.RoutingChangesApplied && savedState.Routing.DirectProcesses.Contains("discord.exe")));
  Check(coexist.Routing.MultiServer==null && coexist.Routing.ZapretDirectDomains.Contains("*.youtube.com") &&
   coexist.Zapret.SavedProxyDomains.Contains("assets.youtube.com"));
  coexist.Zapret.Mode="VPN";
  ZapretRoutingTransitionService.SetActive(coexist,false);
  Check(RoutingMigrationService.Fingerprint(coexist.Routing)==ordinaryBefore);
  var activeLegacy = State();
  activeLegacy.Routing.ProxyDomains.Add("assets.youtube.com");
  activeLegacy.Routing.ProxyProcessPaths.Add(@"C:\Apps\Discord\Discord.exe");
  var ordinarySnapshot = RoutingMigrationService.Fingerprint(activeLegacy.Routing);
  activeLegacy.Zapret.Mode = "VPN+Zapret";
  ZapretRoutingTransitionService.SetActive(activeLegacy,true);
  StateStore.NormalizeForSerialization(activeLegacy);
  var activeFingerprint = FullStateRestorePlan.Fingerprint(activeLegacy);
  var activeRoutingFingerprint = RoutingMigrationService.Fingerprint(activeLegacy.Routing);
  Check(activeLegacy.Zapret.RoutingChangesApplied && activeLegacy.Zapret.SavedProxyDomains.Contains("assets.youtube.com"));
  var editorSource = (RoutingSettings)typeof(MultiServerRoutingHistory).GetMethod("AssignmentEditorSource",BindingFlags.Static|BindingFlags.NonPublic)
   .Invoke(null,new object[]{activeLegacy});
  Check(FullStateRestorePlan.Fingerprint(activeLegacy)==activeFingerprint);
  Check(editorSource.ZapretDirectDomains.Count==0 && editorSource.DirectProcesses.Count==0 &&
   RoutingMigrationService.Fingerprint(editorSource)==ordinarySnapshot);
  window = new MultiServerRoutingWindow(editorSource,activeLegacy.Profiles,Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences(),true);
  try {
   ((CheckBox)Field(window,"acknowledgement")).IsChecked=true;
   var candidate=(RoutingSettings)typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
   Check(candidate.MultiServer.Rules.Any(r=>r.Domain=="assets.youtube.com"&&r.Target=="Default") &&
    candidate.MultiServer.Rules.Any(r=>r.ApplicationPath==@"C:\Apps\Discord\Discord.exe"&&r.Target=="Default"));
   var entry=MultiServerRoutingCommitPlan.PrepareConnected(activeLegacy,candidate,"fr");
   var store=typeof(StateStore).GetMethod("OpenDirectory",BindingFlags.Static|BindingFlags.NonPublic)
    .Invoke(null,new object[]{Path.Combine(args[0],"active-zapret-transition"),null});
   var storageFlags=BindingFlags.Instance|BindingFlags.NonPublic;
   void SaveDraft(AppState draft) => store.GetType().GetMethod("Save",storageFlags).Invoke(store,new object[]{draft});
   AppState ReadDraft() => (AppState)store.GetType().GetMethod("Load",storageFlags).Invoke(store,null);
   SaveDraft(FullTransfer.Copy(activeLegacy));
   var diskBefore=FullStateRestorePlan.Fingerprint(ReadDraft());
   int writes=0;
   Check(Refuses(()=>entry.Apply(()=>true,staged=>{
    SaveDraft(staged);
    if(++writes==1)throw new IOException("Fixture write failure after publication");
   })));
   Check(writes==2 && FullStateRestorePlan.Fingerprint(activeLegacy)==activeFingerprint &&
    FullStateRestorePlan.Fingerprint(ReadDraft())==diskBefore);
   entry.Apply(()=>true,staged=>Check(staged.Routing.MultiServer!=null && !staged.Zapret.RoutingChangesApplied &&
    staged.Routing.ZapretDirectDomains.Contains("*.youtube.com")));
   Check(RoutingMigrationService.Fingerprint(activeLegacy.MultiServerRoutingHistory.SingleServerRouting)==ordinarySnapshot);
   var livePlan=CompiledRoutingPlan.CreateFor(activeLegacy.Profiles[0],activeLegacy.Routing,VpnEngine.SingBox,activeLegacy.Profiles);
   Check(livePlan.Explain("images.youtube.com",@"C:\Apps\Discord\Discord.exe","tcp",443).Route=="direct" &&
    livePlan.Explain("assets.youtube.com",@"C:\Apps\Discord\Discord.exe","tcp",443).ProfileId=="fr");
   MultiServerRoutingCommitPlan.PrepareDisable(activeLegacy,"fr").Apply(()=>true,_=>{});
   Check(activeLegacy.Zapret.RoutingChangesApplied &&
    RoutingMigrationService.Fingerprint(activeLegacy.Routing)==activeRoutingFingerprint);
   var resumed=MultiServerRoutingHistory.PreviewResume(activeLegacy);
   Check(resumed.ZapretDirectDomains.Count==0 && resumed.DirectProcesses.Count==0 && resumed.MultiServer!=null);
   activeLegacy.Zapret.Mode="VPN";
   ZapretRoutingTransitionService.SetActive(activeLegacy,false);
   Check(RoutingMigrationService.Fingerprint(activeLegacy.Routing)==ordinarySnapshot);
  } finally {window.Close();}
  var dualState = State();
  dualState.Profiles.Add(Node("jp"));
  var dualDraft = Candidate();
  dualDraft.MultiServer.Rules.Add(new MultiServerRuleRecord { Domain = "example.test", Target = "Profile", ProfileId = "us" });
  var dualApp = new ApplicationEntry { Name = "ChatGPT", ExecutablePath = @"C:\Apps\ChatGPT.exe" };
  {
   var dualWindow = new MultiServerRoutingWindow(dualDraft, dualState.Profiles, new[] { dualApp }, "fr", dualState.Visual);
   try
   {
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var second = (ComboBox)Field(dualWindow, "secondary");
    var targets = (ComboBox)Field(dualWindow, "target");
    var secondApps = (ListBox)Field(dualWindow, "secondaryApps");
    Check(!second.Items.Cast<ComboBoxItem>().Any(item => Equals(item.Tag, "fr")));
    second.SelectedItem = second.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, "us"));
    Check(targets.Items.Cast<ComboBoxItem>().Any(item => Equals(item.Content, "Через VPN-2")));
    second.SelectedItem = second.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, "jp"));
    var build = typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate", flags);
    var assign = typeof(MultiServerRoutingWindow).GetMethod("AssignSecondaryApps", flags);
    var candidate = (RoutingSettings)build.Invoke(dualWindow, null);
    Check(candidate.MultiServer.SecondaryProfileId == "jp" && candidate.MultiServer.Rules.All(rule => rule.ProfileId == "jp"));
    secondApps.SelectedItems.Add(secondApps.Items[0]);
    assign.Invoke(dualWindow, new object[] { false });
    candidate = (RoutingSettings)build.Invoke(dualWindow, null);
    Check(candidate.MultiServer.Rules.Single(rule => rule.ApplicationPath != null).Target == "Default");
    Check(candidate.MultiServer.Rules.Single(rule => rule.Domain != null).ProfileId == "jp");
    secondApps.SelectedItems.Add(secondApps.Items[0]);
    assign.Invoke(dualWindow, new object[] { true });
    candidate = (RoutingSettings)build.Invoke(dualWindow, null);
    Check(candidate.MultiServer.Rules.Count(rule => rule.ApplicationPath != null) == 1 &&
     candidate.MultiServer.Rules.Single(rule => rule.ApplicationPath != null).ProfileId == "jp");
    string previous = RoutingMigrationService.Fingerprint(dualState.Routing);
    MultiServerRoutingCommitPlan.Prepare(dualState, candidate).Apply(() => true, _ => { });
    var roundTrip = FullTransfer.Copy(dualState);
    Check(roundTrip.Routing.MultiServer.SecondaryProfileId == "jp");
    MultiServerRoutingCommitPlan.PrepareDisable(dualState).Apply(() => true, _ => { });
    Check(RoutingMigrationService.Fingerprint(dualState.Routing) == previous &&
     MultiServerRoutingHistory.PreviewResume(dualState).MultiServer.SecondaryProfileId == "jp");
   }
   finally { dualWindow.Close(); }
  }
  CheckPostPublishRollback(args[0]).GetAwaiter().GetResult();
  Console.WriteLine("PASS "+count+" Windows editor/commit assertions; no window shown, no personal save or VPN");
 }
}
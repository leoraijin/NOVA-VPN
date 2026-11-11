using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using NovaVpn;

static class MultiServerRoutingHistoryTests
{
    static int checks;
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Reject(Action work,string message){bool refused=false;try{work();}catch(Exception){refused=true;}Check(refused,message);}
    static VpnProfile Node(string id)=>new VpnProfile{Id=id,Name=id,Protocol="vless",Server=id+".example.test",Port=443,UserId="11111111-1111-4111-8111-111111111111",Transport="tcp",Security="tls",Sni=id+".example.test",AddedAt=new DateTime(2026,11,6,0,0,0,DateTimeKind.Utc)};
    static AppState State(){var s=StateStore.CreateEphemeral();s.Profiles=new List<VpnProfile>{Node("fr"),Node("us")};s.SelectedProfileId="us";s.FallbackRootProfileId="us";
        s.Routing=new RoutingSettings{Mode="Direct",DefaultRoute="Direct",DnsMode="System",EnableIpv6=true,BypassLan=false,BypassPrivateDns=false,EnableSniffing=false,
            BlockAds=true,DnsProvider="Quad9",CustomDnsEndpoint="https://dns.example.test/query",DnsStrategy="PreferIPv6",StrictRoute=false,
            DirectDomains=new List<string>{"local.example.test"},ProxyDomains=new List<string>{"remote.example.test"},BlockDomains=new List<string>{"blocked.example.test"},
            DirectProcessPaths=new List<string>{@"C:\Synthetic\Local.exe"},ProxyProcessPaths=new List<string>{@"C:\Synthetic\Remote.exe"}};
        // MainWindow migrates visuals immediately after load, before any routing action.
        s.Visual.MigrateWindowsDesign();StateStore.NormalizeForSerialization(s);return s;}
    static RoutingSettings Multi(string primary="fr",long revision=1)=>new RoutingSettings{MultiServer=new MultiServerSettings{Revision=revision,DefaultProfileId=primary,Rules=new List<MultiServerRuleRecord>{new MultiServerRuleRecord{Domain="chat.example.test",Target="Profile",ProfileId="us"}}}};
    static void Enable(AppState s,RoutingSettings candidate=null){MultiServerRoutingCommitPlan.Prepare(s,candidate??Multi()).Apply(()=>true,c=>{});}
    static string RouteHash(RoutingSettings r)=>RoutingMigrationService.Fingerprint(r);
    static void RestoreAndResume()
    {
        var s=State();string oldRouting=RouteHash(s.Routing),oldState=FullStateRestorePlan.Fingerprint(s);var candidate=Multi();
        var plan=MultiServerRoutingCommitPlan.Prepare(s,candidate);Check(FullStateRestorePlan.Fingerprint(s)==oldState,"prepare does not mutate live state");
        candidate.MultiServer.DefaultProfileId="us";AppState saved=null;
        plan.Apply(()=>true,c=>{Check(s.Routing.MultiServer==null&&s.MultiServerRoutingHistory==null,"persist before live publication");saved=c;});
        Check(s.SelectedProfileId=="fr"&&s.FallbackRootProfileId=="fr","enabled primary selected atomically");
        Check(s.MultiServerRoutingHistory.SelectedProfileId=="us"&&s.MultiServerRoutingHistory.FallbackRootProfileId=="us","previous selected/fallback retained");
        Check(RouteHash(s.MultiServerRoutingHistory.SingleServerRouting)==oldRouting,"every old routing field retained");
        saved.MultiServerRoutingHistory.SingleServerRouting.DirectDomains.Add("mutated.example.test");
        Check(RouteHash(s.MultiServerRoutingHistory.SingleServerRouting)==oldRouting,"persistence callback cannot retain live backup alias");
        var editing=Multi("us",2);MultiServerRoutingCommitPlan.PrepareConnected(s,editing,"us").Apply(()=>true,c=>{});
        Check(RouteHash(s.MultiServerRoutingHistory.SingleServerRouting)==oldRouting,"editing active assignments never overwrites original single snapshot");
        Check(s.MultiServerRoutingHistory.SavedAssignments.Revision==2,"latest assignments retained");
        var before=FullStateRestorePlan.Fingerprint(s);plan=MultiServerRoutingCommitPlan.PrepareDisable(s,"us");
        Check(FullStateRestorePlan.Fingerprint(s)==before,"disable prepare leaves active assignments alone");
        plan.Apply(()=>true,c=>Check(s.Routing.MultiServer!=null,"restore persists before publishing ordinary routing"));
        Check(s.Routing.MultiServer==null&&RouteHash(s.Routing)==oldRouting,"restores exact mode/sites/apps/DNS/IPv6/options");
        Check(s.SelectedProfileId=="us"&&s.FallbackRootProfileId=="us"&&s.LastConnectedProfileId=="us","restores actual old primary and startup identity");
        Check(s.MultiServerRoutingHistory.SavedAssignments.Revision==2&&s.MultiServerRoutingHistory.SavedAssignments.DefaultProfileId=="us","disabling retains latest assignment draft");
        var preview=MultiServerRoutingHistory.PreviewResume(s);preview.MultiServer.Rules[0].ProfileId="fr";
        Check(s.Routing.MultiServer==null&&s.MultiServerRoutingHistory.SavedAssignments.Rules[0].ProfileId=="us","resume preview is a private draft");
        s.Routing.DirectDomains.Add("added-while-off.example.test");s.SelectedProfileId="fr";s.FallbackRootProfileId="fr";string newer=RouteHash(s.Routing);
        Enable(s,Multi("us",3));MultiServerRoutingCommitPlan.PrepareDisable(s).Apply(()=>true,c=>{});
        Check(RouteHash(s.Routing)==newer&&s.SelectedProfileId=="fr","new enable captures latest ordinary edits rather than stale first snapshot");
        var copied=FullTransfer.Copy(s);Check(copied.MultiServerRoutingHistory!=null&&RouteHash(copied.Routing)==newer,"disabled history survives DataContract restart snapshot");
        Check(s.SchemaVersion==17,"new persistent history uses schema17");
        plan=MultiServerRoutingCommitPlan.PrepareForgetHistory(s);before=RouteHash(s.Routing);plan.Apply(()=>true,c=>{});
        Check(s.MultiServerRoutingHistory==null&&RouteHash(s.Routing)==before&&s.SelectedProfileId=="fr","forget removes only saved mode history");
    }
    static void FailuresAndMissing(string output)
    {
        var s=State();Enable(s);string baseline=FullStateRestorePlan.Fingerprint(s);
        var plan=MultiServerRoutingCommitPlan.PrepareDisable(s);
        Reject(()=>plan.Apply(()=>true,c=>{throw new IOException("fixture write failure");}),"save failure is reported");
        Check(FullStateRestorePlan.Fingerprint(s)==baseline,"failed save preserves assignments and old backup");
        AppState disk=null;int writes=0;
        Reject(()=>plan.Apply(()=>true,c=>{disk=FullTransfer.Copy(c);if(++writes==1)throw new IOException("write completed then callback failed");}),"post-write failure is reported");
        if(writes!=2||FullStateRestorePlan.Fingerprint(disk)!=baseline) {
            foreach(var pair in new[]{new {Name="baseline",State=s},new {Name="compensated",State=disk}})using(var file=File.Create(Path.Combine(output,pair.Name+"-fixture-state.json")))
                new DataContractJsonSerializer(typeof(AppState),new DataContractJsonSerializerSettings{DateTimeFormat=new System.Runtime.Serialization.DateTimeFormat("o",System.Globalization.CultureInfo.InvariantCulture){DateTimeStyles=System.Globalization.DateTimeStyles.RoundtripKind}}).WriteObject(file,pair.State);
            Console.WriteLine("Compensation fixture writes="+writes);
        }
        Check(writes==2&&FullStateRestorePlan.Fingerprint(disk)==baseline,"post-write failure restores prior persisted state including history");
        Check(FullStateRestorePlan.Fingerprint(s)==baseline,"compensation does not publish rejected candidate");
        int attempts=0;Exception compensationError=null;
        try{plan.Apply(()=>true,c=>{attempts++;throw new IOException(attempts==1?"original write failure":"rollback write failure");});}catch(Exception error){compensationError=error;}
        Check(attempts==2&&compensationError is AggregateException&&((AggregateException)compensationError).InnerExceptions.Count==2,"rollback failure remains explicit alongside initial error");
        Check(FullStateRestorePlan.Fingerprint(s)==baseline,"failed compensation never publishes candidate in memory");
        var newer=State();Enable(newer);var oldPlan=MultiServerRoutingCommitPlan.PrepareDisable(newer);bool owner=true;int ownerWrites=0;AppState ownedDisk=null;
        Reject(()=>oldPlan.Apply(()=>owner,c=>{ownerWrites++;ownedDisk=FullTransfer.Copy(c);newer.Routing.DnsProvider="new-owner";owner=false;ownedDisk=FullTransfer.Copy(newer);throw new IOException("owner changed after write");}),"old writer reports lost ownership");
        Check(ownerWrites==1&&ownedDisk.Routing.DnsProvider=="new-owner"&&newer.Routing.DnsProvider=="new-owner","compensation cannot overwrite a newer owner");
        Reject(()=>plan.Apply(()=>false,c=>{throw new Exception("must not persist");}),"cancelled restore refuses before persistence");
        Check(FullStateRestorePlan.Fingerprint(s)==baseline,"cancel does not mutate history");
        Reject(()=>plan.Apply(()=>true,c=>c.MultiServerRoutingHistory.SingleServerRouting.DnsMode="tampered"),"modified persistence snapshot refused");
        Check(FullStateRestorePlan.Fingerprint(s)==baseline,"tampered save callback cannot publish new route/history");
        s.MultiServerRoutingHistory.SingleServerRouting.DnsProvider="edited";bool wrote=false;
        Reject(()=>plan.Apply(()=>true,c=>wrote=true),"stale backup cannot overwrite another edit");Check(!wrote,"stale restore never writes");
        Reject(()=>MultiServerRoutingCommitPlan.PrepareForgetHistory(s),"cannot forget rollback while active");
        s=State();Enable(s);s.Profiles.RemoveAll(p=>p.Id=="us");
        Reject(()=>MultiServerRoutingCommitPlan.PrepareDisable(s,"fr"),"connected restore cannot certify different original primary");
        MultiServerRoutingCommitPlan.PrepareDisable(s).Apply(()=>true,c=>{});
        Check(s.SelectedProfileId=="us"&&MultiServerRoutingHistory.MissingRestoredProfile(s),"offline restore keeps missing identity without substitution");
        s.SelectedProfileId="fr";Check(!MultiServerRoutingHistory.MissingRestoredProfile(s),"explicit user selection can resolve missing old primary");
        s.SelectedProfileId="us";s.Profiles.Add(Node("us"));s.Profiles.Single(p=>p.Id=="us").RetiredFromSubscription=true;
        Check(MultiServerRoutingHistory.MissingRestoredProfile(s),"retired original is not automatically reused");
        s=State();s.Routing=Multi();s.MultiServerRoutingHistory=null;var fallback=MultiServerRoutingHistory.PreviewDisable(s);
        Check(fallback.SingleServerRouting.MultiServer==null&&fallback.SelectedProfileId=="fr","older enabled document gets explicitly documented ordinary-mode fallback");
        Check(s.MultiServerRoutingHistory==null&&s.Routing.MultiServer!=null,"old-document preview never invents persisted history");
        s=State();Enable(s,new RoutingSettings{MultiServer=new MultiServerSettings{DefaultProfileId="fr"}});
        Check(MultiServerSettings.ProfileReferences(s).Contains("us")&&MultiServerSettings.ProfileReferences(s).Contains("fr"),"ownership protects old primary and saved assignment exits");
        MultiServerRoutingCommitPlan.PrepareDisable(s).Apply(()=>true,c=>{});
        Check(MultiServerSettings.ProfileReferences(s).Contains("fr"),"disabled assignments remain retained subscription references");
        MultiServerRoutingCommitPlan.PrepareForgetHistory(s).Apply(()=>true,c=>{});Check(MultiServerSettings.ProfileReferences(s).Length==0,"forget releases dormant assignment references");
        var history=new MultiServerRoutingHistory{SingleServerRouting=Multi(),SavedAssignments=Multi().MultiServer};
        Reject(()=>history.Validate(),"nested multi snapshot rejected");
        history.SingleServerRouting=new RoutingSettings();history.SelectedProfileId=new string('x',129);Reject(()=>history.Validate(),"invalid old-profile identity rejected");
    }
    static void RetiredTransfer(string output)
    {
        const string password = "synthetic-retirement-transfer";
        var state = State();
        Enable(state);
        state.Profiles.Single(profile => profile.Id=="us").RetiredFromSubscription = true;
        string envelope = FullTransfer.Export(state,password);
        File.WriteAllText(Path.Combine(output,"windows-retired-full.txt"),envelope);
        var imported = FullTransfer.Import(envelope,password);
        Check(imported.Profiles.Single(profile => profile.Id=="us").RetiredFromSubscription &&
            imported.Routing.MultiServer.Rules.Single().ProfileId=="us","round trip preserves retired assignment");
        var plan = CompiledRoutingPlan.CreateFor(imported.Profiles.Single(profile=>profile.Id=="fr"),imported.Routing,VpnEngine.SingBox,imported.Profiles);
        Check(plan.Explain("chat.example.test",@"C:\Fixture\Browser.exe","tcp",443).Route=="block" &&
            plan.Explain("ordinary.example.test",@"C:\Fixture\Browser.exe","tcp",443).ProfileId=="fr","retired assignment blocks while default still works");

        var json = new System.Web.Script.Serialization.JavaScriptSerializer();
        var document = (Dictionary<string,object>)json.DeserializeObject(IdentityTransfer.Open(envelope,password));
        var profiles = ((System.Collections.IEnumerable)document["profiles"]).Cast<Dictionary<string,object>>().ToArray();
        var retired = profiles.Single(profile=>Equals(profile["id"],"us"));
        var platforms = (Dictionary<string,object>)document["platforms"];
        AppState ImportEdited() => FullTransfer.Import(IdentityTransfer.Seal(json.Serialize(document),password),password);
        foreach(var row in profiles)row.Remove("retiredFromSubscription");
        File.WriteAllText(Path.Combine(output,"windows-retired-legacy-full.txt"),IdentityTransfer.Seal(json.Serialize(document),password));
        Check(ImportEdited().Profiles.Single(profile=>profile.Id=="us").RetiredFromSubscription,"older Windows envelope inherits retirement from native snapshot");
        retired["retiredFromSubscription"] = false;
        Check(!ImportEdited().Profiles.Single(profile=>profile.Id=="us").RetiredFromSubscription,"new portable availability supersedes older native metadata");
        retired["retiredFromSubscription"] = true;
        platforms.Remove("windows");
        Check(ImportEdited().Profiles.Single(profile=>profile.Id=="us").RetiredFromSubscription,"portable-only envelope preserves retirement");
        retired["retiredFromSubscription"] = "false";
        Reject(()=>ImportEdited(),"nonboolean portable availability refused");

        retired.Remove("retiredFromSubscription");
        var legacyAndroid = new Dictionary<string,object> {
            ["schema"]=2,
            ["profiles"]=new[]{new {id="fr"},new {id="us"}},
            ["subscriptions"]=new[]{new {id="fixture-source"}},
            ["memberships"]=new[]{new {profileId="fr",subscriptionId="fixture-source"}},
            ["manuallyOwned"]=Array.Empty<string>()
        };
        void UpdateForeign() => platforms["android"]=json.Serialize(new {profileDocument=json.Serialize(legacyAndroid)});
        UpdateForeign();
        var foreign=ImportEdited();
        Check(foreign.Profiles.Single(p=>p.Id=="us").RetiredFromSubscription &&
            !foreign.Profiles.Single(p=>p.Id=="fr").RetiredFromSubscription,"legacy Android ownership preserves retired and subscribed profiles");
        retired["retiredFromSubscription"]=false;
        Check(!ImportEdited().Profiles.Single(p=>p.Id=="us").RetiredFromSubscription,"current marker outranks foreign ownership");
        retired.Remove("retiredFromSubscription");
        legacyAndroid["memberships"]=new[]{new {profileId="us",subscriptionId="unknown-source"}};
        UpdateForeign();
        Reject(()=>ImportEdited(),"invalid recognized Android ownership is refused");

        var fixture=Environment.GetEnvironmentVariable("NOVA_ANDROID_LEGACY_TRANSFER");
        if(!string.IsNullOrWhiteSpace(fixture))
        {
            var actual=FullTransfer.Import(File.ReadAllText(fixture),password);
            if(!actual.Profiles.Single(p=>p.Id=="synthetic").RetiredFromSubscription ||
                actual.Profiles.Single(p=>p.Id=="owned").RetiredFromSubscription)
                throw new InvalidOperationException("Android encrypted retirement fixture availability differs.");
            Console.WriteLine("Legacy Android retirement fixture verified on Windows");
        }
    }

    static void Transfer(string output)
    {
        var s=State();Enable(s);string old=RouteHash(s.MultiServerRoutingHistory.SingleServerRouting);
        var copy=FullTransfer.Copy(s);Check(copy.MultiServerRoutingHistory!=null&&RouteHash(copy.MultiServerRoutingHistory.SingleServerRouting)==old,"active history survives serialized copy");
        string envelope=FullTransfer.Export(s,"synthetic-transfer-password-only");var imported=FullTransfer.Import(envelope,"synthetic-transfer-password-only");
        Check(imported.MultiServerRoutingHistory!=null&&RouteHash(imported.MultiServerRoutingHistory.SingleServerRouting)==old,"encrypted full transfer preserves machine-specific rollback snapshot");
        string file=Path.Combine(output,"synthetic-settings.json");PortableConfigService.ExportSettingsOnly(s,file);var common=PortableConfigService.ImportSettingsOnly(file);
        Check(common.MultiServerRoutingHistory==null&&s.MultiServerRoutingHistory!=null,"settings-only strips history without mutating source");
        Check(!File.ReadAllText(file).Contains("Local.exe"),"settings-only cannot leak paths hidden in the old backup");
        var target=State();Enable(target);int count=target.Profiles.Count;PortableConfigService.ApplySettingsOnly(target,common);
        Check(target.MultiServerRoutingHistory==null&&target.Profiles.Count==count,"common settings replace routing and discard incompatible old backup only");
        string full=Path.Combine(output,"synthetic-full.json");PortableConfigService.Export(s,full);var package=PortableConfigService.Import(full);
        Check(package.MultiServerRoutingHistory!=null,"complete Windows package retains history");
        // A malicious settings-only envelope must not smuggle a full per-machine backup.
        string raw=File.ReadAllText(full).Replace("nova-portable-config-v1","nova-settings-no-secrets-v1");
        string bad=Path.Combine(output,"synthetic-spoofed-settings.json");File.WriteAllText(bad,raw);
        Reject(()=>PortableConfigService.ImportSettingsOnly(bad),"settings-only rejects injected routing history");
        var restored=State();var restore=FullStateRestorePlan.Prepare(restored,s);restore.Apply(restored,()=>true,c=>{});
        Check(restored.MultiServerRoutingHistory!=null&&RouteHash(restored.MultiServerRoutingHistory.SingleServerRouting)==old,"full state restore publishes independent history");
    }
    static void NativeFixtures(string output)
    {
        var state=State();var profiles=MultiServerConfigBuilder.CaptureProfiles(state.Profiles);Enable(state);
        File.WriteAllText(Path.Combine(output,"synthetic-mode-enabled.json"),CoreManager.BuildPreparedConfiguration(profiles.Single(p=>p.Id=="fr"),state.Routing,VpnEngine.SingBox,profiles,Path.Combine(output,"synthetic-fakeip.db")));
        MultiServerRoutingCommitPlan.PrepareDisable(state).Apply(()=>true,c=>{});
        File.WriteAllText(Path.Combine(output,"synthetic-mode-restored.json"),CoreManager.BuildPreparedConfiguration(profiles.Single(p=>p.Id==state.SelectedProfileId),state.Routing,VpnEngine.SingBox));
        var resumed=MultiServerRoutingHistory.PreviewResume(state);resumed.DirectDomains.Clear();resumed.ProxyDomains.Clear();resumed.DirectProcessPaths.Clear();resumed.ProxyProcessPaths.Clear();resumed.Mode="Smart";resumed.DefaultRoute="Proxy";resumed.DnsMode="Secure";
        File.WriteAllText(Path.Combine(output,"synthetic-mode-resumed.json"),CoreManager.BuildPreparedConfiguration(profiles.Single(p=>p.Id==resumed.MultiServer.DefaultProfileId),resumed,VpnEngine.SingBox,profiles,Path.Combine(output,"synthetic-fakeip.db")));
        Check(state.Routing.MultiServer==null&&state.SelectedProfileId=="us","native fixture generation never re-enables live model");
    }
    static IEnumerable<Button> Buttons(DependencyObject node) {
        if(node is Button button)yield return button;
        foreach(object child in LogicalTreeHelper.GetChildren(node))if(child is DependencyObject dep)foreach(var item in Buttons(dep))yield return item;
    }
    static bool HasNamedElement(DependencyObject node,string name) {
        if(node is FrameworkElement element&&element.Name==name)return true;
        foreach(object child in LogicalTreeHelper.GetChildren(node))if(child is DependencyObject dep&&HasNamedElement(dep,name))return true;
        return false;
    }
    static void Ui()
    {
        new Application();var window=(MainWindow)Activator.CreateInstance(typeof(MainWindow),new object[]{true});
        try {
            var state=(AppState)typeof(MainWindow).GetField("state",Flags).GetValue(window);var model=State();Enable(model);
            state.Profiles=model.Profiles;state.Routing=model.Routing;state.MultiServerRoutingHistory=model.MultiServerRoutingHistory;state.SelectedProfileId=model.SelectedProfileId;
            var page=(DependencyObject)typeof(MainWindow).GetMethod("BuildMultiServerRoutingPage",Flags).Invoke(window,null);
            Check(Buttons(page).Single(b=>b.Name=="DisableMultiServerRouting").Content.ToString()=="Вернуть прежнюю маршрутизацию","active mode exposes precise restore button");
            MultiServerRoutingCommitPlan.PrepareDisable(state).Apply(()=>true,c=>{});
            page=(DependencyObject)typeof(MainWindow).GetMethod("BuildRoutingPage",Flags).Invoke(window,null);
            Check(Buttons(page).Any(b=>b.Name=="ResumeMultiServerRouting"),"ordinary mode exposes retained assignment draft");
            Check(Buttons(page).Any(b=>b.Name=="ForgetMultiServerRouting"),"ordinary mode can release retained assignments");
            state.SelectedProfileId="missing-original";
            Check(typeof(MainWindow).GetMethod("SelectedProfile",Flags).Invoke(window,null)==null,"actual UI resolver refuses arbitrary server fallback after missing restored primary");
            page=(DependencyObject)typeof(MainWindow).GetMethod("BuildRoutingPage",Flags).Invoke(window,null);
            Check(HasNamedElement(page,"RestoredServerUnavailable"),"missing restored primary remains visible after reopen, not only a temporary toast");
            var editor=new MultiServerRoutingWindow(Multi(),state.Profiles,Array.Empty<ApplicationEntry>(),"fr",new VisualPreferences());
            try{
                var ipv6=(CheckBox)typeof(MultiServerRoutingWindow).GetField("ipv6",Flags).GetValue(editor);
                Check(ipv6.IsChecked==false,"editor shows current explicit IPv6 choice");ipv6.IsChecked=true;
                var accepted=(RoutingSettings)typeof(MultiServerRoutingWindow).GetMethod("BuildCandidate",Flags).Invoke(editor,null);
                Check(accepted.EnableIpv6&&accepted.MultiServer!=null,"IPv6 choice reaches the candidate config instead of only its visual state");
            }finally{editor.Close();}
        }finally{window.Close();}
    }
    [STAThread] public static int Main(string[] args){try{string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);RestoreAndResume();FailuresAndMissing(output);Transfer(output);RetiredTransfer(output);Ui();NativeFixtures(output);Console.WriteLine("MultiServerRoutingHistoryTests: "+checks+" assertions passed; private state/transfer and unshown WPF only, no personal save, network changes or TUN.");return 0;}catch(Exception error){Console.Error.WriteLine("Checks="+checks+" "+error);return 1;}}
}

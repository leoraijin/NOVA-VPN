using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NovaVpn;

static class MultiExitHealthDisplayTests
{
    static int assertions;
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
    static IEnumerable<string> Texts(DependencyObject root) {
        if(root is TextBlock text)yield return text.Text;
        foreach(object child in LogicalTreeHelper.GetChildren(root))if(child is DependencyObject dep)foreach(string line in Texts(dep))yield return line;
    }
    [STAThread] public static int Main()
    {
        MainWindow window=null;
        try {
            new Application();
            window=(MainWindow)Activator.CreateInstance(typeof(MainWindow),new object[]{true});
            var core=(CoreManager)typeof(MainWindow).GetField("core",Flags).GetValue(window);
            var state=(AppState)typeof(MainWindow).GetField("state",Flags).GetValue(window);
            var fr=new VpnProfile{Id="fr",Name="France",Protocol="vless",Server="fr.example.test",Port=443};
            var us=new VpnProfile{Id="us",Name="USA",Protocol="vless",Server="us.example.test",Port=443};
            state.Profiles=new List<VpnProfile>{fr,us};
            state.Routing.MultiServer=new MultiServerSettings{Revision=1,DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord>{new MultiServerRuleRecord{Domain="example.test",Target="Profile",ProfileId="us"}}};
            typeof(CoreManager).GetProperty("Status").GetSetMethod(true).Invoke(core,new object[]{CoreStatus.Connected});
            typeof(CoreManager).GetProperty("LastConnectedRouting").GetSetMethod(true).Invoke(core,new object[]{RoutingOverlayService.Clone(state.Routing)});
            typeof(CoreManager).GetField("activeProfiles", Flags).SetValue(core, MultiServerConfigBuilder.CaptureProfiles(state.Profiles));
            var headline=typeof(MainWindow).GetMethod("StatusHeadline",Flags);
            Check((string)headline.Invoke(window,null)=="Проверяем назначенные серверы","initial connected group is still checking");
            var exits=new[]{new ExitProbeResult(new ExitProbeTarget("fr","France",ExitProbeTarget.Fingerprint(fr),ExitProbePhase.Queued),ExitProbePhase.Verified,15,DateTime.UtcNow,"https://example.test/"),
                new ExitProbeResult(new ExitProbeTarget("us","USA",ExitProbeTarget.Fingerprint(us),ExitProbePhase.Queued),ExitProbePhase.Failed,-1,DateTime.UtcNow)};
            var proof=new ConnectionVerification{Completed=true,InternetOk=false,DnsOk=false};
            typeof(ConnectionVerification).GetProperty("MultiExits").GetSetMethod(true).Invoke(proof,new object[]{new MultiExitHealthSnapshot(exits,"synthetic-network","synthetic-core")});
            typeof(CoreManager).GetField("verification",Flags).SetValue(core,proof);
            Check((string)headline.Invoke(window,null)=="Проверка выходов не подтверждена","one failure does not claim all internet is down or all exits are protected");
            var page=(DependencyObject)typeof(MainWindow).GetMethod("BuildMultiServerRoutingPage",Flags).Invoke(window,null);
            var text=Texts(page).ToArray();
            Check(text.Any(t=>t.Contains("France → HTTPS подтверждён")&&t.Contains("15 мс")),"successful exit has its own latency");
            Check(text.Any(t=>t.Contains("USA → HTTPS не подтверждён")),"failed exit is separately visible");
            Check(text.Contains("Проверка активного подключения"),"proof explicitly belongs to active connection");
            var refresh = typeof(MainWindow).GetMethod("UpdateMultiExitHealthPanel", Flags);
            Check(!Texts(page).Any(t => t.Contains("прежний снимок до переподключения")), "matching catalog needs no reapply");
            us.Name = "Renamed US";
            refresh.Invoke(window, null);
            Check(!Texts(page).Any(t => t.Contains("прежний снимок до переподключения")), "cosmetic rename does not require reapply");
            us.Password="changed";refresh.Invoke(window,null);
            Check(Texts(page).Any(t => t.Contains("прежний снимок до переподключения")), "changed live credentials show the reapply notice");
            Check(Texts(page).Any(t=>t.StartsWith("USA →")&&t.Contains("снимок до изменения конфига")),"changed catalog cannot silently relabel old proof");
            us.Password = null;
            us.RetiredFromSubscription = true;
            refresh.Invoke(window, null);
            Check(Texts(page).Any(t => t.Contains("прежний снимок до переподключения")), "retired live exit requires reapply");
            us.RetiredFromSubscription = false;
            refresh.Invoke(window, null);
            Check(!Texts(page).Any(t => t.Contains("прежний снимок до переподключения")), "exact restored catalog clears the notice");
            var caption=(string)typeof(MainWindow).GetMethod("StatusCaption",Flags).Invoke(window,null);
            Check(caption.Contains("1 из 2"),"aggregate caption preserves partial success");
            Check(core.Verification.MultiExits.Results[0].Phase==ExitProbePhase.Verified,"display does not erase another exit result");
            state.Routing.MultiServer.SecondaryProfileId="us";
            typeof(CoreManager).GetProperty("LastConnectedRouting").GetSetMethod(true).Invoke(core,new object[]{RoutingOverlayService.Clone(state.Routing)});
            var home=(DependencyObject)typeof(MainWindow).GetMethod("BuildHomePage",Flags).Invoke(window,null);
            Check(Texts(home).Any(t=>t.StartsWith("VPN-2 · Renamed US")),"secondary server name on home");
            Check(Texts(home).Any(t=>t.Contains("HTTPS не подтверждён")&&t.Contains("сайтов: 1")),"separate secondary failure on home");
            var update=typeof(MainWindow).GetMethod("UpdateHomeVpn2Status",Flags);
            var ok=new ExitProbeResult(new ExitProbeTarget("us","USA",ExitProbeTarget.Fingerprint(us),ExitProbePhase.Queued),ExitProbePhase.Verified,42,DateTime.UtcNow,"https://example.test/");
            var evidence=typeof(MainWindow).Assembly.GetType("NovaVpn.ProfileHealthEvidence");
            string network=(string)evidence.GetMethod("NetworkKey").Invoke(null,null);
            string coreKey=(string)evidence.GetMethod("CoreKey").Invoke(null,new object[]{core.CorePath});
            typeof(ConnectionVerification).GetProperty("MultiExits").GetSetMethod(true).Invoke(proof,new object[]{new MultiExitHealthSnapshot(new[]{exits[0],ok},network,coreKey)});
            update.Invoke(window,null);
            Check(Texts(home).Any(t=>t.Contains("42 мс")&&t.Contains("сайтов: 1")),"secondary own latency missing");
            us.Password="new-secret-fixture";update.Invoke(window,null);
            Check(Texts(home).Any(t=>t.StartsWith("Конфиг изменён")),"old success shown after secondary config changed");
            us.Password=null;
            state.Routing.MultiServer.Rules.Clear();update.Invoke(window,null);
            Check(Texts(home).Any(t=>t.StartsWith("Не используется")),"unused secondary shown as connected");
            state.Routing.MultiServer.SecondaryProfileId=null;update.Invoke(window,null);
            Check(Texts(home).Contains("VPN-2 · Сервер не выбран"),"unselected secondary not shown");
            Console.WriteLine("MultiExitHealthDisplayTests: "+assertions+" assertions passed; unshown ephemeral WPF snapshot only, not rendered UI or native TUN acceptance.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}finally{if(window!=null)window.Close();}
    }
}

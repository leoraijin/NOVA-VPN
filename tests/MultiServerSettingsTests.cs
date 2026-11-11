using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using NovaVpn;
class MultiServerSettingsTests
{
    static int count;
    static void Check(bool value) { if(!value) throw new Exception("Assertion " + (count+1)); count++; }
    static T Roundtrip<T>(T value) { using(var stream=new MemoryStream()) { var codec=new DataContractJsonSerializer(typeof(T));codec.WriteObject(stream,value);stream.Position=0;return (T)codec.ReadObject(stream); } }
    static bool Refuses(Action action) { try { action();return false; } catch(Exception) { return true; } }
    static void Main(string[] args)
    {
        var settings=new MultiServerSettings { Revision=7,DefaultProfileId="fr",Rules=new List<MultiServerRuleRecord> {
            new MultiServerRuleRecord {ApplicationPath=@"C:\Apps\ChatGPT.exe",Target="Profile",ProfileId="us"},
            new MultiServerRuleRecord {Domain="example.com",IncludeSubdomains=true,Target="Direct"} } };
        var routes=new RoutingSettings {MultiServer=settings};
        var restored=Roundtrip(routes);
        Check(restored.MultiServer.DefaultProfileId=="fr");
        Check(restored.MultiServer.Rules[0].ProfileId=="us");
        Check(restored.MultiServer.Revision==7);
        Check(restored.MultiServer.Compile(new[]{"fr","us"}).Decide(@"C:\Apps\ChatGPT.exe",null).Effective.ProfileId=="us");
        Check(restored.MultiServer.Compile(new[]{"fr"}).Decide(@"C:\Apps\ChatGPT.exe",null).Reason=="missing-profile");
        var clone=RoutingOverlayService.Clone(routes);
        settings.Rules[0].ProfileId="changed";
        Check(clone.MultiServer.Rules[0].ProfileId=="us");
        Check(!object.ReferenceEquals(clone.MultiServer.Rules,routes.MultiServer.Rules));
        Check(Roundtrip(new RoutingSettings()).MultiServer==null);
        var invalid=clone.MultiServer.Copy();invalid.Schema=9;Check(Refuses(()=>Roundtrip(invalid)));
        invalid=clone.MultiServer.Copy();invalid.Rules[0].Target="1";Check(Refuses(()=>invalid.Validate()));
        invalid=clone.MultiServer.Copy();invalid.Rules.Add(invalid.Rules[0].Copy());Check(Refuses(()=>invalid.Validate()));
        invalid=clone.MultiServer.Copy();invalid.RequiresRebinding=true;Check(Refuses(()=>Roundtrip(invalid).Compile(new[]{"fr","us"})));
        var fixture=Path.Combine(args[0],"settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var state=new AppState {Routing=clone};
        var full=Path.Combine(fixture,"full.json");PortableConfigService.Export(state,full);
        Check(PortableConfigService.Import(full).Routing.MultiServer.Rules[0].ProfileId=="us");
        var portable=Path.Combine(fixture,"settings.json");PortableConfigService.ExportSettingsOnly(state,portable);
        var imported=PortableConfigService.ImportSettingsOnly(portable);
        Check(imported.Routing.MultiServer.RequiresRebinding);
        Check(imported.Routing.MultiServer.Rules[0].ProfileId=="us");
        Check(!state.Routing.MultiServer.RequiresRebinding);
        Check(Refuses(()=>imported.Routing.MultiServer.Compile(new[]{"fr","us"})));
        imported.Routing.MultiServer.RequiresRebinding=false;
        var target=new AppState();PortableConfigService.ApplySettingsOnly(target,imported);
        Check(target.Routing.MultiServer.RequiresRebinding);
        Check(!imported.Routing.MultiServer.RequiresRebinding);
        Check(Refuses(()=>ConfigBuilder.Build(new VpnProfile(),clone)));
        Check(Refuses(()=>RealityConfigBuilder.Build(new VpnProfile(),clone)));
        Console.WriteLine("PASS "+count+" actual Windows persistence/portable/clone assertions");
    }
}
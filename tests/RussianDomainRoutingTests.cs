using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using NovaVpn;

internal static class RussianDomainRoutingTests
{
    private const string App = @"C:\Apps\browser.exe";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int assertions;

    public static void Main(string[] args)
    {
        string directory = args[0];
        var primary = Profile("primary");
        var secondary = Profile("secondary");
        foreach (string mode in new[] { "Global", "Smart", "Direct" })
        {
            var routing = new RoutingSettings { Mode = mode, EnableSniffing = false };
            routing.ProxyProcessPaths.Add(App);
            var plan = CompiledRoutingPlan.CreateFor(primary, routing, VpnEngine.SingBox);
            CheckRoute(plan, "SITE.EXAMPLE.RU.", "direct");
            CheckRoute(plan, "https://site.example.ru./path", "direct");
            CheckRoute(plan, "site.example.ru.example.net", "proxy");
            CheckRoute(plan, "siteru", "proxy");
            CheckRoute(plan, "ru", "proxy");
            CheckRoute(plan, "пример.рф", "proxy");

            routing.DirectDomains.Add("example.ru");
            routing.ProxyDomains.AddRange(new[] { "vpn.example.ru", "*.vpn-child.example.ru", "blocked.example.ru", "пример.ru" });
            routing.BlockDomains.Add("blocked.example.ru");
            plan = CompiledRoutingPlan.CreateFor(primary, routing, VpnEngine.SingBox);
            CheckRoute(plan, "default.example.ru", "direct");
            CheckRoute(plan, "vpn.example.ru", "proxy");
            CheckRoute(plan, "x.vpn.example.ru", "direct");
            CheckRoute(plan, "x.vpn-child.example.ru", "proxy");
            CheckRoute(plan, "blocked.example.ru", "block");
            CheckRoute(plan, "https://пример.ru/", "proxy");
            CheckRoute(plan, "xn--e1afmkfd.ru", "proxy");
            Save(directory, "single-" + mode, ConfigBuilder.Build(primary, routing));
        }

        var basic = new RoutingSettings { BypassPrivateDns = false };
        CheckRoute(CompiledRoutingPlan.CreateFor(primary, basic, VpnEngine.Reality), "unassigned.ru", "direct");
        basic.ProxyDomains.Add("vpn.example.ru");
        basic.BlockDomains.Add("blocked.example.ru");
        foreach (bool ipv6 in new[] { false, true })
        {
            basic.EnableIpv6 = ipv6;
            var plan = CompiledRoutingPlan.CreateFor(primary, basic, VpnEngine.Reality);
            CheckRoute(plan, "vpn.example.ru", "proxy");
            CheckRoute(plan, "blocked.example.ru", "block");
            Save(directory, "reality-single-" + ipv6, RealityConfigBuilder.Build(primary, basic));
        }

        var imported = new VpnProfile { Protocol = "sing-box", RawJson = @"{
            ""inbounds"":[{""type"":""tun"",""tag"":""tun"",""address"":[""172.19.0.1/30""]}],
            ""outbounds"":[{""type"":""socks"",""tag"":""remote"",""server"":""127.0.0.1"",""server_port"":1}],
            ""route"":{""rules"":[{""domain"":[""blocked.example.ru""],""action"":""reject""}],""final"":""remote""}
        }" };
        var importedRouting = new RoutingSettings();
        CheckRoute(CompiledRoutingPlan.CreateFor(imported, importedRouting, VpnEngine.SingBox), "unassigned.ru", "direct");
        importedRouting.ProxyDomains.AddRange(new[] { "vpn.example.ru", "blocked.example.ru" });
        var importedPlan = CompiledRoutingPlan.CreateFor(imported, importedRouting, VpnEngine.SingBox);
        CheckRoute(importedPlan, "vpn.example.ru", "proxy");
        CheckRoute(importedPlan, "blocked.example.ru", "block");
        Save(directory, "imported", ConfigBuilder.Build(imported, importedRouting));
        imported.RawJson = imported.RawJson.Replace(@"{""type"":""tun"",""tag"":""tun"",""address"":[""172.19.0.1/30""]}",
            @"{""type"":""mixed"",""tag"":""proxy-in"",""listen"":""127.0.0.1"",""listen_port"":17090}");
        CheckRoute(CompiledRoutingPlan.CreateFor(imported, importedRouting, VpnEngine.SingBox), "unassigned.ru", "direct");
        CheckRoute(CompiledRoutingPlan.CreateFor(imported, importedRouting, VpnEngine.SingBox), "vpn.example.ru", "proxy");
        Save(directory, "imported-proxy", ConfigBuilder.Build(imported, importedRouting));

        var multi = new RoutingSettings { MultiServer = new MultiServerSettings {
            DefaultProfileId = primary.Id, SecondaryProfileId = secondary.Id,
            Rules = new List<MultiServerRuleRecord> {
                new MultiServerRuleRecord { ApplicationPath = App, Target = "Profile", ProfileId = secondary.Id },
                new MultiServerRuleRecord { Domain = "vpn.example.ru", Target = "Profile", ProfileId = secondary.Id },
                new MultiServerRuleRecord { Domain = "vpn.example.ru", Target = "Direct" },
                new MultiServerRuleRecord { Domain = "dns.example.ru", Target = "Direct" },
                new MultiServerRuleRecord { Domain = "blocked.example.ru", Target = "Block" },
                new MultiServerRuleRecord { Domain = "blocked.example.ru", Target = "Default" },
                new MultiServerRuleRecord { Domain = "vpn-main.example.ru", Target = "Default" },
            }
        } };
        var profiles = new[] { primary, secondary };
        foreach (bool paused in new[] { false, true })
        {
            multi.MultiServer.SecondaryPaused = paused;
            string wanted = paused ? primary.Id : secondary.Id;
            var policy = multi.MultiServer.Compile(profiles.Select(profile => profile.Id));
            Check(policy.Decide(App, "site.ru").Effective.Kind == ServerRouteKind.Direct, "model direct");
            Check(policy.Decide(null, "site.ru", false).Effective.Kind == ServerRouteKind.Direct, "model unknown process");
            Check(policy.Decide(App, "vpn.example.ru").Effective.ProfileId == wanted, "model VPN override");
            foreach (var engine in new[] { VpnEngine.SingBox, VpnEngine.Reality })
            {
                var plan = CompiledRoutingPlan.CreateFor(primary, multi, engine, profiles);
                CheckRoute(plan, "site.ru", "direct");
                CheckRoute(plan, "site.ru.example.net", "proxy", wanted);
                CheckRoute(plan, "vpn.example.ru", "proxy", wanted);
                CheckRoute(plan, "vpn-main.example.ru", "proxy", primary.Id);
                CheckRoute(plan, "blocked.example.ru", "block");
                string config = CoreManager.BuildPreparedConfiguration(primary, multi, engine, profiles, Path.Combine(directory, "fake.db"));
                var root = Json.Deserialize<Dictionary<string, object>>(config);
                var dns = (Dictionary<string, object>)root["dns"];
                var rules = ((IEnumerable)dns["rules"]).Cast<Dictionary<string, object>>().ToArray();
                var russianLookups = rules.Where(rule => rule.TryGetValue("domain_suffix", out object values) &&
                    ((IEnumerable)values).Cast<string>().SequenceEqual(new[] { ".ru" })).ToArray();
                Check(russianLookups.Length == 2 && RussianResolver(russianLookups[0], engine) &&
                    Equals(russianLookups[1]["server"], "nova-bootstrap-dns"), "multi DNS routing");
                Save(directory, "multi-" + engine + "-" + paused, config);
            }
        }
        Check(multi.MultiServer.Rules.Count == 7 && multi.MultiServer.Rules[1].ProfileId == secondary.Id,
            "saved routes stay in their original lists");
        Console.WriteLine("PASS: " + assertions + " Russian domain routing assertions.");
    }

    private static bool RussianResolver(Dictionary<string, object> lookup, VpnEngine engine) =>
        Equals(lookup["server"], engine == VpnEngine.SingBox ? "nova-bootstrap-dns" : "nova-fakeip");

    private static VpnProfile Profile(string id) => new VpnProfile {
        Id = id, Protocol = "vless", Server = "127.0.0.1", Port = 1, Transport = "tcp", Security = "tls",
        Sni = id + ".example.test", UserId = "00000000-0000-4000-8000-000000000001"
    };

    private static void Save(string directory, string name, string config) =>
        File.WriteAllText(Path.Combine(directory, name + ".json"), config);

    private static void CheckRoute(CompiledRoutingPlan plan, string host, string route, string profile = null)
    {
        var decision = plan.Explain(host, App, "tcp", 443);
        Check(decision.Route == route && (profile == null || profile == decision.ProfileId),
            host + " expected " + (profile ?? route) + " got " + decision.Route + " " + decision.ProfileId + ": " + decision.Reason);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        assertions++;
    }
}

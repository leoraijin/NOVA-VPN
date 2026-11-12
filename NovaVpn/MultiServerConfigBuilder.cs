using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace NovaVpn;

// One ordinary desktop TUN with per-assignment exits. Native check remains mandatory.
public static class MultiServerConfigBuilder
{
    static Dictionary<string,object> Obj(params object[] pairs) { var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d; }
    static IEnumerable<Dictionary<string,object>> Items(object value) => ((IEnumerable)value).Cast<object>().Cast<Dictionary<string,object>>();
    static Dictionary<string,object> Copy(Dictionary<string,object> value) => new Dictionary<string,object>(value);
    public static IDictionary<string,RoutingExitIdentity> ExitIdentities(MultiServerSettings settings,IEnumerable<VpnProfile> profiles)
    {
        var entries=profiles.ToArray();
        var referenced=new HashSet<string>(settings.Compile(entries.Select(p=>p.Id)).ReferencedProfileIds,StringComparer.Ordinal);
        return entries.Where(p=>!p.RetiredFromSubscription&&referenced.Contains(p.Id)).OrderBy(p=>p.Id,StringComparer.Ordinal)
            .Select((p,i)=>new {Tag="nova-exit-"+i,Identity=new RoutingExitIdentity(p.Id,p.Name)})
            .ToDictionary(p=>p.Tag,p=>p.Identity,StringComparer.Ordinal);
    }
    internal static bool CatalogChanged(MultiServerSettings settings, IEnumerable<VpnProfile> applied, IEnumerable<VpnProfile> current)
    {
        var referenced = new HashSet<string>(settings.Compile(Array.Empty<string>()).ReferencedProfileIds, StringComparer.Ordinal);
        Dictionary<string, string> Available(IEnumerable<VpnProfile> profiles) => profiles
            .Where(profile => !profile.RetiredFromSubscription && referenced.Contains(profile.Id))
            .ToDictionary(profile => profile.Id, SubscriptionManager.ConfigKey, StringComparer.Ordinal);
        var previous = Available(applied);
        var latest = Available(current);
        return previous.Count != latest.Count || previous.Any(item =>
            !latest.TryGetValue(item.Key, out string fingerprint) || fingerprint != item.Value);
    }

    internal static void RequireCurrentCatalog(MultiServerSettings settings, IEnumerable<VpnProfile> validated, IEnumerable<VpnProfile> current)
    {
        if (CatalogChanged(settings, validated, current))
            throw new InvalidOperationException("Назначенные серверы изменились после проверки. Повторите применение.");
    }

    public static VpnProfile[] CaptureProfiles(IEnumerable<VpnProfile> profiles)
    {
        var entries=(profiles??throw new InvalidOperationException("Нет снимка серверов.")).ToArray();
        if(entries.Length>2000)throw new ArgumentException("Слишком много серверов.");
        using(var stream=new MemoryStream()) {
            var codec=new System.Runtime.Serialization.DataContractSerializer(typeof(VpnProfile[]),new System.Runtime.Serialization.DataContractSerializerSettings {MaxItemsInObjectGraph=1000000});
            codec.WriteObject(stream,entries);stream.Position=0;return (VpnProfile[])codec.ReadObject(stream);
        }
    }
    public static string Build(RoutingSettings routing, IEnumerable<VpnProfile> availableProfiles, string cachePath)
    {
        if(routing?.MultiServer==null)throw new ArgumentException("Нет назначений серверов.");
        var settings=routing.MultiServer.Copy();
        var snapshot=(availableProfiles??throw new ArgumentNullException(nameof(availableProfiles))).ToArray();
        if(snapshot.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Id))||snapshot.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=snapshot.Length)throw new ArgumentException("Некорректные ID серверов.");
        var available=snapshot.Where(p=>!p.RetiredFromSubscription).ToArray();
        var policy=settings.Compile(available.Select(p=>p.Id));
        if(!Path.IsPathRooted(cachePath)||Path.GetPathRoot(cachePath).Length<3)throw new ArgumentException("Нужен приватный абсолютный путь DNS-кэша.");
        if(routing.MigratedPolicy!=null||routing.Gaming!=null)throw new NotSupportedException("Сначала завершите временный режим или преобразуйте перенесённую политику.");
        if(new[]{routing.DirectDomains,routing.ProxyDomains,routing.DirectProcessPaths,routing.ProxyProcessPaths}.Any(list=>list?.Count>0))throw new NotSupportedException("Перенесите прежние правила в назначения серверов.");
        if(string.Equals(routing.DnsMode,"System",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Назначения серверов требуют защищённого DNS.");
        var referenced=new HashSet<string>(policy.ReferencedProfileIds,StringComparer.Ordinal);
        var selected=available.Where(p=>referenced.Contains(p.Id)).OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
        if(selected.Any(p=>p.Protocol=="sing-box"||CorporateProxy.Enabled(p)))throw new NotSupportedException("Полные JSON и корпоративные зависимости требуют отдельной поддержки назначений.");
        var advanced = ConfigBuilder.BuildAdvancedRules(routing.AdvancedRules).ToArray();
        var clean=RoutingOverlayService.Clone(routing);clean.MultiServer=null;
        clean.AdvancedRules.Clear();
        clean.DirectProcesses.Clear();
        clean.ProxyProcesses.Clear();
        var codec=new JavaScriptSerializer {MaxJsonLength=int.MaxValue};
        // The ordinary builder supplies only the shared inbound/DNS template.
        // With no usable exit, never read a retired profile's credentials or raw
        // graph. All placeholder outbounds are replaced below; missing targets
        // compile to explicit reject rules, including the default.
        var basis=selected.FirstOrDefault(p=>p.Id==settings.DefaultProfileId)??selected.FirstOrDefault()
            ??new VpnProfile {Protocol="vless",Server="routing-template.invalid",Port=443,
                UserId="00000000-0000-4000-8000-000000000001",Transport="tcp",Security="none"};
        var config=codec.Deserialize<Dictionary<string,object>>(ConfigBuilder.Build(basis,clean));
        var tags=selected.Select((p,i)=>new {p.Id,Tag="nova-exit-"+i}).ToDictionary(p=>p.Id,p=>p.Tag,StringComparer.Ordinal);
        var outbounds=new List<object>{Obj("type","direct","tag","nova-direct","domain_resolver","nova-bootstrap-dns")};
        foreach(var profile in selected) {
            var outbound=codec.Deserialize<Dictionary<string,object>>(ConfigBuilder.ExportBaseOutbound(profile));
            if(outbound.ContainsKey("detour"))throw new NotSupportedException("Зависимый outbound требует отдельной поддержки.");
            outbound["tag"]=tags[profile.Id];outbound["domain_resolver"]="nova-bootstrap-dns";outbounds.Add(outbound);
        }
        config["outbounds"]=outbounds.ToArray();
        var dns=(Dictionary<string,object>)config["dns"];
        var template=Items(dns["servers"]).First(s=>Equals(s["tag"],"secure-dns"));
        var servers=new List<object>{Obj("type","local","tag","nova-bootstrap-dns"),Obj("type","fakeip","tag","nova-fakeip","inet4_range","198.19.0.0/16","inet6_range","fc00::/18")};
        foreach(var tag in tags.Values.Concat(new[]{"nova-direct"})) {var server=Copy(template);server["tag"]="dns-"+tag;server["detour"]=tag;server["domain_resolver"]="nova-bootstrap-dns";servers.Add(server);}
        var previousRoute=(Dictionary<string,object>)config["route"];
        // FakeIP already restored the real hostname. An outer ECH SNI must not replace it.
        var rules = new List<object>
        {
            Obj("domain_regex", new[] { ".+" }, "port", new[] { 53 }, "action", "sniff", "sniffer", new[] { "dns" }, "timeout", "300ms"),
            Obj("domain_regex", new[] { ".+" }, "invert", true, "action", "sniff", "timeout", "300ms"),
            Obj("protocol", "dns", "action", "hijack-dns")
        };
        // Preserve existing explicit blocks/ad blocks from the ordinary builder.
        rules.AddRange(Items(previousRoute["rules"]).Where(r=>r.TryGetValue("action",out object a)&&Equals(a,"reject")));
        rules.AddRange(advanced.Where(rule => Equals(rule["action"], "reject")));
        if(!routing.EnableIpv6)rules.Add(Obj("ip_version",6,"action","reject"));
        Dictionary<string,object> Selector(MultiServerRule rule)
        {
            // sing-box's literal process_path matcher is case-sensitive on Windows.
            var match = rule.Domain == null
                ? (rule.ApplicationName != null ? Obj("process_path_regex", new[] {ApplicationRoutingService.ExactNameRegex(rule.ApplicationName)}) : Obj("process_path_regex", new[] { ApplicationRoutingService.ExactPathRegex(rule.ApplicationPath) }))
                : rule.SubdomainsOnly ? Obj("domain_suffix", new[] { rule.Domain }) : Obj("domain", new[] { rule.Domain });
            if (rule.IncludeSubdomains) match["domain_suffix"] = new[] { "." + rule.Domain };
            return match;
        }
        var ordered=settings.EffectiveRules().OrderBy(r=>r.Target.Kind==ServerRouteKind.Block?0:r.Domain!=null?1:r.ApplicationPath!=null?2:3).ThenByDescending(r=>r.Domain?.Length??0).ThenBy(r=>r.IncludeSubdomains).ToArray();
        var dnsRules=new List<object>();
        // Reject explicit blocks before local resolution or FakeIP allocation.
        foreach (var rule in ordered.Where(rule => rule.Target.Kind == ServerRouteKind.Block))
        {
            var query = Selector(rule);
            query["action"] = "reject";
            dnsRules.Add(query);
        }
        bool UsesLocalDns(MultiServerRule rule) =>
            routing.BypassPrivateDns && (rule.Domain == "localhost" ||
                rule.Domain?.EndsWith(".local", StringComparison.Ordinal) == true);
        if (routing.BypassPrivateDns)
        {
            var localMatch = Obj("domain", new[] { "localhost" }, "domain_suffix", new[] { ".local" });
            var assigned = ordered.Where(rule => UsesLocalDns(rule) && rule.Target.Kind != ServerRouteKind.Block).ToArray();
            // Retain the hostname via FakeIP for sites with an explicit route.
            if (assigned.Length != 0)
            {
                var excluded = Obj("invert", true);
                var exact = assigned.Where(rule => !rule.SubdomainsOnly).Select(rule => rule.Domain).ToArray();
                if (exact.Length != 0) excluded["domain"] = exact;
                var suffixes = assigned.Where(rule => rule.SubdomainsOnly || rule.IncludeSubdomains)
                    .Select(rule => rule.SubdomainsOnly ? rule.Domain : "." + rule.Domain).ToArray();
                if (suffixes.Length != 0) excluded["domain_suffix"] = suffixes;
                localMatch = Obj("type", "logical", "mode", "and", "rules", new[] { localMatch, excluded });
            }
            localMatch["action"] = "route";
            localMatch["server"] = "nova-bootstrap-dns";
            dnsRules.Add(localMatch);
        }
        dnsRules.Add(Obj("query_type",new[]{"HTTPS","SVCB"},"action","predefined","rcode","NOERROR"));
        if(!routing.EnableIpv6)dnsRules.Add(Obj("query_type","AAAA","action","predefined","rcode","NOERROR"));
        dnsRules.Add(Obj("query_type",new[]{"A","AAAA"},"action","route","server","nova-fakeip"));
        string Exit(ServerRouteTarget target) {if(target.Kind==ServerRouteKind.Block)return null;if(target.Kind==ServerRouteKind.Direct)return "nova-direct";var id=target.Kind==ServerRouteKind.Default?settings.DefaultProfileId:target.ProfileId;return tags.TryGetValue(id,out string tag)?tag:null;}
        void Append(Dictionary<string,object> match,ServerRouteTarget target,string resolver = null) {
            string tag=Exit(target);
            if(tag==null){var block=Copy(match);block["action"]="reject";rules.Add(block);return;}
            var resolve=Copy(match);resolve["action"]="resolve";resolve["server"]=resolver??"dns-"+tag;resolve["strategy"]=routing.EnableIpv6?"prefer_ipv4":"ipv4_only";rules.Add(resolve);
            var route=Copy(match);route["action"]="route";route["outbound"]=tag;rules.Add(route);
        }
        void AppendForwardedDns(Dictionary<string,object> match, ServerRouteTarget target, string resolver = null)
        {
            // A/AAAA are answered by FakeIP above.
            if (target.Kind == ServerRouteKind.Block) return;
            var query = Copy(match);
            string tag = Exit(target);
            query["action"] = tag == null ? "reject" : "route";
            if (tag != null) query["server"] = resolver ?? "dns-" + tag;
            dnsRules.Add(query);
        }
        void AppendAssignment(MultiServerRule rule)
        {
            var match = Selector(rule);
            string resolver = UsesLocalDns(rule) && rule.Target.Kind == ServerRouteKind.Direct ? "nova-bootstrap-dns" : null;
            Append(match, rule.Target, resolver);
            AppendForwardedDns(match, rule.Target, resolver);
        }
        foreach (var rule in ordered.Where(rule => rule.Target.Kind == ServerRouteKind.Block || rule.Domain != null))
            AppendAssignment(rule);

        var zapretSites = (routing.ZapretDirectDomains ?? new List<string>())
            .Select(MultiServerRule.NormalizeDomainSelector).Distinct(StringComparer.Ordinal);
        foreach (string site in zapretSites)
            AppendAssignment(new MultiServerRule(null, site, false, new ServerRouteTarget(ServerRouteKind.Direct)));

        foreach (var rule in ordered.Where(rule => rule.ApplicationPath != null && rule.Target.Kind != ServerRouteKind.Block))
            AppendAssignment(rule);

        if (ordered.Any(rule => rule.ApplicationPath != null || rule.ApplicationName != null))
        {
            var unknownOwner = Obj("process_path_regex", new[] { ".+" }, "invert", true, "action", "reject");
            rules.Add(unknownOwner);
            dnsRules.Add(Copy(unknownOwner));
        }
        // LAN bypass is the fallback for unassigned destinations, after explicit rules and owner checks.
        if(routing.BypassLan)rules.Add(Obj("ip_is_private",true,"action","route","outbound","nova-direct"));
        // Legacy port routes apply only to traffic without an explicit assignment.
        foreach (var advancedRule in advanced.Where(rule => Equals(rule["action"], "route")))
        {
            var match = Copy(advancedRule);
            match.Remove("action");
            match.Remove("outbound");
            var target = Equals(advancedRule["outbound"], "direct") ? ServerRouteKind.Direct : ServerRouteKind.Default;
            Append(match, new ServerRouteTarget(target));
        }
        void AppendProcessNames(IEnumerable<string> names, ServerRouteKind kind)
        {
            var match = ConfigBuilder.ProcessNameSelector(names);
            if (match == null) return;
            var target = new ServerRouteTarget(kind);
            Append(match, target);
            AppendForwardedDns(match, target);
        }
        AppendProcessNames(routing.DirectProcesses, ServerRouteKind.Direct);
        foreach (var rule in ordered.Where(rule => rule.ApplicationName != null && rule.Target.Kind != ServerRouteKind.Block))
            AppendAssignment(rule);
        AppendProcessNames(routing.ProxyProcesses, ServerRouteKind.Default);
        Append(Obj(),new ServerRouteTarget(ServerRouteKind.Default));
        string final=Exit(new ServerRouteTarget(ServerRouteKind.Default));
        if(final==null)dnsRules.Add(Obj("action","reject"));
        config["route"]=Obj("auto_detect_interface",true,"default_domain_resolver","nova-bootstrap-dns","rules",rules.ToArray(),"final",final??"nova-direct");
        config["dns"]=Obj("servers",servers.ToArray(),"rules",dnsRules.ToArray(),"final",final==null?"nova-bootstrap-dns":"dns-"+final,"independent_cache",true);
        config["experimental"]=Obj("cache_file",Obj("enabled",true,"path",cachePath,"store_fakeip",true));
        // Capture IPv6 even when disabled, so the reject rule can handle it.
        Items(config["inbounds"]).Single()["address"]=new[]{"172.19.0.1/30","fdfe:dcba:9876::1/126"};
        return codec.Serialize(config);
    }
}

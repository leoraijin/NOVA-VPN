using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Interpret the ordered rules emitted by the same compiler that starts the core.
// This avoids a second, diverging implementation of rule priority in the UI.
public sealed class CompiledRoutingPlan
{
    private readonly Dictionary<string, object> config;
    private readonly string capturedInbound;
    private readonly Dictionary<string,RoutingExitIdentity> exits;
    private readonly string[] fakeIpRanges;
    private CompiledRoutingPlan(Dictionary<string, object> config, string capturedInbound = null,
        IDictionary<string, RoutingExitIdentity> exits = null, string[] fakeIpRanges = null)
    {
        this.config = config;
        this.capturedInbound = capturedInbound;
        this.exits = exits == null ? new Dictionary<string, RoutingExitIdentity>() :
            new Dictionary<string, RoutingExitIdentity>(exits, StringComparer.Ordinal);
        this.fakeIpRanges = fakeIpRanges ?? CaptureFakeIpRanges(config);
    }

    private static string[] CaptureFakeIpRanges(Dictionary<string, object> config)
    {
        if (!config.TryGetValue("dns", out var value) || !(value is Dictionary<string, object> dns))
            return Array.Empty<string>();
        return Values(dns, "servers").OfType<Dictionary<string, object>>()
            .Where(server => Text(server, "type") == "fakeip")
            .SelectMany(server => new[] { Text(server, "inet4_range"), Text(server, "inet6_range") })
            .Where(range => !string.IsNullOrWhiteSpace(range)).ToArray();
    }

    public static CompiledRoutingPlan Create(RoutingSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var placeholder = new VpnProfile { Protocol = "vless", Server = "routing-preview.invalid", Port = 443,
            UserId = "00000000-0000-4000-8000-000000000001", Transport = "tcp", Security = "none" };
        return new CompiledRoutingPlan(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
            .Deserialize<Dictionary<string, object>>(ConfigBuilder.Build(placeholder, settings)));
    }

    public static CompiledRoutingPlan CreateFor(VpnProfile profile, RoutingSettings settings, VpnEngine engine, IEnumerable<VpnProfile> profiles = null)
    {
        if (settings?.MultiServer != null) {
            var frozen=RoutingOverlayService.Clone(settings);
            var snapshot=MultiServerConfigBuilder.CaptureProfiles(profiles);
            var jsonMulti=CoreManager.BuildPreparedConfiguration(profile,frozen,engine,snapshot,Path.Combine(StateStore.DirectoryPath,"multi-server-dns.db"));
            return CaptureWithExits(jsonMulti,MultiServerConfigBuilder.ExitIdentities(frozen.MultiServer,snapshot));
        }
        if (profile == null) return Create(settings);
        string json = CoreManager.BuildPreparedConfiguration(profile,settings,engine,profiles,Path.Combine(StateStore.DirectoryPath,"multi-server-dns.db"));
        return new CompiledRoutingPlan(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json));
    }

    internal static CompiledRoutingPlan TryCapture(string json)
    {
        try{return Capture(json);}
        catch(Exception error)when(error is ArgumentException||error is InvalidOperationException||error is FormatException){return null;}
    }
    internal static CompiledRoutingPlan Capture(string json) => CaptureWithExits(json,null);
    internal static CompiledRoutingPlan TryCaptureWithExits(string json,IDictionary<string,RoutingExitIdentity> exits)
    {
        try{return CaptureWithExits(json,exits);}
        catch(Exception error)when(error is ArgumentException||error is InvalidOperationException||error is FormatException){return null;}
    }
    internal static CompiledRoutingPlan CaptureWithExits(string json,IDictionary<string,RoutingExitIdentity> exits)
    {
        var root=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<Dictionary<string,object>>(json);
        if(root==null)throw new InvalidOperationException("Нет объекта конфигурации для снимка.");
        var frozen=new Dictionary<string,object>();
        if(root.TryGetValue("route",out var routeValue)){
            if(routeValue is Dictionary<string,object> route){var projection=new Dictionary<string,object>();foreach(string key in new[]{"rules","final"})if(route.TryGetValue(key,out var value))projection[key]=value;frozen["route"]=projection;}
            else frozen["route"]=routeValue;
        }
        // Retain interpretation fields only, never native proxy passwords/controller secrets.
        foreach(string key in new[]{"outbounds","endpoints"}) if(root.ContainsKey(key))frozen[key]=Values(root,key).Select(value=>value is Dictionary<string,object> outbound?(object)new Dictionary<string,object>{{"type",Text(outbound,"type")},{"tag",Text(outbound,"tag")}}:value).ToArray();
        var tun=Values(root,"inbounds").Select((value,index)=>new{Value=value as Dictionary<string,object>,Index=index}).Where(item=>item.Value!=null&&Text(item.Value,"type")=="tun").ToArray();
        string inbound=tun.Length==1?(Text(tun[0].Value,"tag")==""?tun[0].Index.ToString(System.Globalization.CultureInfo.InvariantCulture):Text(tun[0].Value,"tag")):null;
        return new CompiledRoutingPlan(frozen, inbound, exits, CaptureFakeIpRanges(root));
    }

    public RoutingDecision Explain(string destination, string process, string network, int? port,string inbound=null)
    {
        string host = RoutingInspector.NormalizeDomainRule(destination);
        if (string.IsNullOrWhiteSpace(host)) return Unknown("Введите адрес назначения.");
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host.Trim('[', ']');
            if (!port.HasValue && uri.Port > 0) port = uri.Port;
        }
        if (port.HasValue && (port < 1 || port > 65535)) return Unknown("Порт должен быть от 1 до 65535.");
        if (IPAddress.TryParse(host, out var destinationIp) && fakeIpRanges.Any(range => InSubnet(destinationIp, range)))
            return Unknown("Это адрес FakeIP. Для проверки маршрута введите исходный домен назначения.");
        network = (network ?? "").Trim().ToLowerInvariant(); process = (process ?? "").Trim();inbound=inbound??capturedInbound??"";
        if (network != "" && network != "tcp" && network != "udp") return Unknown("Укажите TCP, UDP или оставьте транспорт неизвестным.");
        config.TryGetValue("route",out var routeValue);var route=routeValue as Dictionary<string,object>;
        if(routeValue!=null&&route==null)return Unknown("Некорректный раздел маршрутизации в полном конфиге.");
        if(route==null)route=new Dictionary<string,object>();
        bool dnsAssumption = false;
        int index = 0;
        string routeOptionsReason = "";
        string runtimeReason = "";
        foreach (var rule in Values(route, "rules").OfType<Dictionary<string, object>>())
        {
            index++;
            // Pinned sing-box RuleAction.UnmarshalJSON defaults missing/empty action to route.
            string action = Text(rule, "action");if(action=="")action="route";
            if (action == "sniff" || action == "resolve")
            {
                // These options configure the action; they are not extra match predicates.
                var predicate = new Dictionary<string,object>(rule);
                foreach (string key in action == "sniff" ? new[]{"sniffer","timeout","override_destination"} : new[]{"server","strategy","disable_cache","rewrite_ttl","client_subnet"}) predicate.Remove(key);
                if (action == "resolve" && IPAddress.TryParse(host, out var literal)) continue;
                if (Match(predicate, host, process, network, port, inbound, ref dnsAssumption) != false)
                {
                    string note = action == "sniff" ? " Результат распознавания содержимого соединения неизвестен; домен и протокол могут изменить маршрут." : " Результат DNS-разрешения ядром неизвестен; запрос может завершиться ошибкой или изменить совпадение правил.";
                    if (!runtimeReason.Contains(note)) runtimeReason += note;
                }
                continue;
            }
            if (action == "hijack-dns")
            {
                bool? hijack = Match(rule, host, process, network, port, inbound, ref dnsAssumption);
                if (hijack == false) continue;
                if (hijack == true) return new RoutingDecision { Route = "dns", RuleIndex = index, IsCertain = !dnsAssumption && runtimeReason == "",
                    Reason = routeOptionsReason + "Совпало правило №" + index + ": обработка модулем DNS ядра; это не прямой выход в интернет." + runtimeReason + " Это расчёт конфигурации, а не наблюдение фактического соединения." };
                string note = " Протокол или другие данные для правила перехвата DNS неизвестны; ядро может передать запрос своему модулю DNS.";
                if (!runtimeReason.Contains(note)) runtimeReason += note;
                continue;
            }
            // On Windows without auto_redirect, bypass is a route only when an outbound is supplied.
            if (action == "bypass" && Text(rule, "outbound") == "") continue;
            if (action != "route" && action != "reject" && action != "route-options" && action != "bypass") continue;
            if (action=="reject" && rule.ContainsKey("ip_version") && rule.Keys.All(k=>k=="action"||k=="ip_version") &&
                (Convert.ToString(rule["ip_version"])=="4"||Convert.ToString(rule["ip_version"])=="6") && !IPAddress.TryParse(host,out var versionAddress)) {
                runtimeReason += " Для IPv"+Convert.ToString(rule["ip_version"])+" действует блокировка; выбранный выход относится к остальным адресам.";
                continue;
            }
            bool? match = Match(rule, host, process, network, port, inbound,ref dnsAssumption);
            if (match == false) continue;
            if (match == null) return Unknown("Правило №" + index + " требует недостающих данных: приложения, TCP/UDP, порта или адреса после DNS. Уточните запрос.");
            if (action == "route-options")
            {
                if (!ApplyRouteOptions(rule, ref host, ref port, out bool changed))
                    return Unknown("Правило №" + index + " содержит неподдерживаемое изменение назначения.");
                if (changed) routeOptionsReason = "Учтены изменения назначения в предыдущих правилах. ";
                continue;
            }
            string tag = action == "reject" ? "block" : Text(rule, "outbound");
            return Decision(tag, routeOptionsReason + "Совпало правило №" + index + ": " + Describe(rule, tag), index, dnsAssumption,null,action=="reject",runtimeReason);
        }
        string final=Text(route,"final");
        if(final!="")return Decision(final,routeOptionsReason+"Явное правило не совпало; использован выход по умолчанию.",0,dnsAssumption,runtimeReason:runtimeReason);
        var exits=Values(config,"outbounds").ToArray();
        if(exits.Any(exit=>!(exit is Dictionary<string,object>)))return Unknown("Некорректный список выходов в полном конфиге.");
        var first=exits.Cast<Dictionary<string,object>>().FirstOrDefault();bool fallback=first==null;
        if(fallback)first=new Dictionary<string,object>{{"type","direct"}};
        return Decision(Text(first,"tag"),routeOptionsReason+(fallback?"Выходы не заданы; ядро использует встроенное прямое соединение.":"Выход по умолчанию не указан; использован первый выход конфигурации."),0,dnsAssumption,first,runtimeReason:runtimeReason);
    }

    private static bool ApplyRouteOptions(Dictionary<string,object> rule, ref string host, ref int? port, out bool changed)
    {
        changed = false;
        string address = Text(rule, "override_address");
        if (address != "")
        {
            if (IPAddress.TryParse(address, out var ip)) host = ip.ToString();
            else if (Uri.CheckHostName(address) == UriHostNameType.Dns) host = address;
            else return false;
            changed = true;
        }
        if (rule.TryGetValue("override_port", out var value))
        {
            if (!int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out int number) || number < 0 || number > 65535) return false;
            if (number > 0) { port = number; changed = true; }
        }
        return true;
    }

    private RoutingDecision Decision(string tag, string reason, int index, bool dnsAssumption,Dictionary<string,object> selected=null,bool rejected=false,string runtimeReason=null)
    {
        string path = rejected ? "block" : "unknown";
        var outbound = rejected?null:selected??FindOutbound(tag);
        if (outbound != null)
        {
            string type = Text(outbound, "type");
            path = type == "direct" ? "direct" : type == "block" ? "block" :
                new[] { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "socks", "http", "wireguard", "ssh", "tailscale" }.Contains(type) ? "proxy" : "unknown";
        }
        exits.TryGetValue(tag??"",out var identity);
        return new RoutingDecision { Route = path, OutboundTag=tag, ProfileId=path=="proxy"?identity?.ProfileId:null, ProfileName=path=="proxy"?identity?.ProfileName:null, RuleIndex = index, IsCertain = !dnsAssumption && string.IsNullOrEmpty(runtimeReason) && path != "unknown",
            Reason = reason + (dnsAssumption ? " Расчёт по имени: IP после DNS не указан; частный адрес может изменить результат." : "") +
                runtimeReason + " Это расчёт конфигурации, а не наблюдение фактического соединения." };
    }

    private Dictionary<string,object> FindOutbound(string tag)
    {
        if(String.IsNullOrEmpty(tag))return null;
        Dictionary<string,object> found=null;int index=0;
        foreach(var value in Values(config,"outbounds")){
            if(!(value is Dictionary<string,object> outbound))return null;
            string effective=Text(outbound,"tag");if(effective=="")effective=index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if(effective==tag){if(found!=null)return null;found=outbound;}index++;
        }
        foreach (var value in Values(config,"endpoints")) {
            if (!(value is Dictionary<string,object> endpoint)) return null;
            if (Text(endpoint,"tag") != tag) continue;
            if (found != null) return null;
            found = endpoint;
        }
        return found;
    }
    private static RoutingDecision Unknown(string reason) => new RoutingDecision { Route = "unknown", Reason = reason, IsCertain = false };
    private static string Describe(Dictionary<string, object> rule, string tag)
    {
        var parts = new List<string>();
        string predicate=DescribePredicate(rule,0);if(predicate!="")parts.Add(predicate);
        if (tag == StrictDirectRouting.ExitTag) parts.Add("строгий прямой маршрут сайта и DNS");
        else if (tag == "nova-game") parts.Add("выход текущей игровой сессии");
        else if (Text(rule,"action") == "reject") parts.Add("блокировка");
        else parts.Add("выход " + tag);
        return string.Join(" · ", parts);
    }

    internal static string DescribePredicate(Dictionary<string,object> rule,int depth)
    {
        if(depth>=4)return "…";string text;
        if(Text(rule,"type")=="logical"){
            var children=Values(rule,"rules").OfType<Dictionary<string,object>>().Take(5).ToArray();
            text="("+string.Join(Text(rule,"mode")=="or"?" ИЛИ ":" И ",children.Take(4).Select(child=>DescribePredicate(child,depth+1)))+(children.Length>4?" …":"")+")";
        }else{
            var parts=new List<string>();foreach(string key in new[]{"domain","domain_suffix","domain_keyword","ip_cidr","process_name","process_path","network","port","port_range"}){
                var values=Values(rule,key).Take(4).Select(v=>{string value=Convert.ToString(v)??"";return value.Length>80?value.Substring(0,80)+"…":value;}).ToArray();
                if(values.Length>0){string label=key=="domain"?"домен":key=="domain_suffix"?"домен и поддомены":key=="domain_keyword"?"домен содержит":key=="process_name"?"приложение":key=="process_path"?"путь приложения":key=="network"?"протокол":key=="port"?"порт":key=="port_range"?"порты":"IP/подсеть";parts.Add(label+" = "+string.Join(", ",values));}
            }
            var exactPaths = Values(rule, "process_path_regex").Select(value =>
                ApplicationRoutingService.DecodeExactPathRegex(Convert.ToString(value))).Where(path => path != null).Take(4);
            var displayedPaths = exactPaths.Select(path => path.Length > 80 ? path.Substring(0, 80) + "…" : path).ToArray();
            if (displayedPaths.Length > 0) parts.Add("путь приложения = " + string.Join(", ", displayedPaths));
            text=string.Join(" · ",parts);
        }
        if(text==""&&depth>0)text="любое назначение";if(text.Length>800)text=text.Substring(0,800)+"…";
        return rule.TryGetValue("invert",out var inverted)&&Equals(inverted,true)?"НЕ ("+text+")":text;
    }

    private static bool? Match(Dictionary<string, object> rule, string host, string process, string network, int? port,string inbound,ref bool dnsAssumption)
    {
        // Inversion applies to the whole result, including a definite early mismatch.
        bool? result = MatchUninverted(rule, host, process, network, port,inbound,ref dnsAssumption);
        return rule.TryGetValue("invert", out var inverted) && Equals(inverted, true) && result.HasValue ? !result.Value : result;
    }

    private static bool? MatchUninverted(Dictionary<string, object> rule, string host, string process, string network, int? port,string inbound,ref bool dnsAssumption)
    {
        bool unknown = false;
        if (Text(rule, "type") == "logical")
        {
            if (rule.Keys.Any(k => !new[] { "type", "mode", "rules", "invert", "action", "outbound", "method", "no_drop", "override_address", "override_port" }.Contains(k))) return null;
            if (Text(rule, "mode") != "or" && Text(rule, "mode") != "and") return null;
            var children = new List<bool?>();
            foreach (var child in Values(rule, "rules").OfType<Dictionary<string, object>>()) children.Add(Match(child, host, process, network, port,inbound,ref dnsAssumption));
            if (children.Count == 0) return null;
            bool? result = Text(rule, "mode") == "or" ? children.Any(c => c == true) ? true : children.Any(c => c == null) ? (bool?)null : false :
                children.Any(c => c == false) ? false : children.Any(c => c == null) ? (bool?)null : true;
            return result;
        }
        if (Text(rule, "type") != "" && Text(rule, "type") != "default") return null;
        if(Values(rule,"inbound").Any()){
            if(inbound=="")unknown=true;
            else if(!Values(rule,"inbound").Any(value=>inbound==Convert.ToString(value)))return false;
        }
        // Unknown destination matchers/rule sets may join an OR group with known fields.
        // A known nonmatch must not hide a possible match from those unsupported fields.
        if (rule.Keys.Any(key => !new[] { "action", "outbound", "method", "no_drop", "domain", "domain_suffix", "domain_keyword", "domain_regex", "ip_cidr", "ip_is_private", "network", "port", "port_range", "process_path", "process_name", "process_path_regex", "ip_version", "invert", "type", "inbound", "override_address", "override_port" }.Contains(key))) return null;
        if (rule.ContainsKey("ip_version")) {
            string version=Convert.ToString(rule["ip_version"]);
            if(version!="4"&&version!="6")return null;
            if(IPAddress.TryParse(host,out var address)) {
                int actual=address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork?4:6;
                if(actual.ToString()!=version)return false;
            }else unknown=true;
        }
        bool hasDestination = false, destinationMatch = false, destinationUnknown = false;
        foreach (string key in new[] { "domain", "domain_suffix", "domain_keyword", "domain_regex", "ip_cidr", "ip_is_private" })
        {
            if (!rule.ContainsKey(key) || key == "ip_is_private" && !Equals(rule[key], true) || key != "ip_is_private" && !Values(rule, key).Any()) continue;
            hasDestination = true;
            if (key == "domain") destinationMatch |= Values(rule, key).Any(v => host.Equals(Convert.ToString(v), StringComparison.OrdinalIgnoreCase));
            if (key == "domain_regex")
            {
                if (Values(rule, key).Any(value => Convert.ToString(value) != ".+")) return null;
                destinationMatch |= !IPAddress.TryParse(host, out _);
            }
            if (key == "domain_keyword") destinationMatch |= Values(rule, key).Any(v => host.IndexOf(Convert.ToString(v), StringComparison.OrdinalIgnoreCase) >= 0);
            if (key == "domain_suffix") destinationMatch |= Values(rule, key).Any(v => {
                string suffix = Convert.ToString(v); return suffix.StartsWith(".") ? host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) :
                    host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase); });
            if (key == "ip_cidr")
            {
                if (IPAddress.TryParse(host, out var address)) destinationMatch |= Values(rule, key).Any(v => InSubnet(address, Convert.ToString(v)));
                else destinationUnknown = true;
            }
            if (key == "ip_is_private")
            {
                if (IPAddress.TryParse(host, out var address)) destinationMatch |= IsPrivate(address);
                else dnsAssumption = true; // DNS is deliberately not queried by this local explanation.
            }
        }
        if (hasDestination && !destinationMatch && !destinationUnknown) return false;
        unknown |= hasDestination && !destinationMatch && destinationUnknown;
        if (Values(rule, "network").Any())
        {
            if (network == "") unknown = true;
            else if (!Values(rule, "network").Any(v => network.Equals(Convert.ToString(v), StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (Values(rule, "port").Any() || Values(rule, "port_range").Any())
        {
            if (!port.HasValue) unknown = true;
            else if (!Values(rule, "port").Any(v => Convert.ToInt32(v) == port.Value) && !Values(rule, "port_range").Any(v => InRange(port.Value, Convert.ToString(v)))) return false;
        }
        foreach (string key in new[] { "process_path", "process_name" })
        {
            if (!Values(rule, key).Any()) continue;
            if (string.IsNullOrEmpty(process)) unknown = true;
            else if (!Values(rule, key).Any(v => key == "process_path" ? ApplicationRoutingService.SamePath(Convert.ToString(v), process) :
                Path.GetFileName(process).Equals(Convert.ToString(v), StringComparison.Ordinal))) return false;
        }
        var pathPatterns = Values(rule, "process_path_regex").Select(Convert.ToString).ToArray();
        if (pathPatterns.Length > 0)
        {
            if (string.IsNullOrEmpty(process)) unknown = true;
            else if (!pathPatterns.Contains(".+"))
            {
                // Only interpret the generated exact paths. Other Go expressions stay unknown.
                var paths = pathPatterns.Select(ApplicationRoutingService.DecodeExactPathRegex).ToArray();
                if (paths.Any(path => path == null)) return null;
                if (!paths.Any(path => ApplicationRoutingService.SamePath(path, process))) return false;
            }
        }
        bool? matched = unknown ? (bool?)null : true;
        return matched;
    }

    private static bool InRange(int port, string range)
    {
        var parts = range.Split(':');
        if (parts.Length != 2) return false;
        int lower = 0, upper = 65535;
        return (parts[0] == "" || int.TryParse(parts[0], out lower)) && (parts[1] == "" || int.TryParse(parts[1], out upper)) && lower <= port && port <= upper;
    }
    private static bool InSubnet(IPAddress address, string range)
    {
        var parts = range.Split('/');
        if (!IPAddress.TryParse(parts[0], out var subnet)) return false;
        byte[] bytes = address.GetAddressBytes(), network = subnet.GetAddressBytes();
        if (bytes.Length != network.Length) return false;
        int bits = bytes.Length * 8;
        if (parts.Length > 1 && !int.TryParse(parts[1], out bits) || bits < 0 || bits > bytes.Length * 8) return false;
        for (int i = 0; i < bytes.Length && bits > 0; i++, bits -= 8)
        {
            int mask = 255 << Math.Max(0, 8 - bits) & 255;
            if ((bytes[i] & mask) != (network[i] & mask)) return false;
        }
        return true;
    }
    // sing-box ip_is_private is !sing/common/network.IsPublicAddr, not only RFC1918.
    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return new[] { "0.0.0.0/32", "10.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4",
            "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8" }.Any(range => InSubnet(address, range));
    }
    private static string Text(Dictionary<string, object> value, string key) => value.TryGetValue(key, out var found) ? Convert.ToString(found) : "";
    private static IEnumerable<object> Values(Dictionary<string, object> value, string key)
    {
        if (!value.TryGetValue(key, out var found) || found == null) return Enumerable.Empty<object>();
        return found is IEnumerable items && !(found is string) ? items.Cast<object>() : new[] { found };
    }
}

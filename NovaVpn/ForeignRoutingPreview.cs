using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Analysis only: no StateStore, core, DNS lookup, file access or settings mutation.
// Ordered candidates are deliberately separate from the legacy sorted domain lists.
public sealed class ForeignRoutePreview
{
    public int Position { get; }
    public string MatchKind { get; }
    public string Policy { get; }
    public string Issue { get; }
    public string CriteriaJson { get; }
    public string PolicyName { get; }
    public string ConditionSummary => CriteriaJson==null?"Условие недоступно для переноса":
        CriteriaJson=="{}"?"Любое назначение":"Условие (кратко): "+CompiledRoutingPlan.DescribePredicate(ForeignRoutingPreview.Json().Deserialize<Dictionary<string,object>>(CriteriaJson),0);
    public bool ConditionSupported => CriteriaJson != null;
    public bool PolicyResolved => Policy == "direct" || Policy == "block";
    public string Summary => "№" + Position + " · " + MatchKind + " · " +
        (Policy == "direct" ? "напрямую" : Policy == "block" ? "блокировка" : "нужно сопоставить выход") +
        (!PolicyResolved && ConditionSupported && PolicyName != "" ? " · «" + PolicyName + "»" : "") +
        (Issue == "" ? "" : " · " + Issue);
    internal ForeignRoutePreview(int position, string kind, string policy, string issue, Dictionary<string, object> criteria, string policyName = "")
    {
        Position = position; MatchKind = kind; Policy = policy; Issue = issue; PolicyName = policyName;
        CriteriaJson = criteria == null ? null : ForeignRoutingPreview.Json().Serialize(criteria);
    }
}

public sealed class ForeignRoutingPreview
{
    public string Format { get; }
    public IReadOnlyList<ForeignRoutePreview> Rules { get; }
    public bool RequiresDnsMapping { get; }
    public string ContainerIssue { get; }
    public string Summary => Format + ": правил " + Rules.Count + ", условий распознано " + Rules.Count(r => r.ConditionSupported) +
        ", выходов без сопоставления " + Rules.Count(r => !r.PolicyResolved) + ". Правила ещё не применяются." +
        (RequiresDnsMapping ? " Требуется отдельное сопоставление DNS и его влияния на IP-правила." : "") +
        (ContainerIssue == "" ? "" : " " + ContainerIssue);
    private ForeignRoutingPreview(string format, List<ForeignRoutePreview> rules, bool dns, bool containerSupported)
    { Format = format; Rules = new ReadOnlyCollection<ForeignRoutePreview>(rules); RequiresDnsMapping = dns; ContainerIssue = containerSupported ? "" : "Раздел routing содержит неподдерживаемую политику; частичное применение запрещено."; }
    internal static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 64 };
    internal static ForeignRoutingPreview Read(Dictionary<string, object> root, bool mihomo)
    {
        object raw = null; var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        bool dns = root.ContainsKey("dns");
        bool containerSupported = true;
        if (mihomo)
        {
            root.TryGetValue("rules", out raw);
            containerSupported = (!root.ContainsKey("mode") || Text(root, "mode") == "rule") &&
                Text(root, "find-process-mode") != "off" && !root.ContainsKey("routing-mark") && !root.ContainsKey("interface-name");
            dns |= root.ContainsKey("hosts") || root.ContainsKey("sniffer");
        }
        else
        {
            foreach (var node in Items(Get(root, "outbounds")).OfType<Dictionary<string, object>>())
            {
                string tag = Text(node, "tag"), protocol = Text(node, "protocol");
                if (tag == "") continue;
                // Duplicate tags are ambiguous, even when one node is a service outbound.
                string policy = protocol == "freedom" && Only(node, "tag,protocol,settings") && Empty(Get(node, "settings")) ? "direct" :
                    protocol == "blackhole" && Only(node, "tag,protocol,settings") && Empty(Get(node, "settings")) ? "block" : "";
                if (policies.ContainsKey(tag)) policies[tag] = ""; else policies.Add(tag, policy);
            }
            if (Get(root, "routing") is Dictionary<string, object> route)
            {
                route.TryGetValue("rules", out raw);
                containerSupported = Only(route, "rules,domainStrategy") &&
                    (!route.ContainsKey("domainStrategy") || Text(route, "domainStrategy") == "AsIs");
                dns |= Text(route, "domainStrategy") != "" && Text(route, "domainStrategy") != "AsIs";
            }
            else if (root.ContainsKey("routing")) containerSupported = false;
        }
        var rows = new List<ForeignRoutePreview>();
        if (raw != null && (!(raw is IEnumerable) || raw is string || raw is IDictionary))
            rows.Add(new ForeignRoutePreview(1, "Некорректный список", "", "ожидался список правил", null));
        else foreach (object item in Items(raw))
        {
            if (rows.Count >= 2000) throw new FormatException("В экспорте больше 2000 правил маршрутизации.");
            int position = rows.Count + 1;
            string kind = "Неизвестное условие", policy = "", policyName = "", issue = ""; Dictionary<string, object> criteria = null;
            try
            {
                if (mihomo) criteria = Mihomo(item, out kind, out policy, out policyName);
                else criteria = Xray(item, policies, out kind, out policy, out policyName);
                if (!containerSupported) { criteria = null; issue = "политика раздела routing требует отдельного переноса"; }
                else if (policy == "") issue = "именованный выход или группа требуют явного сопоставления";
            }
            catch (NotSupportedException) { issue = "условие или его параметры нельзя переносить без изменения смысла"; }
            catch (FormatException) { issue = "некорректные параметры правила"; }
            rows.Add(new ForeignRoutePreview(position, kind, policy, issue, criteria, policyName));
        }
        return new ForeignRoutingPreview(mihomo ? "Clash/Mihomo" : "Xray", rows, dns, containerSupported);
    }
    static Dictionary<string, object> Mihomo(object raw, out string kind, out string policy, out string policyName)
    {
        kind = "Неизвестное условие"; policy = ""; policyName = "";
        if (!(raw is string line) || line.Length > 8192 || line.Any(char.IsControl)) throw new FormatException();
        string first=line.Split(',')[0].Trim();
        if(first=="AND"||first=="OR"||first=="NOT"){
            kind=first;var top=LogicalParts(line);if(top.Length!=3)throw new NotSupportedException();
            policyName=Token(top[2],256);policy=policyName=="DIRECT"?"direct":policyName=="REJECT"?"block":"";
            int nodes=0;return MihomoCondition(top[0]+","+top[1],0,ref nodes);
        }
        string[] parts = line.Split(',').Select(x => x.Trim()).ToArray();
        string type = parts[0];
        var known = new[] { "DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "IP-CIDR", "IP-CIDR6", "PROCESS-NAME", "PROCESS-PATH", "DST-PORT", "NETWORK", "MATCH" };
        if (!known.Contains(type)) throw new NotSupportedException(); kind = type;
        int expected = type == "MATCH" ? 2 : 3;
        if (parts.Length != expected) throw new NotSupportedException(); // no-resolve/src are semantic, never silently dropped.
        policyName = Token(parts[expected - 1], 256);
        policy = parts[expected - 1] == "DIRECT" ? "direct" : parts[expected - 1] == "REJECT" ? "block" : "";
        if (parts[expected - 1] == "") throw new FormatException();
        string value = expected == 3 ? parts[1] : "";
        switch (type)
        {
            case "DOMAIN": return Obj("domain", new[] { Domain(value) });
            case "DOMAIN-SUFFIX": return Obj("domain_suffix", new[] { Domain(value) });
            case "DOMAIN-KEYWORD": return Obj("domain_keyword", new[] { Token(value, 253) });
            // An IP rule without no-resolve may trigger DNS in Mihomo. That effect needs a mapped DNS policy.
            case "IP-CIDR": case "IP-CIDR6":
                Cidr(value, type == "IP-CIDR6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                throw new NotSupportedException();
            case "PROCESS-NAME":
                if (value.IndexOfAny(new[] { '/', '\\', '*', '?', ':' }) >= 0) throw new FormatException();
                return Obj("process_name", new[] { Token(value, 260) });
            case "PROCESS-PATH":
                if (value.Length < 4 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' || value.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0) throw new NotSupportedException();
                return Obj("process_path", new[] { Token(value, 4096) });
            case "DST-PORT": return Ports(value, '-');
            case "NETWORK": value=value.ToLowerInvariant();if (value != "tcp" && value != "udp") throw new FormatException(); return Obj("network", new[] { value });
            case "MATCH": return Obj();
            default: throw new NotSupportedException();
        }
    }
    // Parentheses delimit predicates, not actions. A single unknown child rejects
    // its entire source row; never extract a wider supported subexpression.
    static Dictionary<string,object> MihomoCondition(string text,int depth,ref int nodes)
    {
        if(depth>16||++nodes>256)throw new NotSupportedException();
        var parts=LogicalParts(text);string type=parts[0];
        if(type=="AND"||type=="OR"||type=="NOT"){
            if(parts.Length!=2)throw new NotSupportedException();
            var children=LogicalParts(UnwrapLogical(parts[1]));
            if(children.Length==0||type=="NOT"&&children.Length!=1)throw new NotSupportedException();
            var rules=new List<object>();foreach(string child in children)rules.Add(MihomoCondition(UnwrapLogical(child),depth+1,ref nodes));
            var result=Obj("type","logical","mode",type=="OR"?"or":"and","rules",rules.ToArray());
            if(type=="NOT")result.Add("invert",true);return result;
        }
        if(type=="MATCH"||type=="SUB-RULE")throw new NotSupportedException();
        return Mihomo(text+",DIRECT",out _,out _,out _);
    }
    static string[] LogicalParts(string text)
    {
        var result=new List<string>();int depth=0,start=0;
        for(int i=0;i<text.Length;i++){
            if(text[i]=='('){if(++depth>64)throw new NotSupportedException();}
            else if(text[i]==')'){if(--depth<0)throw new FormatException();}
            else if(text[i]==','&&depth==0){result.Add(Token(text.Substring(start,i-start).Trim(),8192));start=i+1;}
        }
        if(depth!=0)throw new FormatException();result.Add(Token(text.Substring(start).Trim(),8192));return result.ToArray();
    }
    static string UnwrapLogical(string text)
    {
        text=text.Trim();if(text.Length<3||text[0]!='('||text[text.Length-1]!=')')throw new FormatException();
        int depth=0;for(int i=0;i<text.Length;i++){if(text[i]=='(')depth++;else if(text[i]==')')depth--;if(depth<1&&i<text.Length-1)throw new FormatException();}
        if(depth!=0)throw new FormatException();return text.Substring(1,text.Length-2).Trim();
    }
    static Dictionary<string, object> Xray(object raw, Dictionary<string, string> policies, out string kind, out string policy, out string policyName)
    {
        kind = "Xray field"; policy = ""; policyName = "";
        if (!(raw is Dictionary<string, object> map)) throw new FormatException();
        policyName = Token(Text(map, "outboundTag"), 256);
        if (policies.TryGetValue(Text(map, "outboundTag"), out var known)) policy = known;
        if (!Only(map, "type,domain,ip,port,network,outboundTag,ruleTag") || map.ContainsKey("type") && Text(map, "type") != "field" || Text(map, "outboundTag") == "") throw new NotSupportedException();
        var conditions = new List<object>();
        if (map.ContainsKey("domain"))
        {
            var alternatives = new List<object>();
            foreach (string value in Strings(Get(map, "domain")))
            {
                if (value.StartsWith("full:", StringComparison.Ordinal)) alternatives.Add(Obj("domain", new[] { Domain(value.Substring(5)) }));
                else if (value.StartsWith("domain:", StringComparison.Ordinal)) alternatives.Add(Obj("domain_suffix", new[] { Domain(value.Substring(7)) }));
                else if (value.StartsWith("keyword:", StringComparison.Ordinal)) alternatives.Add(Obj("domain_keyword", new[] { Token(value.Substring(8), 253) }));
                else if (value.Contains(":")) throw new NotSupportedException();
                else alternatives.Add(Obj("domain_keyword", new[] { Token(value, 253) }));
            }
            conditions.Add(alternatives.Count == 1 ? alternatives[0] : Obj("type", "logical", "mode", "or", "rules", alternatives.ToArray()));
        }
        if (map.ContainsKey("ip"))
        {
            var ips = Strings(Get(map, "ip")); foreach (string ip in ips) Cidr(ip, null);
            conditions.Add(Obj("ip_cidr", ips));
        }
        if (map.ContainsKey("port")) conditions.Add(Ports(Text(map, "port"), '-'));
        if (map.ContainsKey("network"))
        {
            var networks = Text(map, "network").Split(',').Select(x => x.Trim()).ToArray();
            if (networks.Length < 1 || networks.Length > 2 || networks.Any(x => x != "tcp" && x != "udp")) throw new FormatException();
            conditions.Add(Obj("network", networks));
        }
        if (conditions.Count == 0) throw new NotSupportedException(); // Never broaden a malformed empty field to MATCH.
        return conditions.Count == 1 ? (Dictionary<string, object>)conditions[0] : Obj("type", "logical", "mode", "and", "rules", conditions.ToArray());
    }
    static Dictionary<string, object> Ports(string text, char separator)
    {
        var exact = new List<int>(); var ranges = new List<string>();
        var parts = text.Split(','); if (parts.Length > 256) throw new FormatException();
        foreach (string value in parts)
        {
            string[] ends = value.Trim().Split(separator);
            if (ends.Length < 1 || ends.Length > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from) || from < 1 || from > 65535) throw new FormatException();
            if (ends.Length == 1) exact.Add(from);
            else { if (!int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out int to) || to < from || to > 65535) throw new FormatException(); ranges.Add(from + ":" + to); }
        }
        var result = Obj(); if (exact.Count > 0) result.Add("port", exact.ToArray()); if (ranges.Count > 0) result.Add("port_range", ranges.ToArray()); return result;
    }
    static string Domain(string value)
    {
        Token(value, 253);
        if (value.StartsWith(".") || value.Contains("*") || Uri.CheckHostName(value) != UriHostNameType.Dns) throw new FormatException();
        return value;
    }
    static string Token(string value, int maximum) { if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw new FormatException(); return value; }
    static void Cidr(string value, AddressFamily? family)
    {
        string[] parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) || parts[0].Contains('%') || family.HasValue && ip.AddressFamily != family.Value) throw new NotSupportedException();
        int max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) || prefix < 0 || prefix > max)) throw new FormatException();
    }
    static string[] Strings(object value)
    {
        if (!(value is object[] list) || list.Length == 0 || list.Length > 2000 || list.Any(x => !(x is string))) throw new FormatException();
        return list.Cast<string>().Select(x => Token(x, 4096)).ToArray();
    }
    static bool Only(Dictionary<string, object> map, string keys) { var allowed = new HashSet<string>(keys.Split(','), StringComparer.Ordinal); return map.Keys.All(allowed.Contains); }
    static bool Empty(object value) => value == null || value is Dictionary<string, object> map && map.Count == 0;
    static object Get(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;
    static string Text(Dictionary<string, object> map, string key) => Get(map, key) is string text ? text : "";
    static IEnumerable<object> Items(object value) => value is IEnumerable list && !(value is string) && !(value is IDictionary) ? list.Cast<object>() : Enumerable.Empty<object>();
    static Dictionary<string, object> Obj(params object[] pairs) { var map = new Dictionary<string, object>(StringComparer.Ordinal); for (int i = 0; i < pairs.Length; i += 2) map.Add((string)pairs[i], pairs[i + 1]); return map; }
}

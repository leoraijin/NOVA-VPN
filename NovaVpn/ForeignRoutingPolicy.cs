using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Persist only reviewed routing predicates/actions, never a profile's credentials or native config.
[DataContract]
public sealed class ForeignRoutingPolicy
{
    [DataMember] public int Schema = 1;
    [DataMember] public string Format;
    [DataMember] public string Fallback;
    [DataMember] public List<string> Rules = new List<string>();
    internal static ForeignRoutingPolicy FromPlan(ForeignRoutingMigrationPlan plan)
    {
        var config = Json().Deserialize<Dictionary<string, object>>(plan.MigratedConfig);
        var route = (Dictionary<string, object>)config["route"];
        var rules = List(route["rules"]).Cast<Dictionary<string, object>>().ToArray();
        var policy = new ForeignRoutingPolicy { Format = plan.SourceFormat,
            Fallback = Convert.ToString(route["final"], CultureInfo.InvariantCulture),
            Rules = rules.Skip(rules.Length - plan.SourceRuleCount - 1).Select(r => Json().Serialize(r)).ToList() };
        // A block fallback is represented by an unconditional reject rule, not route.final.
        if (Text(rules.Last(), "action") == "reject") policy.Fallback = "block";
        policy.Validate(); return policy;
    }
    public ForeignRoutingPolicy Copy()
    { Validate(); return new ForeignRoutingPolicy { Schema = Schema, Format = Format, Fallback = Fallback, Rules = new List<string>(Rules) }; }
    public string Apply(string preparedConfig)
    {
        Validate();
        return ForeignRoutingMigrationPlan.ApplyCompiledRules(preparedConfig, Rules.Select(r => (object)Json().Deserialize<Dictionary<string, object>>(r)).ToList(), Fallback);
    }
    public bool HasProcessPaths()
    {
        Validate();
        return Rules.Any(r => HasPath(Json().Deserialize<Dictionary<string, object>>(r)));
    }
    private static bool HasPath(object value)
    {
        if (value is Dictionary<string, object> map) return map.ContainsKey("process_path") || map.Values.Any(HasPath);
        return value is IEnumerable values && !(value is string) && values.Cast<object>().Any(HasPath);
    }
    public void Validate()
    {
        if (Schema != 1 || Format != "Clash/Mihomo" && Format != "Xray" || !Route(Fallback) || Rules == null || Rules.Count < 2 || Rules.Count > 2001)
            throw new InvalidDataException("Неподдерживаемая сохранённая политика переноса.");
        if (Rules.Any(r => r == null || r.Length > 128 * 1024) || Rules.Sum(r => (long)r.Length) > 4 * 1024 * 1024)
            throw new InvalidDataException("Сохранённая маршрутизация слишком большая.");
        foreach (string json in Rules)
        {
            Dictionary<string, object> rule;
            try { rule = Json().Deserialize<Dictionary<string, object>>(json); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { throw new InvalidDataException("Некорректное сохранённое правило."); }
            if (rule == null) throw new InvalidDataException("Некорректное сохранённое правило.");
            string action = Text(rule, "action");
            if (action == "route")
            {
                if (Text(rule, "outbound") != "direct" && Text(rule, "outbound") != "proxy" || rule.ContainsKey("method") || rule.ContainsKey("no_drop")) Invalid();
            }
            else if (action == "reject")
            {
                if (rule.ContainsKey("outbound") || Text(rule, "method") != "default" && Text(rule, "method") != "drop" ||
                    Text(rule, "method") == "default" && (!rule.TryGetValue("no_drop", out var noDrop) || !Equals(noDrop, true)) ||
                    Text(rule, "method") == "drop" && rule.ContainsKey("no_drop")) Invalid();
            }
            else Invalid();
            var predicate = rule.Where(p => !new[] { "action", "outbound", "method", "no_drop" }.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            Predicate(predicate, 0, true);
        }
        var last = Json().Deserialize<Dictionary<string, object>>(Rules.Last());
        if (last.Keys.Any(k => !new[] { "action", "outbound", "method", "no_drop" }.Contains(k)) ||
            (Fallback == "block" ? Text(last, "action") != "reject" : Text(last, "action") != "route" || Text(last, "outbound") != Fallback)) Invalid();
    }
    private static void Predicate(Dictionary<string, object> map, int depth, bool allowEmpty)
    {
        if (depth > 16 || map.Count == 0 && !allowEmpty) Invalid();
        if (Text(map, "type") == "logical")
        {
            if (map.Keys.Any(k => !new[] { "type", "mode", "rules", "invert" }.Contains(k)) || Text(map, "mode") != "and" && Text(map, "mode") != "or") Invalid();
            if(map.ContainsKey("invert")&&!(map["invert"] is bool))Invalid();
            var children = List(Get(map, "rules")); if (children.Length < 1 || children.Length > 2000) Invalid();
            foreach (var value in children) { if (!(value is Dictionary<string, object> child)) Invalid(); else Predicate(child, depth + 1, false); }
            return;
        }
        foreach (var pair in map)
        {
            var values = List(pair.Value); if (values.Length < 1 || values.Length > 2000) Invalid();
            foreach (object value in values)
            {
                if (pair.Key == "port") { if (!(value is int port) || port < 1 || port > 65535) Invalid(); continue; }
                if (!(value is string text) || string.IsNullOrWhiteSpace(text) || text != text.Trim() || text.Length > 4096 || text.Any(char.IsControl)) { Invalid(); continue; }
                switch (pair.Key)
                {
                    case "domain": case "domain_suffix": if (text.Length > 253 || text.Contains("*") || text.StartsWith(".") || Uri.CheckHostName(text) != UriHostNameType.Dns) Invalid(); break;
                    case "domain_keyword": if (text.Length > 253) Invalid(); break;
                    case "network": if (text != "tcp" && text != "udp") Invalid(); break;
                    case "process_name": if (text.Length > 260 || text.IndexOfAny(new[] { '/', '\\', '*', '?', ':' }) >= 0) Invalid(); break;
                    case "process_path": if (text.Length < 4 || !char.IsLetter(text[0]) || text[1] != ':' || text[2] != '\\' || text.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0) Invalid(); break;
                    case "port_range":
                        string[] ends = text.Split(':');
                        if (ends.Length != 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from) || !int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out int to) || from < 1 || to < from || to > 65535) Invalid();
                        break;
                    case "ip_cidr":
                        string[] parts = text.Split('/');
                        if (parts.Length > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var ip)) { Invalid(); break; }
                        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) || prefix < 0 || prefix > ip.GetAddressBytes().Length * 8)) Invalid();
                        break;
                    default: Invalid(); break;
                }
            }
        }
    }
    private static object[] List(object value)
    {
        if (!(value is IEnumerable) || value is string || value is IDictionary) { Invalid(); return null; }
        return ((IEnumerable)value).Cast<object>().ToArray();
    }
    private static bool Route(string value) => value == "proxy" || value == "direct" || value == "block";
    private static void Invalid() { throw new InvalidDataException("Сохранённое правило не соответствует проверенной схеме переноса."); }
    private static object Get(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;
    private static string Text(Dictionary<string, object> map, string key) => Get(map, key) as string ?? "";
    private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 64 };
}

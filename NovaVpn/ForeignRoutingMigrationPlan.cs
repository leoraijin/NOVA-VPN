using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Frozen all-or-nothing proposal. Preparation never starts a core or changes state.
// The caller must review mappings, verify the source config, and retain OriginalConfig for rollback.
public sealed class ForeignRoutingMigrationPlan
{
    public string OriginalConfig { get; }
    public string MigratedConfig { get; }
    public string SourceFormat { get; }
    public int SourceRuleCount { get; }
    private readonly byte[] originalHash;
    private ForeignRoutingMigrationPlan(string original, string migrated, string format, int count)
    { OriginalConfig = original; MigratedConfig = migrated; SourceFormat = format; SourceRuleCount = count; originalHash = Hash(original); }
    public bool IsCurrent(string preparedConfig)
    {
        if (preparedConfig == null || preparedConfig.Length > 16 * 1024 * 1024) return false;
        var actual = Hash(preparedConfig); int difference = 0;
        for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ originalHash[i];
        return difference == 0;
    }
    // Mappings are explicit per source position: proxy means this prepared config's VPN exit.
    // No named group or node is silently treated as the current proxy.
    public static ForeignRoutingMigrationPlan Prepare(string preparedConfig, ForeignRoutingPreview preview,
        IDictionary<int, string> destinationMappings, string unmatchedRoute)
    {
        if (preparedConfig == null || preview == null) throw new ArgumentNullException();
        if (preparedConfig.Length > 16 * 1024 * 1024) throw new ArgumentException("Конфигурация слишком большая.");
        if (preview.RequiresDnsMapping || preview.ContainerIssue != "" || preview.Rules.Count == 0 || preview.Rules.Any(r => !r.ConditionSupported))
            throw new NotSupportedException("Нельзя применить часть маршрутизации. Сначала сопоставьте все условия и политику DNS.");
        var mappings = destinationMappings == null ? new Dictionary<int, string>() : new Dictionary<int, string>(destinationMappings);
        if (mappings.Keys.Any(p => p < 1 || p > preview.Rules.Count)) throw new ArgumentException("Сопоставление относится к другому правилу.");
        ValidateRoute(unmatchedRoute);
        var rules = new List<object>();
        foreach (var row in preview.Rules)
        {
            string policy = row.Policy;
            if (row.PolicyResolved)
            {
                if (mappings.TryGetValue(row.Position, out var mapped) && mapped != policy)
                    throw new ArgumentException("Явный DIRECT или блокировка не заменяются другим выходом при переносе.");
            }
            else if (!mappings.TryGetValue(row.Position, out policy))
                throw new NotSupportedException("Для каждого именованного выхода нужно явное сопоставление.");
            ValidateRoute(policy);
            var rule = Json().Deserialize<Dictionary<string, object>>(row.CriteriaJson);
            Action(rule, policy, preview.Format == "Xray" && row.Policy == "block");
            rules.Add(rule);
        }
        // Explicitly chosen fallback, rather than the unrelated old personal lists/default.
        var final = new Dictionary<string, object>(); Action(final, unmatchedRoute, false); rules.Add(final);
        return new ForeignRoutingMigrationPlan(preparedConfig, ApplyCompiledRules(preparedConfig, rules, unmatchedRoute), preview.Format, preview.Rules.Count);
    }
    public ForeignRoutingPolicy ToPolicy() => ForeignRoutingPolicy.FromPlan(this);
    internal static string ApplyCompiledRules(string preparedConfig, List<object> rules, string unmatchedRoute)
    {
        Dictionary<string, object> config;
        try { config = Json().Deserialize<Dictionary<string, object>>(preparedConfig); }
        catch (ArgumentException) { throw new ArgumentException("Некорректная подготовленная конфигурация."); }
        if (config == null || !(Get(config, "route") is Dictionary<string, object> route)) throw new ArgumentException("Не найден раздел маршрутизации.");
        StrictObjects(Get(config, "outbounds"));
        RemoveReplacedDirectLayer(config);
        var outbounds = StrictObjects(Get(config, "outbounds"));
        if (outbounds.Length == 0 || outbounds.Any(o => Text(o, "tag") == "") || outbounds.GroupBy(o => Text(o, "tag"), StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new ArgumentException("Выходы подготовленной конфигурации неоднозначны.");
        if (outbounds.Count(o => Text(o, "tag") == "direct" && Text(o, "type") == "direct") != 1 ||
            outbounds.Count(o => Text(o, "tag") == "proxy" && new[] { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "socks", "http" }.Contains(Text(o, "type"))) != 1)
            throw new NotSupportedException("Перенос требует стандартные отдельные VPN- и direct-выходы NOVA.");
        // Do not discard an unrecognized existing rule format or a misplaced bootstrap action.
        object rawRules = Get(route, "rules");
        if (rawRules != null && (!(rawRules is IEnumerable) || rawRules is string || rawRules is IDictionary)) throw new ArgumentException("Некорректный список исходных правил.");
        var existing = Values(rawRules).ToArray();
        if (existing.Any(r => !(r is Dictionary<string, object>))) throw new ArgumentException("Некорректные исходные правила.");
        var prefix = existing.Cast<Dictionary<string, object>>().TakeWhile(r => Text(r, "action") == "sniff" || Text(r, "action") == "hijack-dns").ToArray();
        if (existing.Skip(prefix.Length).Cast<Dictionary<string, object>>().Any(r => Text(r, "action") == "sniff" || Text(r, "action") == "hijack-dns"))
            throw new NotSupportedException("Исходная конфигурация содержит особый порядок служебных правил.");
        route["rules"] = prefix.Cast<object>().Concat(rules).ToArray();
        // An unconditional final rule handles block as well. Keep route.final valid and deterministic.
        route["final"] = unmatchedRoute == "direct" ? "direct" : "proxy";
        return Json().Serialize(config);
    }
    private static void RemoveReplacedDirectLayer(Dictionary<string, object> config)
    {
        const string exit = "nova-strict-direct", dnsTag = "nova-strict-direct-dns";
        var exits = StrictObjects(Get(config, "outbounds"));
        var own = exits.Where(o => Text(o, "tag") == exit).ToArray();
        if (own.Length == 0) return;
        if (own.Length != 1 || own[0].Count != 3 || Text(own[0], "type") != "direct" || Text(own[0], "domain_resolver") != dnsTag ||
            exits.Any(o => Text(o, "tag") != exit && (RefersTo(o, exit) || RefersTo(o, dnsTag))) || !(Get(config, "dns") is Dictionary<string, object> dns))
            throw new NotSupportedException("Особый прямой выход нельзя заменить без сопоставления его зависимостей.");
        var servers = StrictObjects(Get(dns, "servers"));
        var ownDns = servers.Where(d => Text(d, "tag") == dnsTag).ToArray();
        if (ownDns.Length != 1 || ownDns[0].Count != 3 || Text(ownDns[0], "type") != "local" || Text(ownDns[0], "detour") != exit ||
            Text(dns, "final") == dnsTag || servers.Any(d => Text(d, "tag") != dnsTag && (RefersTo(d, exit) || RefersTo(d, dnsTag))))
            throw new NotSupportedException("Особая DNS-политика прямого выхода требует отдельного переноса.");
        var dnsRules = StrictObjects(Get(dns, "rules")).Cast<object>().ToArray();
        foreach (var value in dnsRules)
        {
            if (!(value is Dictionary<string, object> rule)) throw new ArgumentException("Некорректное исходное правило DNS.");
            if (Text(rule, "server") == dnsTag && (Text(rule, "action") != "route" || rule.Keys.Any(k => !new[] { "domain", "domain_suffix", "action", "server" }.Contains(k))))
                throw new NotSupportedException("Нельзя частично удалить составное правило DNS.");
        }
        dns["rules"] = dnsRules.Where(v => Text((Dictionary<string, object>)v, "server") != dnsTag).ToArray();
        dns["servers"] = servers.Where(d => Text(d, "tag") != dnsTag).ToArray();
        config["outbounds"] = exits.Where(o => Text(o, "tag") != exit).ToArray();
    }
    private static bool RefersTo(object value, string token)
    {
        if (value is string text) return text == token;
        if (value is Dictionary<string, object> map) return map.Values.Any(v => RefersTo(v, token));
        return Values(value).Any(v => RefersTo(v, token));
    }
    private static Dictionary<string, object>[] StrictObjects(object value)
    {
        if (!(value is IEnumerable) || value is string || value is IDictionary) throw new ArgumentException("Ожидался список объектов конфигурации.");
        var rows = Values(value).ToArray();
        if (rows.Any(v => !(v is Dictionary<string, object>))) throw new ArgumentException("Некорректный объект конфигурации.");
        return rows.Cast<Dictionary<string, object>>().ToArray();
    }
    private static void Action(Dictionary<string, object> rule, string route, bool blackhole)
    {
        rule["action"] = route == "block" ? "reject" : "route";
        if (route != "block") rule["outbound"] = route;
        else if (blackhole) rule["method"] = "drop";
        else { rule["method"] = "default"; rule["no_drop"] = true; }
    }
    private static void ValidateRoute(string route) { if (route != "direct" && route != "proxy" && route != "block") throw new ArgumentException("Выберите VPN, напрямую или блокировку."); }
    private static byte[] Hash(string value) { using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(value)); }
    private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
    private static object Get(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;
    private static string Text(Dictionary<string, object> map, string key) => Get(map, key) as string ?? "";
    private static IEnumerable<object> Values(object value) => value is IEnumerable list && !(value is string) && !(value is IDictionary) ? list.Cast<object>() : Enumerable.Empty<object>();
}

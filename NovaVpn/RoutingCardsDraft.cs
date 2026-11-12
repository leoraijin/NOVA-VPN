using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace NovaVpn;

// Detached UI lists: editing and duplicate warnings never mutate the running policy.
internal sealed class RoutingCardsDraft
{
    internal sealed class Group
    {
        internal string Key, Title, Target;
        internal bool Applications;
        internal readonly List<string> Values = new List<string>();
        internal readonly List<string> Initial = new List<string>();
        internal readonly List<MultiServerRuleRecord> Records = new List<MultiServerRuleRecord>();
    }
    internal readonly RoutingSettings Source;
    internal readonly string Baseline;
    internal readonly List<Group> Groups = new List<Group>();
    internal bool Dirty => Groups.Any(group => !group.Values.SequenceEqual(group.Initial));
    internal RoutingCardsDraft(RoutingSettings source)
    {
        Source = RoutingOverlayService.Clone(source);
        Baseline = RoutingMigrationService.Fingerprint(source);
        foreach (bool apps in new[] {false, true})
        foreach (string target in new[] {"Direct", "Default", "Profile", "Block"})
        {
            var group = new Group { Applications = apps, Target = target,
                Key = (apps ? "RoutingApps" : "RoutingSites") + (target == "Direct" ? "Direct" : target == "Default" ? "Primary" : target == "Profile" ? "Secondary" : "Blocked"),
                Title = (apps ? "Приложения " : "Сайты ") + (target == "Direct" ? "напрямую" : target == "Default" ? "через основной VPN" : target == "Profile" ? "через VPN-2" : "— блокировать") };
            group.Records.AddRange(Source.MultiServer.Rules.Where(rule =>
                (apps ? rule.ApplicationPath != null || rule.ApplicationName != null : rule.Domain != null) && MatchesTarget(rule, target)));
            group.Values.AddRange(group.Records.Select(Label));
            if (apps && target == "Direct") group.Values.AddRange(Source.DirectProcesses);
            if (apps && target == "Default") group.Values.AddRange(Source.ProxyProcesses);
            if (!apps && target == "Block") group.Values.AddRange(Source.BlockDomains);
            group.Initial.AddRange(group.Values);
            Groups.Add(group);
        }
    }
    private bool MatchesTarget(MultiServerRuleRecord rule, string target) => target == "Default"
        ? rule.Target == "Default" || rule.Target == "Profile" && rule.ProfileId == Source.MultiServer.DefaultProfileId
        : target == "Profile" ? Source.MultiServer.SecondaryProfileId != null && rule.Target == "Profile" && rule.ProfileId == Source.MultiServer.SecondaryProfileId
        : rule.Target == target;
    internal static string Label(MultiServerRuleRecord rule) => rule.ApplicationPath ?? rule.ApplicationName ?? rule.DomainLabel;
    internal static string Key(string value, bool apps)
    {
        var text = (value ?? "").Trim().Trim('"');
        if (apps)
        {
            try { return ApplicationRoutingService.NormalizePath(text).ToLowerInvariant(); }
            catch { return text.ToLowerInvariant(); }
        }
        const string suffix = " и поддомены";
        if (text.EndsWith(suffix, StringComparison.Ordinal)) text = text.Substring(0, text.Length - suffix.Length);
        try { return MultiServerRule.NormalizeDomainSelector(RoutingInspector.NormalizeDomainRule(text)); }
        catch { return text.ToLowerInvariant(); }
    }
    internal static bool Same(string first, string second, bool apps)
    {
        if (!apps) return Key(first, false) == Key(second, false);
        string a = Environment.ExpandEnvironmentVariables((first ?? "").Trim().Trim('"')).Replace('/', '\\');
        string b = Environment.ExpandEnvironmentVariables((second ?? "").Trim().Trim('"')).Replace('/', '\\');
        if (a.Length == 0 || b.Length == 0) return false;
        // A name rule also matches the same executable entered by full path.
        if (a.IndexOf('\\') < 0 || b.IndexOf('\\') < 0)
        {
            try { return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }
        return ApplicationRoutingService.SamePath(a, b);
    }
    internal string WarningFor(Group current) => WarningForValues(current, current.Values);
    internal string WarningForValues(Group current, IEnumerable<string> candidates)
    {
        foreach (string value in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var matches = Groups.Where(group => group != current && group.Applications == current.Applications &&
                group.Values.Any(other => Same(value, other, current.Applications))).Select(group => group.Title).ToList();
            // Also inspect assignments to third/fourth servers, which remain visible below.
            if (Source.MultiServer.Rules.Except(Groups.SelectMany(group => group.Records)).Any(rule =>
                (current.Applications ? rule.ApplicationPath != null || rule.ApplicationName != null : rule.Domain != null) && Same(value, Label(rule), current.Applications)))
                matches.Add("Другие назначения");
            if (!current.Applications && Source.ZapretDirectDomains.Any(other => Same(value, other, false))) matches.Add("Сайты Zapret");
            if (matches.Count != 0) return value + " уже есть в: «" + string.Join("», «", matches) + "». Добавление разрешено; записи сохранены в обоих окнах.";
        }
        return null;
    }
    internal RoutingSettings Build()
    {
        var next = RoutingOverlayService.Clone(Source);
        var settings = next.MultiServer;
        foreach (var group in Groups.Where(group => !group.Values.SequenceEqual(group.Initial)))
        {
            if (group.Target == "Profile" && settings.SecondaryProfileId == null && group.Values.Count != 0)
                throw new InvalidOperationException("Сначала выберите сервер VPN-2 во вкладке «Серверы».");
            var removed = Source.MultiServer.Rules.Select((rule, index) => new {rule, index})
                .Where(item => group.Records.Contains(item.rule)).Select(item => item.index).ToArray();
            // Match against the original records, not selectors in other cards.
            var originalCopies = group.Records.Select(rule => rule.Copy()).ToList();
            foreach (int index in removed) next.MultiServer.Rules[index] = null;
            List<string> legacy = group.Applications && group.Target == "Direct" ? next.DirectProcesses :
                group.Applications && group.Target == "Default" ? next.ProxyProcesses :
                !group.Applications && group.Target == "Block" ? next.BlockDomains : null;
            var legacyOriginal = legacy == null ? new List<string>() : new List<string>(legacy);
            legacy?.Clear();
            foreach (string raw in group.Values)
            {
                string value = raw.Trim();
                if (value.Length == 0) continue;
                var existing = originalCopies.FirstOrDefault(rule => Label(rule) == value);
                if (existing != null) { settings.Rules.Add(existing); originalCopies.Remove(existing); continue; }
                if (legacy != null && legacyOriginal.Remove(value)) { legacy.Add(value); continue; }
                var record = new MultiServerRuleRecord {Target = group.Target,
                    ProfileId = group.Target == "Profile" ? settings.SecondaryProfileId : null};
                if (group.Applications)
                {
                    string path = Environment.ExpandEnvironmentVariables(value.Trim('"')).Replace('/', '\\');
                    if (!Path.IsPathRooted(path))
                    {
                        if (legacy != null && path.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        { legacy.Add(path); continue; }
                        record.ApplicationName = path;
                    }
                    else
                    {
                        record.ApplicationPath = ApplicationRoutingService.NormalizePath(path);
                        if (record.ApplicationPath.Length == 0) throw new InvalidOperationException("Укажите имя приложения или полный путь к .exe.");
                    }
                }
                else
                {
                    const string suffix = " и поддомены";
                    record.IncludeSubdomains = value.EndsWith(suffix, StringComparison.Ordinal);
                    record.Domain = Key(value, false);
                }
                record.ToRule();
                settings.Rules.Add(record);
            }
        }
        settings.Rules.RemoveAll(rule => rule == null);
        settings.Revision++;
        settings.Validate();
        return next;
    }
}

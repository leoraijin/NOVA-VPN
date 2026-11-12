using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NovaVpn;

public enum ServerRouteKind { Default, Profile, Direct, Block }

// Immutable policy primitives. These contain identities, never credentials or raw configs.
public sealed class ServerRouteTarget
{
    public ServerRouteKind Kind { get; }
    public string ProfileId { get; }
    public ServerRouteTarget(ServerRouteKind kind, string profileId = null)
    {
        if (!Enum.IsDefined(typeof(ServerRouteKind), kind)) throw new ArgumentException("Unknown route target");
        if (kind == ServerRouteKind.Profile ? string.IsNullOrWhiteSpace(profileId) : profileId != null)
            throw new ArgumentException("Only profile targets require a profile ID");
        Kind = kind; ProfileId = profileId;
    }
}

public sealed class MultiServerRule
{
    public string ApplicationPath { get; }
    public string ApplicationName { get; }
    public string Domain { get; }
    public bool IncludeSubdomains { get; }
    internal bool SubdomainsOnly => Domain?.StartsWith(".", StringComparison.Ordinal) == true;
    public ServerRouteTarget Target { get; }
    public MultiServerRule(string applicationPath, string domain, bool includeSubdomains, ServerRouteTarget target, string applicationName = null)
    {
        if ((applicationPath != null ? 1 : 0) + (domain != null ? 1 : 0) + (applicationName != null ? 1 : 0) != 1)
            throw new ArgumentException("Choose application or domain");
        if (applicationName != null && (string.IsNullOrWhiteSpace(applicationName) || applicationName != applicationName.Trim() ||
            applicationName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !applicationName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Expected executable name");
        ApplicationName = applicationName;
        if (applicationPath != null && (( !Path.IsPathRooted(applicationPath) || Path.GetPathRoot(applicationPath).Length < 3) || applicationPath.IndexOfAny(new[] {'*', '?'}) >= 0))
            throw new ArgumentException("Application requires an absolute executable path");
        ApplicationPath = applicationPath == null ? null : Path.GetFullPath(applicationPath);
        Domain = domain == null ? null : NormalizeDomainSelector(domain);
        IncludeSubdomains = domain != null && !SubdomainsOnly && includeSubdomains;
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }
    internal static string NormalizeDomainSelector(string value)
    {
        string input = (value ?? "").Trim();
        if (input.StartsWith("*.", StringComparison.Ordinal)) input = input.Substring(1);
        return input.StartsWith(".", StringComparison.Ordinal) ? "." + NormalizeDomain(input.Substring(1)) : NormalizeDomain(input);
    }

    public static string NormalizeDomain(string value)
    {
        var input = (value ?? "").Trim().TrimEnd('.');
        var host = new IdnMapping().GetAscii(input).ToLowerInvariant();
        if (host.Length > 253 || host.Split('.').Any(label =>
            label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-' ||
            label.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-'))))
            throw new ArgumentException("Expected a domain name");
        System.Net.IPAddress ignored;
        if (System.Net.IPAddress.TryParse(host, out ignored)) throw new ArgumentException("Expected domain, not IP");
        return host;
    }
    internal bool Matches(string app, string domain) => Domain != null
        ? domain != null && (SubdomainsOnly ? domain.EndsWith(Domain, StringComparison.Ordinal)
            : domain == Domain || IncludeSubdomains && domain.EndsWith("." + Domain, StringComparison.Ordinal))
        : app != null && string.Equals(ApplicationName ?? ApplicationPath, ApplicationName == null ? app : Path.GetFileName(app), StringComparison.OrdinalIgnoreCase);
}

public sealed class MultiServerDecision
{
    public ServerRouteTarget Requested { get; }
    public ServerRouteTarget Effective { get; }
    public string Reason { get; }
    internal MultiServerDecision(ServerRouteTarget requested, ServerRouteTarget effective, string reason)
    { Requested = requested; Effective = effective; Reason = reason; }
}

// Compiled from a captured snapshot, independent of changes to the editor or profile list.
public sealed class MultiServerPolicy
{
    private readonly MultiServerRule[] rules;
    private readonly HashSet<string> profiles;
    public string DefaultProfileId { get; }
    public long Revision { get; }
    public string[] ReferencedProfileIds => rules.Select(r => r.Target.ProfileId).Concat(new[] { DefaultProfileId })
        .Where(id => id != null).Distinct(StringComparer.Ordinal).ToArray();
    public MultiServerPolicy(long revision, string defaultProfileId, IEnumerable<MultiServerRule> rules, IEnumerable<string> availableProfileIds)
    {
        if (revision < 0 || string.IsNullOrWhiteSpace(defaultProfileId)) throw new ArgumentException("Invalid policy identity");
        Revision = revision; DefaultProfileId = defaultProfileId;
        this.rules = (rules ?? throw new ArgumentNullException(nameof(rules))).ToArray();
        if (this.rules.Length > 12000 || this.rules.Any(r => r == null)) throw new ArgumentException("Invalid rules");
        var duplicate = this.rules.GroupBy(r => r.Domain != null ? "d:" + r.Domain + ":" + r.IncludeSubdomains : (r.ApplicationName != null ? "n:" + r.ApplicationName.ToUpperInvariant() : "a:" + r.ApplicationPath.ToUpperInvariant()))
            .Any(g => g.Count() > 1);
        if (duplicate) throw new ArgumentException("Duplicate route assignment");
        profiles = new HashSet<string>(availableProfileIds ?? throw new ArgumentNullException(nameof(availableProfileIds)), StringComparer.Ordinal);
    }
    public MultiServerDecision Decide(string applicationPath, string domain, bool ownerKnown = true)
    {
        string host = null;
        if (domain != null) { try { host = MultiServerRule.NormalizeDomain(domain); } catch (ArgumentException) { } }
        var matched = rules.Where(r => r.Matches(applicationPath, host))
            .OrderBy(r => r.Target.Kind == ServerRouteKind.Block ? 0 : r.Domain != null ? 1 : r.ApplicationPath != null ? 2 : 3)
            .ThenByDescending(r => r.Domain?.Length ?? 0).ThenBy(r => r.IncludeSubdomains).FirstOrDefault();
        var requested = matched?.Target ?? new ServerRouteTarget(ServerRouteKind.Default);
        if (matched == null && !ownerKnown && rules.Any(r => r.ApplicationPath != null || r.ApplicationName != null))
            return new MultiServerDecision(requested, new ServerRouteTarget(ServerRouteKind.Block), "unknown-owner");
        var resolved = requested.Kind == ServerRouteKind.Default ? new ServerRouteTarget(ServerRouteKind.Profile, DefaultProfileId) : requested;
        if (resolved.Kind == ServerRouteKind.Profile && !profiles.Contains(resolved.ProfileId))
            return new MultiServerDecision(requested, new ServerRouteTarget(ServerRouteKind.Block), "missing-profile");
        return new MultiServerDecision(requested, resolved, matched == null ? "default" : matched.Domain != null ? "site" : "application");
    }
}
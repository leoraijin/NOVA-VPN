using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace NovaVpn;

// Persisted editor document. Profile bindings are identities, never keys or profile JSON.
[DataContract]
public sealed class MultiServerSettings
{
    [DataMember(IsRequired=true)] public int Schema = 1;
    [DataMember(IsRequired=true)] public long Revision;
    [DataMember(IsRequired=true)] public string DefaultProfileId;
    [DataMember(IsRequired=true)] public List<MultiServerRuleRecord> Rules = new List<MultiServerRuleRecord>();
    [DataMember] public bool RequiresRebinding;
    [DataMember(EmitDefaultValue=false)] public string SecondaryProfileId;

    public string[] ReferencedProfileIds { get { Validate();return new MultiServerPolicy(Revision,DefaultProfileId,Rules.Select(r=>r.ToRule()),Array.Empty<string>()).ReferencedProfileIds; } }
    public static string[] ProfileReferences(AppState state) => new[]{state?.Routing?.MultiServer,state?.RoutingMigrationBackup?.MultiServer,state?.MultiServerRoutingHistory?.SavedAssignments}
        .Where(s=>s!=null).SelectMany(s=>s.ReferencedProfileIds.Concat(!s.RequiresRebinding && s.SecondaryProfileId != null
            ? new[]{s.SecondaryProfileId} : Array.Empty<string>())).Concat(new[]{state?.MultiServerRoutingHistory?.SelectedProfileId,state?.MultiServerRoutingHistory?.FallbackRootProfileId}.Where(id=>!String.IsNullOrEmpty(id)))
        .Distinct(StringComparer.Ordinal).ToArray();
    public void Validate()
    {
        if (Schema != 1 || Rules == null || Rules.Count > 12000 || Rules.Any(r => r == null))
            throw new SerializationException("Некорректная версия или правила назначений серверов.");
        if (SecondaryProfileId != null && (String.IsNullOrWhiteSpace(SecondaryProfileId) ||
            SecondaryProfileId.Length > 128 || SecondaryProfileId == DefaultProfileId))
            throw new SerializationException("Выберите разные конфиги для основного VPN и VPN-2.");
        new MultiServerPolicy(Revision,DefaultProfileId,Rules.Select(r => r.ToRule()),Array.Empty<string>());
    }
    [OnDeserialized] private void Read(StreamingContext context) { Validate(); }
    public MultiServerSettings Copy()
    {
        Validate();
        return new MultiServerSettings { Schema=Schema, Revision=Revision, DefaultProfileId=DefaultProfileId,
            SecondaryProfileId=SecondaryProfileId, RequiresRebinding=RequiresRebinding, Rules=Rules.Select(r => r.Copy()).ToList() };
    }
    internal void SetSecondaryProfile(string profileId)
    {
        if (profileId == DefaultProfileId)
            throw new InvalidOperationException("Выберите разные конфиги для основного VPN и VPN-2.");
        if (SecondaryProfileId != null)
        {
            foreach (var rule in Rules.Where(rule => rule.Target == "Profile" && rule.ProfileId == SecondaryProfileId))
                rule.ProfileId = profileId;
        }
        SecondaryProfileId = profileId;
    }

    public MultiServerPolicy Compile(IEnumerable<string> availableProfileIds)
    {
        Validate();
        if (RequiresRebinding) throw new InvalidOperationException("Назначьте серверы для импортированных правил маршрутизации.");
        return new MultiServerPolicy(Revision,DefaultProfileId,Rules.Select(r => r.ToRule()),availableProfileIds);
    }
}

[DataContract]
public sealed class MultiServerRuleRecord
{
    [DataMember] public string ApplicationPath;
    [DataMember] public string Domain;
    [DataMember] public bool IncludeSubdomains;
    [DataMember(IsRequired=true)] public string Target;
    [DataMember] public string ProfileId;
    internal string DomainLabel => Domain?.StartsWith(".", StringComparison.Ordinal) == true ? "*" + Domain
        : Domain + (IncludeSubdomains ? " и поддомены" : "");

    public MultiServerRule ToRule()
    {
        if (!Enum.GetNames(typeof(ServerRouteKind)).Contains(Target)) throw new SerializationException("Неизвестное назначение маршрута.");
        var rule = new MultiServerRule(ApplicationPath,Domain,IncludeSubdomains,
            new ServerRouteTarget((ServerRouteKind)Enum.Parse(typeof(ServerRouteKind),Target),ProfileId));
        if (Domain != null && Domain != rule.Domain) throw new SerializationException("Домен должен быть нормализован.");
        return rule;
    }
    public MultiServerRuleRecord Copy() => new MultiServerRuleRecord { ApplicationPath=ApplicationPath,Domain=Domain,
        IncludeSubdomains=IncludeSubdomains,Target=Target,ProfileId=ProfileId };
}
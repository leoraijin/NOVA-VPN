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

    [DataMember(EmitDefaultValue=false)] public bool SecondaryPaused;
    [DataMember(EmitDefaultValue=false)] public List<VpnSlot> AdditionalSlots;
    public SortedDictionary<int,string> Slots {
        get {
            var result = new SortedDictionary<int,string>();
            if (SecondaryProfileId != null) result.Add(2, SecondaryProfileId);
            foreach (var slot in AdditionalSlots ?? new List<VpnSlot>()) {
                if (slot == null || slot.Number < 3 || slot.Number > 10 || result.ContainsKey(slot.Number))
                    throw new SerializationException("Некорректный номер VPN.");
                result.Add(slot.Number, slot.ProfileId);
            }
            return result;
        }
    }
    internal void SetSlot(int number, string profileId) {
        if (number < 2 || number > 10) throw new ArgumentOutOfRangeException(nameof(number));
        if (profileId != null && (profileId == DefaultProfileId || Slots.Any(s => s.Key != number && s.Value == profileId)))
            throw new InvalidOperationException("Этот конфиг уже назначен другому VPN.");
        string old; Slots.TryGetValue(number, out old);
        if (old != null) {
            if (profileId == null) Rules.RemoveAll(r => r.Target == "Profile" && r.ProfileId == old);
            else foreach (var rule in Rules.Where(r => r.Target == "Profile" && r.ProfileId == old)) rule.ProfileId = profileId;
        }
        if (number == 2) SecondaryProfileId = profileId;
        else {
            if (AdditionalSlots == null) AdditionalSlots = new List<VpnSlot>();
            AdditionalSlots.RemoveAll(s => s.Number == number);
            if (profileId != null) AdditionalSlots.Add(new VpnSlot { Number = number, ProfileId = profileId });
        }
        Validate();
    }

    public string[] ReferencedProfileIds { get { Validate();return Rules.Select(rule => rule.ProfileId).Concat(Slots.Values).Concat(new[] {DefaultProfileId}).Where(id => id != null).Distinct(StringComparer.Ordinal).ToArray(); } }
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
        if (Slots.Values.Any(id => String.IsNullOrWhiteSpace(id) || id.Length > 128 || id == DefaultProfileId) ||
            Slots.Values.Distinct(StringComparer.Ordinal).Count() != Slots.Count)
            throw new SerializationException("Выберите разные конфиги для каждого VPN.");
        new MultiServerPolicy(Revision,DefaultProfileId,EffectiveRules(),Array.Empty<string>());
    }
    [OnDeserialized] private void Read(StreamingContext context) { Validate(); }
    public MultiServerSettings Copy()
    {
        Validate();
        return new MultiServerSettings { Schema=Schema, Revision=Revision, DefaultProfileId=DefaultProfileId,
            SecondaryProfileId=SecondaryProfileId, SecondaryPaused=SecondaryPaused, AdditionalSlots=AdditionalSlots?.Select(s => new VpnSlot {Number=s.Number, ProfileId=s.ProfileId}).ToList(), RequiresRebinding=RequiresRebinding, Rules=Rules.Select(r => r.Copy()).ToList() };
    }
    internal void SetSecondaryProfile(string profileId) => SetSlot(2, profileId);

    // Keep all user entries in the document; resolve identical selectors only for the core.
    // Blocks win. Explicit VPN assignments for .ru win over identical direct entries.
    internal IEnumerable<MultiServerRule> EffectiveRules()
    {
        return Rules.Select(record => record.ToRule())
            .OrderBy(rule => rule.Target.Kind == ServerRouteKind.Block ? 0 :
                rule.Target.Kind == ServerRouteKind.Direct ? (StrictDirectRouting.IsRussianSelector(rule.Domain) ? 5 : 1) :
                rule.Target.Kind == ServerRouteKind.Profile && rule.Target.ProfileId == SecondaryProfileId ? 2 :
                rule.Target.Kind == ServerRouteKind.Profile && rule.Target.ProfileId != DefaultProfileId ? 3 : 4)
            .GroupBy(rule => rule.Domain != null ? "d:" + rule.Domain + ":" + rule.IncludeSubdomains :
                (rule.ApplicationName != null ? "n:" + rule.ApplicationName.ToUpperInvariant() : "a:" + rule.ApplicationPath.ToUpperInvariant()), StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(rule => SecondaryPaused && rule.Target.Kind == ServerRouteKind.Profile
                ? new MultiServerRule(rule.ApplicationPath, rule.Domain, rule.IncludeSubdomains,
                    new ServerRouteTarget(ServerRouteKind.Default), rule.ApplicationName)
                : rule);
    }

    public MultiServerPolicy Compile(IEnumerable<string> availableProfileIds)
    {
        Validate();
        if (RequiresRebinding) throw new InvalidOperationException("Назначьте серверы для импортированных правил маршрутизации.");
        return new MultiServerPolicy(Revision,DefaultProfileId,EffectiveRules(),availableProfileIds);
    }
}

[DataContract]
public sealed class MultiServerRuleRecord
{
    [DataMember] public string ApplicationPath;
    [DataMember(EmitDefaultValue=false)] public string ApplicationName;
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
            new ServerRouteTarget((ServerRouteKind)Enum.Parse(typeof(ServerRouteKind),Target),ProfileId),ApplicationName);
        if (Domain != null && Domain != rule.Domain) throw new SerializationException("Домен должен быть нормализован.");
        return rule;
    }
    public MultiServerRuleRecord Copy() => new MultiServerRuleRecord { ApplicationPath=ApplicationPath,ApplicationName=ApplicationName,Domain=Domain,
        IncludeSubdomains=IncludeSubdomains,Target=Target,ProfileId=ProfileId };
}
[DataContract]
public sealed class VpnSlot {
    [DataMember(IsRequired=true)] public int Number;
    [DataMember(IsRequired=true)] public string ProfileId;
}

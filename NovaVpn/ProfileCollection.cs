using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class ProfileCollection
{
    [DataMember] public string Id = Guid.NewGuid().ToString("N");
    [DataMember] public string Name = "Новая коллекция";
    [DataMember] public List<string> ProfileIds = new List<string>();
    [DataMember] public List<string> CountryCodes = new List<string>();
    [DataMember] public List<string> Protocols = new List<string>();
    [DataMember] public string SelectionMode = "VerifiedFastest";
    [DataMember] public bool FavoritesOnly;
}

public static class ProfileCollectionService
{
    public static readonly string[] SupportedProtocols = { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "sing-box" };
    public static string Country(string value)
    {
        string code = (value ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        if (code.Length != 2 || code.Any(c => c < 'A' || c > 'Z')) return "";
        try { var region = new RegionInfo(code); return region.TwoLetterISORegionName == code ? code : ""; }
        catch (ArgumentException) { return ""; }
    }

    public static void Normalize(AppState state)
    {
        if (state.ProfileCollections == null) state.ProfileCollections = new List<ProfileCollection>();
        state.ProfileCollections.RemoveAll(c => c == null);
        foreach (var collection in state.ProfileCollections)
        {
            if (string.IsNullOrWhiteSpace(collection.Id)) collection.Id = Guid.NewGuid().ToString("N");
            collection.Name = (collection.Name ?? "").Trim();
            collection.ProfileIds = (collection.ProfileIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            // Preserve invalid imported constraints so validation fails closed instead of widening scope.
            collection.CountryCodes = (collection.CountryCodes ?? new List<string>()).Select(c => Country(c) is string normalized && normalized != "" ? normalized : (c ?? "").Trim().ToUpperInvariant()).Distinct().ToList();
            collection.Protocols = (collection.Protocols ?? new List<string>()).Select(p => (p ?? "").Trim().ToLowerInvariant()).Distinct().ToList();
            collection.SelectionMode = collection.SelectionMode ?? "VerifiedFastest";
        }
        foreach (var profile in state.Profiles ?? new List<VpnProfile>())
            if (profile != null) profile.DeclaredCountryCode = Country(profile.DeclaredCountryCode);
    }

    public static string Validate(ProfileCollection collection)
    {
        if (collection == null) return "Коллекция не найдена.";
        if (string.IsNullOrWhiteSpace(collection.Name) || collection.Name.Length > 80) return "Название должно содержать от 1 до 80 символов.";
        if (collection.ProfileIds == null || collection.ProfileIds.Count > 2000) return "В коллекции допускается до 2000 профилей.";
        if (collection.CountryCodes == null || collection.CountryCodes.Count > 250 || collection.CountryCodes.Any(c => Country(c) == "")) return "Укажите двухбуквенные коды стран, например DE, NL, GB.";
        if (collection.Protocols == null || collection.Protocols.Any(p => !SupportedProtocols.Contains((p ?? "").ToLowerInvariant()))) return "В фильтре есть неизвестный протокол.";
        if (collection.SelectionMode != "VerifiedFastest" && collection.SelectionMode != "Ordered") return "Неизвестный способ выбора сервера.";
        return null;
    }

    public static ProfileCollection Active(AppState state, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(state.ActiveProfileCollectionId)) return null;
        var matches = (state.ProfileCollections ?? new List<ProfileCollection>()).Where(c => c != null && c.Id == state.ActiveProfileCollectionId).Take(2).ToArray();
        if (matches.Length != 1) { error = "Активная коллекция отсутствует или имеет повторяющийся идентификатор. Выберите коллекцию заново."; return null; }
        error = Validate(matches[0]); return matches[0];
    }

    public static string Exclusion(ProfileCollection collection, VpnProfile profile)
    {
        string error = Validate(collection); if (error != null) return error;
        if (profile == null || profile.RetiredFromSubscription) return "Профиль отсутствует или удалён из подписки.";
        if (!collection.ProfileIds.Contains(profile.Id)) return "Профиль не входит в активную коллекцию.";
        if (collection.FavoritesOnly && !profile.IsFavorite) return "Коллекция разрешает только избранные профили.";
        if (collection.Protocols.Count > 0 && !collection.Protocols.Contains((profile.Protocol ?? "").ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)) return "Протокол запрещён настройками коллекции.";
        if (collection.CountryCodes.Count > 0 && !collection.CountryCodes.Contains(Country(profile.DeclaredCountryCode), StringComparer.OrdinalIgnoreCase)) return "Указанная страна профиля не входит в разрешённые страны коллекции.";
        return null;
    }

    public static List<VpnProfile> Candidates(AppState state)
    {
        var collection = Active(state, out string error);
        if (error != null) return new List<VpnProfile>();
        var profiles = (state.Profiles ?? new List<VpnProfile>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id) && !p.RetiredFromSubscription).ToList();
        if (collection == null) return profiles;
        var byId = profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        return collection.ProfileIds.Where(byId.ContainsKey).Select(id => byId[id]).Where(p => Exclusion(collection, p) == null).ToList();
    }

    public static string Exclusion(AppState state, VpnProfile profile)
    {
        var collection = Active(state, out string error);
        return error ?? (collection == null ? null : Exclusion(collection, profile));
    }

    public static VpnProfile BestAvailableFor(AppState state, DateTime now, string network, string core, string excludedId = null)
    {
        var candidates = Candidates(state).Where(p => p.Id != excludedId).ToList();
        var collection = Active(state, out string error);
        if (error != null) return null;
        return collection?.SelectionMode == "Ordered" ? candidates.FirstOrDefault(p => ProfileHealthEvidence.IsCurrent(p, now, network, core)) :
            HealthCheckService.BestAvailableFor(candidates, now, network, core);
    }
}

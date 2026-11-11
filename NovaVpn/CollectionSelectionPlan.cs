using System;
using System.Collections.Generic;
using System.Linq;
namespace NovaVpn;

public sealed class CollectionSelectionPlan
{
    readonly string baseline,selected;readonly AppState prepared;readonly Dictionary<string,string> countries;
    CollectionSelectionPlan(string baseline,string selected,AppState prepared,Dictionary<string,string> countries=null){this.baseline=baseline;this.selected=selected;this.prepared=prepared;this.countries=countries;}
    public static CollectionSelectionPlan Prepare(AppState target,string selected)
    {
        string baseline=FullStateRestorePlan.Fingerprint(target);var candidate=FullTransfer.Copy(target);candidate.ActiveProfileCollectionId=String.IsNullOrEmpty(selected)?null:selected;
        string error;ProfileCollectionService.Active(candidate,out error);if(error!=null)throw new InvalidOperationException(error);
        StateStore.NormalizeForSerialization(candidate);
        return new CollectionSelectionPlan(baseline,candidate.ActiveProfileCollectionId,FullTransfer.Copy(candidate));
    }
    public static CollectionSelectionPlan PrepareEdit(AppState target,string expectedBaseline,string existingId,ProfileCollection accepted,IDictionary<string,string> countryChanges,bool delete)
    {
        string baseline=FullStateRestorePlan.Fingerprint(target);
        if(String.IsNullOrEmpty(expectedBaseline)||baseline!=expectedBaseline)throw new InvalidOperationException("Настройки изменились, пока редактор был открыт. Откройте коллекцию заново.");
        var candidate=FullTransfer.Copy(target);var matches=candidate.ProfileCollections.Where(c=>c.Id==existingId).ToArray();
        if(existingId!=null&&matches.Length!=1||delete&&existingId==null)throw new InvalidOperationException("Коллекция изменилась. Откройте её заново.");
        var countries=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!delete){
            string error=ProfileCollectionService.Validate(accepted);if(error!=null)throw new InvalidOperationException(error);
            if(String.IsNullOrWhiteSpace(accepted.Id)||existingId!=null&&accepted.Id!=existingId||candidate.ProfileCollections.Any(c=>c.Id==accepted.Id&&c.Id!=existingId))throw new InvalidOperationException("Идентификатор коллекции уже используется или изменился.");
            foreach(var pair in countryChanges??new Dictionary<string,string>()){
                string code=ProfileCollectionService.Country(pair.Value);if(!String.IsNullOrWhiteSpace(pair.Value)&&code=="")throw new FormatException("Некорректный код страны профиля.");countries.Add(pair.Key,code);
            }
            int index=existingId==null?candidate.ProfileCollections.Count:candidate.ProfileCollections.IndexOf(matches[0]);
            if(existingId!=null)candidate.ProfileCollections.RemoveAt(index);candidate.ProfileCollections.Insert(index,accepted);candidate.ActiveProfileCollectionId=accepted.Id;
            foreach(var profile in candidate.Profiles)if(countries.TryGetValue(profile.Id,out string country))profile.DeclaredCountryCode=country;
        }else{candidate.ProfileCollections.Remove(matches[0]);if(candidate.ActiveProfileCollectionId==existingId)candidate.ActiveProfileCollectionId=null;}
        // Freeze the dialog object before normalization can touch any of its lists.
        candidate=FullTransfer.Copy(candidate);StateStore.NormalizeForSerialization(candidate);
        return new CollectionSelectionPlan(baseline,candidate.ActiveProfileCollectionId,FullTransfer.Copy(candidate),countries);
    }
    public void Apply(AppState target,Func<bool> canApply,Action<AppState> persist)
    {
        if(target==null||canApply==null||persist==null)throw new ArgumentNullException("collection selection");
        void Check(){if(!canApply()||FullStateRestorePlan.Fingerprint(target)!=baseline)throw new InvalidOperationException("Настройки изменились. Выберите коллекцию заново.");}
        Check();var staged=FullTransfer.Copy(prepared);string expected=FullStateRestorePlan.Fingerprint(staged);persist(staged);
        if(FullStateRestorePlan.Fingerprint(staged)!=expected)throw new InvalidOperationException("Не удалось подтвердить сохранённый выбор коллекции.");
        Check();var committed=FullTransfer.Copy(staged);
        if(countries!=null){target.ProfileCollections=committed.ProfileCollections;foreach(var profile in target.Profiles)if(countries.TryGetValue(profile.Id,out string country))profile.DeclaredCountryCode=country;}
        target.ActiveProfileCollectionId=selected;
    }
}

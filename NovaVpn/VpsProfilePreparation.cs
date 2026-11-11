using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaVpn;

// Prepares a detached profile for verification/review. It does not authorize
// activation, modify AppState, persist data, connect a core or reuse old health.
public static class VpsProfilePreparation
{
    public static VpnProfile Prepare(VpsOperationRecord candidate,VpsServerStatus status,VpnProfile existing=null,VpsOperationRecord replaced=null)
    {
        if(candidate==null||status==null)throw new ArgumentNullException("VPS profile context");
        candidate.Validate();
        if(candidate.Phase!="committed"||!status.MatchesCandidate(candidate)||status.Revision<candidate.ExpectedRevision+1)throw new InvalidOperationException("Текущее состояние VPS не подтверждает выбранную конфигурацию.");
        if(replaced!=null){
            replaced.Validate();
            if(replaced.Phase!="committed"||replaced.TargetIdentityKey!=candidate.TargetIdentityKey||replaced.InstallationId!=candidate.InstallationId||replaced.ProfileId!=candidate.ProfileId||status.Revision<replaced.ExpectedRevision+1)throw new InvalidOperationException("Исходная конфигурация VPS относится к другой установке.");
        }
        if(existing==null&&replaced!=null)throw new InvalidOperationException("Исходный профиль VPS отсутствует. Сначала проверьте список профилей.");
        var result=candidate.CreateCandidateProfile();
        if(existing!=null){
            if(existing.Id!=candidate.ProfileId||!String.IsNullOrEmpty(existing.SubscriptionUrl)||!String.IsNullOrEmpty(existing.SubscriptionId)||existing.RetiredFromSubscription)throw new InvalidOperationException("Этот идентификатор принадлежит другому профилю или подписке.");
            if(existing.HasMissingCorporateBase||existing.EffectivePortableOutboundJson==CorporateProxy.ExportMarker())throw new InvalidOperationException("Обязательная корпоративная цепочка неполна. Обновление профиля отменено.");
            if(!String.IsNullOrWhiteSpace(existing.RawJson)||existing.EffectivePortableOutboundJson!=null)throw new InvalidOperationException("Собственные параметры выхода не заменяются автоматически.");
            if(!SameBase(existing,result)&&(replaced==null||!SameBase(existing,replaced.CreateCandidateProfile())))throw new InvalidOperationException("Параметры исходного профиля изменились. Обновление не заменяет их автоматически.");
            existing.CorporateProxy?.Validate();
            result.ProviderName=String.IsNullOrWhiteSpace(existing.ProviderName)?result.ProviderName:existing.ProviderName;
            result.UserName=existing.UserName??"";
            result.Name=String.IsNullOrWhiteSpace(existing.Name)?(String.IsNullOrWhiteSpace(result.UserName)?result.ProviderName:result.UserName):existing.Name;
            result.IsFavorite=existing.IsFavorite;result.AddedAt=existing.AddedAt;
            result.DeclaredCountryCode=existing.DeclaredCountryCode;
            result.FallbackProfileIds=(existing.FallbackProfileIds??new List<string>()).ToList();
            result.SetCorporateProxy(existing.CorporateProxy);
        }
        result.LastLatency=result.AverageLatency=result.LastLossPercent=-1;
        result.LastCheckedUtc=default(DateTime);result.ConsecutiveFailures=0;
        result.HealthStatus="Не проверен";result.LastTestMessage="";
        result.HealthMeasurement=result.HealthProfileKey=result.HealthNetworkKey=result.HealthCoreKey=result.HealthTarget="";
        return result;
    }
    internal static bool SameBase(VpnProfile a,VpnProfile b)=>a.Protocol==b.Protocol&&a.Server==b.Server&&a.Port==b.Port&&a.Method==b.Method&&a.Password==b.Password&&a.Transport==b.Transport&&a.UserId==b.UserId&&a.Security==b.Security&&a.Host==b.Host&&a.Path==b.Path&&a.Sni==b.Sni&&a.PublicKey==b.PublicKey&&a.ShortId==b.ShortId&&a.Fingerprint==b.Fingerprint&&a.Flow==b.Flow&&a.Obfs==b.Obfs&&a.ObfsPassword==b.ObfsPassword;
}

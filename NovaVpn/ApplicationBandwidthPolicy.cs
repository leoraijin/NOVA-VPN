using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class ApplicationBandwidthRule
{
    [DataMember] public string UserSid;
    [DataMember] public string ExecutablePath;
    [DataMember] public long UploadBytesPerSecond;
    [DataMember] public long DownloadBytesPerSecond;
    [DataMember] public int BurstBytes=65536;
}

public sealed class ApplicationBandwidthPolicy
{
    public ApplicationBandwidthIdentity Identity{get;}
    public long UploadBytesPerSecond{get;}
    public long DownloadBytesPerSecond{get;}
    public int BurstBytes{get;}
    internal ApplicationBandwidthPolicy(ApplicationBandwidthRule rule){Identity=new ApplicationBandwidthIdentity(rule.UserSid,rule.ExecutablePath);UploadBytesPerSecond=rule.UploadBytesPerSecond;DownloadBytesPerSecond=rule.DownloadBytesPerSecond;BurstBytes=rule.BurstBytes;ApplicationBandwidthBudget.Validate(UploadBytesPerSecond,DownloadBytesPerSecond,BurstBytes);if(UploadBytesPerSecond==0&&DownloadBytesPerSecond==0)throw new InvalidDataException("Укажите хотя бы одно ограничение скорости приложения.");}
}

// Frozen rule set. Captured sessions retain their original budget objects across
// edits, removal and re-addition. Ordinary uncaptured proxies need a restart
// before limits can be enabled; settings alone cannot add capture capability.
public sealed class ApplicationBandwidthPolicySnapshot
{
    public const int MaximumRules=1024;
    public bool Enabled{get;}
    public ReadOnlyCollection<ApplicationBandwidthPolicy> Policies{get;}
    ApplicationBandwidthPolicySnapshot(bool enabled,List<ApplicationBandwidthPolicy> rules){Enabled=enabled;Policies=rules.AsReadOnly();}
    public static ApplicationBandwidthPolicySnapshot Freeze(AppState state)
    {
        if(state==null)throw new ArgumentNullException(nameof(state));
        if(!state.ApplicationLimitsEnabled)return new ApplicationBandwidthPolicySnapshot(false,new List<ApplicationBandwidthPolicy>());
        var saved=state.ApplicationBandwidthRules;if(saved==null||saved.Count<1||saved.Count>MaximumRules)throw new InvalidDataException("Некорректный список ограничений приложений.");
        var policies=new List<ApplicationBandwidthPolicy>();var identities=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var rule in saved){if(rule==null)throw new InvalidDataException("Пустое правило ограничения приложения.");var policy=new ApplicationBandwidthPolicy(rule);if(!identities.Add(policy.Identity.Key))throw new InvalidDataException("Повторное правило для одного пользователя и приложения.");policies.Add(policy);}
        return new ApplicationBandwidthPolicySnapshot(true,policies);
    }
    public ApplicationBandwidthController CreateController()
    {
        if(!Enabled)throw new InvalidOperationException("Ограничения приложений выключены.");
        var controller=new ApplicationBandwidthController(MaximumRules);
        try{foreach(var rule in Policies)controller.Configure(rule.Identity,rule.UploadBytesPerSecond,rule.DownloadBytesPerSecond,rule.BurstBytes);return controller;}catch{controller.Dispose();throw;}
    }
}

namespace NovaVpn;

public sealed class ConnectionVerification
{
    public MultiExitHealthSnapshot MultiExits {get;internal set;}
	public bool Completed { get; set; }
	public string ProfileId { get; set; }
	public System.DateTime CheckedUtc { get; set; }
    public long CheckedTimestamp { get; set; } = -1;
	public ServerMapHealth ProfileProbe { get; set; }
	public bool DnsOk { get; set; }

	public bool InternetOk { get; set; }
    public bool IsVerifiedFor(VpnProfile profile,string networkKey,string coreKey,System.DateTime now)
    {
        if(MultiExits!=null)return false; // Multi-exit evidence must validate the entire referenced catalog.
        var proof=ProfileProbe;
        if(!Completed||!DnsOk||!InternetOk||profile==null||profile.RetiredFromSubscription||string.IsNullOrEmpty(profile.Id)||ProfileId!=profile.Id||
            proof==null||!proof.Supported||!proof.Success||proof.LatencyMs<1||string.IsNullOrEmpty(networkKey)||string.IsNullOrEmpty(coreKey)||
            proof.ProfileKey!=SubscriptionManager.ConfigKey(profile)||proof.NetworkKey!=networkKey||proof.CoreKey!=coreKey)return false;
        var age=now-CheckedUtc;var proofAge=now-proof.CheckedUtc;
        if(CheckedUtc==default(System.DateTime)||proof.CheckedUtc==default(System.DateTime)||age<System.TimeSpan.Zero||proofAge<System.TimeSpan.Zero||
            age>=System.TimeSpan.FromMinutes(3)||proofAge>=System.TimeSpan.FromMinutes(3))return false;
        System.Uri target;
        return System.Uri.TryCreate(proof.Target,System.UriKind.Absolute,out target)&&target.Scheme==System.Uri.UriSchemeHttps&&string.IsNullOrEmpty(target.UserInfo);
    }

    // System-route HTTPS alone is not proof of the selected VPN profile.
    public bool? RecoveryHealthy => !Completed ? (bool?)null : MultiExits!=null ? MultiExits.RecoveryHealthy : !InternetOk ? false :
        ProfileProbe == null || !ProfileProbe.Supported ? (bool?)null : ProfileProbe.Success;


	public int InternetLatencyMs { get; set; } = -1;

	public string Message { get; set; } = "Не проверено";
}

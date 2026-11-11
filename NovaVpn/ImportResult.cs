using System.Collections.Generic;

namespace NovaVpn;

public sealed class ImportResult
{
	public ImportResult(List<VpnProfile> profiles, string subscriptionUrl)
	{
		Profiles = profiles ?? new List<VpnProfile>();
		SubscriptionUrl = subscriptionUrl ?? "";
	}

	public List<VpnProfile> Profiles { get; }

	public string SubscriptionUrl { get; }

	public bool IsSubscription => !string.IsNullOrWhiteSpace(SubscriptionUrl);
    public SubscriptionQuota Quota { get; set; }
    public List<ForeignRoutingPreview> RoutingPreviews { get; } = new List<ForeignRoutingPreview>();
    public List<string> Warnings { get; } = new List<string>();
}

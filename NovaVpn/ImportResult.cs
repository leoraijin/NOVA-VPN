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
}

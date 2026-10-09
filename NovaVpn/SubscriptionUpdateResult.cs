using System.Collections.Generic;

namespace NovaVpn;

public sealed class SubscriptionUpdateResult
{
	public int SubscriptionCount { get; set; }

	public int UpdatedProfiles { get; set; }

	public List<string> Errors { get; } = new List<string>();

	public bool Success => Errors.Count == 0;
}

using System.Collections.Generic;

namespace NovaVpn;

public sealed class SubscriptionUpdateResult
{
	public int SubscriptionCount { get; set; }

	public int UpdatedProfiles { get; set; }

	public List<string> Errors { get; } = new List<string>();
    public List<string> Changes { get; } = new List<string>();
    public List<string> Warnings { get; } = new List<string>();

	public int PartialSubscriptionCount { get; set; }
    public bool Success => Errors.Count == 0 && PartialSubscriptionCount == 0;
}

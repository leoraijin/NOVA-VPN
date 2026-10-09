using System;

namespace NovaVpn;

public sealed class ServerHealthResult
{
	public bool Success { get; set; }

	public int LatencyMs { get; set; } = -1;

	public string Status { get; set; } = "Не проверен";

	public string Message { get; set; } = "";

	public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;

	public int SampleCount { get; set; }

	public int SuccessfulSamples { get; set; }

	public int PacketLossPercent { get; set; }
}

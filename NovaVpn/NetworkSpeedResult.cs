namespace NovaVpn;

public sealed class NetworkSpeedResult
{
	public bool Success { get; set; }

	public double MegabitsPerSecond { get; set; }

	public int ElapsedMilliseconds { get; set; }

	public string Message { get; set; }
}

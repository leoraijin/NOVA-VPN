namespace NovaVpn;

public sealed class ConnectionVerification
{
	public bool Completed { get; set; }
	public string ProfileId { get; set; }
	public System.DateTime CheckedUtc { get; set; }
	public ServerMapHealth ProfileProbe { get; set; }
	public bool DnsOk { get; set; }

	public bool InternetOk { get; set; }

	public int InternetLatencyMs { get; set; } = -1;

	public string Message { get; set; } = "Не проверено";
}

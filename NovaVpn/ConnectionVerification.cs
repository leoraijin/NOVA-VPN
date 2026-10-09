namespace NovaVpn;

public sealed class ConnectionVerification
{
	public bool DnsOk { get; set; }

	public bool InternetOk { get; set; }

	public int InternetLatencyMs { get; set; } = -1;

	public string Message { get; set; } = "Не проверено";
}

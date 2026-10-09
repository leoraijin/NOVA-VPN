namespace NovaVpn;

public sealed class RoutingDecision
{
	public string Route { get; set; }

	public string Reason { get; set; }

	public string DisplayRoute => Route == "proxy" ? "Через VPN" : Route == "direct" ? "Напрямую" : "Блокировать";
}

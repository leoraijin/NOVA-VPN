namespace NovaVpn;

public sealed class RoutingDecision
{
	public string Route { get; set; }

	public string Reason { get; set; }
    public bool IsCertain { get; set; }
    public int RuleIndex { get; set; }
    public string OutboundTag { get; set; }
    public string ProfileId { get; set; }
    public string ProfileName { get; set; }

	public string DisplayRoute => Route == "proxy" ? ((IsCertain ? "Через VPN" : "Возможно через VPN") + (string.IsNullOrEmpty(ProfileName) ? "" : " · " + ProfileName)) : Route == "direct" ? (IsCertain ? "Напрямую" : "Возможно напрямую") : Route == "block" ? (IsCertain ? "Блокировать" : "Возможна блокировка") : Route == "dns" ? (IsCertain ? "Обработка DNS" : "Возможна обработка DNS") : "Нужны данные";
}

// Safe display identity captured with a compiled configuration; contains no connection settings.
public sealed class RoutingExitIdentity
{
    public string ProfileId { get; }
    public string ProfileName { get; }
    public RoutingExitIdentity(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new System.ArgumentException("Missing profile ID");
        ProfileId=id;
        name=(name??"").Replace("\r"," ").Replace("\n"," ");
        ProfileName=name.Length>120?name.Substring(0,120):name;
    }
}
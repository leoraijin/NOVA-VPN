using System;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class VpnProfile
{
    [DataMember] public CorporateProxySettings CorporateProxy;
	[DataMember]
	public string Id = Guid.NewGuid().ToString("N");

	[DataMember]
	public string Name = "Новый сервер";
    [DataMember] public System.Collections.Generic.List<string> FallbackProfileIds = new System.Collections.Generic.List<string>();
    [DataMember] public string DeclaredCountryCode = "";
    [DataMember] public string ProviderName = "";
    [DataMember] public string UserName = "";
    [DataMember] public string SubscriptionId = "";
    [DataMember] public bool RetiredFromSubscription;
    [DataMember] public string HealthMeasurement = "";
    [DataMember] public string HealthProfileKey = "";
    [DataMember] public string HealthNetworkKey = "";
    [DataMember] public string HealthCoreKey = "";
    [DataMember] public string HealthTarget = "";

	[DataMember]
	public string Protocol;

	[DataMember]
	public string Server;

	[DataMember]
	public int Port;

	[DataMember]
	public string UserId;

	[DataMember]
	public string Password;

	[DataMember]
	public string Security;

	[DataMember]
	public string Transport;

	[DataMember]
	public string Host;

	[DataMember]
	public string Path;

	[DataMember]
	public string Sni;

	[DataMember]
	public string PublicKey;

	[DataMember]
	public string ShortId;

	[DataMember]
	public string Fingerprint;

	[DataMember]
	public string Flow;

	[DataMember]
	public string Method;

	[DataMember]
	public string Obfs;

	[DataMember]
	public string ObfsPassword;

	[DataMember]
	public string RawJson;
    private string basePortableOutboundJson;
    private bool requiresCorporateProxy;
    private bool hasCorporateBase;
    // Older clients know only PortableOutboundJson. A required-proxy marker
    // prevents them from silently discarding the additional connection policy.
    [DataMember] public string PortableOutboundJson {
        get => CorporateProxy?.Enabled == true || requiresCorporateProxy ? NovaVpn.CorporateProxy.ExportMarker() : basePortableOutboundJson;
        set { if (value == NovaVpn.CorporateProxy.ExportMarker()) requiresCorporateProxy = true; else { basePortableOutboundJson = value; requiresCorporateProxy = false; } }
    }
    [DataMember(EmitDefaultValue = false)] private string CorporateBaseOutboundJson {
        // Encode null explicitly too: missing metadata must not be mistaken for
        // a normal profile whose original portable outbound was null.
        get => CorporateProxy?.Enabled == true && !HasMissingCorporateBase ? new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(basePortableOutboundJson) : null;
        set {
            object decoded;
            try { decoded = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(value); }
            catch (ArgumentException) { throw new SerializationException("Не удалось восстановить обязательные параметры корпоративной цепочки."); }
            if (decoded != null && !(decoded is string)) throw new SerializationException("Некорректные обязательные параметры корпоративной цепочки.");
            basePortableOutboundJson = decoded as string; hasCorporateBase = true;
        }
    }
    internal string EffectivePortableOutboundJson => requiresCorporateProxy && (CorporateProxy?.Enabled != true || !hasCorporateBase) ? NovaVpn.CorporateProxy.ExportMarker() : basePortableOutboundJson;
    public bool HasMissingCorporateBase => requiresCorporateProxy && !hasCorporateBase;
    public void SetCorporateProxy(CorporateProxySettings settings) {
        if (HasMissingCorporateBase) throw new InvalidOperationException("Исходные параметры VPN-профиля отсутствуют. Восстановите полный Windows-пакет или заново импортируйте профиль.");
        CorporateProxy = settings?.Copy(); requiresCorporateProxy = false;
    }

	[DataMember]
	public string Source;

	[DataMember]
	public DateTime AddedAt = DateTime.UtcNow;

	[DataMember]
	public int LastLatency = -1;

	[DataMember]
	public int AverageLatency = -1;

	[DataMember]
	public DateTime LastCheckedUtc;

	[DataMember]
	public int ConsecutiveFailures;

	[DataMember]
	public int LastLossPercent;

	[DataMember]
	public string HealthStatus = "Не проверен";

	[DataMember]
	public string LastTestMessage = "";

	[DataMember]
	public bool IsFavorite;

	[DataMember]
	public string SubscriptionUrl = "";

	[DataMember]
	public DateTime SubscriptionUpdatedUtc;

	public string Endpoint => $"{Server}:{Port}";

	public string ProtocolLabel
	{
		get
		{
			if (string.IsNullOrWhiteSpace(Protocol))
			{
				return "UNKNOWN";
			}
			return Protocol.ToUpperInvariant();
		}
	}
}

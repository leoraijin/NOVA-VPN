using System;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class VpnProfile
{
	[DataMember]
	public string Id = Guid.NewGuid().ToString("N");

	[DataMember]
	public string Name = "Новый сервер";

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

using System;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class ConnectionHistoryItem
{
	[DataMember]
	public DateTime TimestampUtc;

	[DataMember]
	public string Status;

	[DataMember]
	public string ProfileName;

	[DataMember]
	public int DurationSeconds;

	[DataMember]
	public string Details;
}

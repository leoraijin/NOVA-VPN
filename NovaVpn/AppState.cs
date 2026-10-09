using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class AppState
{
	[DataMember]
	public int SchemaVersion = 10;

	[DataMember]
	public List<VpnProfile> Profiles = new List<VpnProfile>();

	[DataMember]
	public string SelectedProfileId;

	[DataMember]
	public RoutingSettings Routing = new RoutingSettings();

	[DataMember]
	public bool AutoConnect;

	[DataMember]
	public bool StartWithWindows;

	[DataMember]
	public bool MinimizeToTray = true;

	[DataMember]
	public string Theme = "Dark";

	[DataMember]
	public bool AutoReconnect = true;

	[DataMember]
	public bool AutoSelectBestServer = true;

	[DataMember]
	public bool KillSwitch;

	[DataMember]
	public bool ShowAnimations = true;

	[DataMember]
	public bool ShowNotifications = true;

	[DataMember]
	public bool SuppressUpdatePrompts;

	[DataMember]
	public string LastSeenReleaseNotesVersion = String.Empty;

	[DataMember]
	public bool ShowTrainingOnStartup = true;

	[DataMember]
	public bool ShowTrainingHints = true;

	[DataMember]
	public int SubscriptionUpdateIntervalHours = 24;

	[DataMember]
	public DateTime LastSubscriptionUpdateUtc;

	[DataMember]
	public DateTime PauseUntilUtc;

	[DataMember]
	public List<ConnectionHistoryItem> ConnectionHistory = new List<ConnectionHistoryItem>();

	[DataMember]
	public VisualPreferences Visual = new VisualPreferences();

	[DataMember]
	public ZapretSettings Zapret = new ZapretSettings();
}

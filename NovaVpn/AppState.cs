using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class AppState
{
    [DataMember] public RoutingSettings RoutingMigrationBackup;
    [DataMember] public MultiServerRoutingHistory MultiServerRoutingHistory;
    [DataMember] public List<GamingHistoryEntry> GamingHistory = new List<GamingHistoryEntry>();
    [DataMember] public List<InstalledGame> ManualGames = new List<InstalledGame>();
    [DataMember] public List<GamePreferences> GamePreferences = new List<GamePreferences>();
	[DataMember]
	public int SchemaVersion = 17;
    [DataMember] public bool ApplicationLimitsEnabled;
    [DataMember] public List<ApplicationBandwidthRule> ApplicationBandwidthRules = new List<ApplicationBandwidthRule>();
    [DataMember] public bool CoreEcoMode;
    [DataMember] public List<SubscriptionRecord> Subscriptions = new List<SubscriptionRecord>();
    [DataMember] public IdentityState SubscriptionIdentities = new IdentityState();

    [DataMember] public int EngineSelection;

	[DataMember]
	public List<VpnProfile> Profiles = new List<VpnProfile>();

	[DataMember]
	public string SelectedProfileId;
    [DataMember] public string FallbackRootProfileId;
    [DataMember] public List<ProfileCollection> ProfileCollections = new List<ProfileCollection>();
    [DataMember] public string ActiveProfileCollectionId;
    [DataMember] public List<LocalProxyEndpoint> LocalProxies = new List<LocalProxyEndpoint>();

	[DataMember]
	public RoutingSettings Routing = new RoutingSettings();

	[DataMember]
	public bool AutoConnect;

	[DataMember]
	public bool StartWithWindows;

	[DataMember]
	public bool StartConnectedWithWindows;

	[DataMember]
	public string LastWorkingProfileId;

	[DataMember]
	public string LastConnectedProfileId;

	[DataMember]
	public bool MinimizeToTray = true;

	[DataMember]
	public string Theme = "Dark";

	[DataMember]
	public bool AutoReconnect = true;

	[DataMember]
	public bool AutoSelectBestServer = true;

	[DataMember]
	// Legacy strict-TUN option. Independent WFP protection is owned by the service,
	// and must never be inferred from this persisted UI preference.
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


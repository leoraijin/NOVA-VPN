using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class ZapretSettings
{
	[DataMember] public string Mode = "VPN";
	[DataMember] public string RepositoryOwner = "Flowseal";
	[DataMember] public string RepositoryName = "zapret-discord-youtube";
	[DataMember] public string RepositoryBranch = "master";
	[DataMember] public string InstalledVersion = "";
	[DataMember] public string LatestVersion = "";
	[DataMember] public string LastCheckedUtcText = "";
	[DataMember] public string LastUpdateMessage = "";
	[DataMember] public bool AutoCheckUpdates = true;
	[DataMember] public bool DisableStrategySelection;
	[DataMember] public string SelectedStrategy = "";
	[DataMember] public string StrategyCheckMessage = "Ещё не проверено";
	[DataMember] public List<string> StandardDomains = new List<string>();
	[DataMember] public bool RoutingChangesApplied;
	[DataMember] public List<string> SavedProxyDomains = new List<string>();
	[DataMember] public List<string> AddedDirectDomains = new List<string>();
	[DataMember] public List<string> SavedProxyProcesses = new List<string>();
	[DataMember] public List<string> AddedDirectProcesses = new List<string>();
	[DataMember] public List<string> SavedProxyProcessPaths = new List<string>();
	[DataMember] public List<string> AddedDirectProcessPaths = new List<string>();

	public ZapretSettings Clone()
	{
		ZapretSettings copy = new ZapretSettings();
		copy.RepositoryOwner = RepositoryOwner;
		copy.RepositoryName = RepositoryName;
		copy.RepositoryBranch = RepositoryBranch;
		copy.Mode = Mode;
		copy.InstalledVersion = InstalledVersion;
		copy.LatestVersion = LatestVersion;
		copy.LastCheckedUtcText = LastCheckedUtcText;
		copy.LastUpdateMessage = LastUpdateMessage;
		copy.AutoCheckUpdates = AutoCheckUpdates;
		copy.DisableStrategySelection = DisableStrategySelection;
		copy.SelectedStrategy = SelectedStrategy;
		copy.StrategyCheckMessage = StrategyCheckMessage;
		copy.StandardDomains = StandardDomains == null ? new List<string>() : new List<string>(StandardDomains);
		copy.RoutingChangesApplied = RoutingChangesApplied;
		copy.SavedProxyDomains = SavedProxyDomains == null ? new List<string>() : new List<string>(SavedProxyDomains);
		copy.AddedDirectDomains = AddedDirectDomains == null ? new List<string>() : new List<string>(AddedDirectDomains);
		copy.SavedProxyProcesses = SavedProxyProcesses == null ? new List<string>() : new List<string>(SavedProxyProcesses);
		copy.AddedDirectProcesses = AddedDirectProcesses == null ? new List<string>() : new List<string>(AddedDirectProcesses);
		copy.SavedProxyProcessPaths = SavedProxyProcessPaths == null ? new List<string>() : new List<string>(SavedProxyProcessPaths);
		copy.AddedDirectProcessPaths = AddedDirectProcessPaths == null ? new List<string>() : new List<string>(AddedDirectProcessPaths);
		return copy;
	}
}

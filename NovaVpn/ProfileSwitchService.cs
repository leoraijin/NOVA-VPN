using System;
using System.Linq;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class ProfileSwitchResult
{
	public VpnProfile PreviousProfile { get; internal set; }
	public VpnProfile TargetProfile { get; internal set; }
	public bool TargetConnected { get; internal set; }
	public bool PreviousRestored { get; internal set; }
	public Exception TargetError { get; internal set; }
	public Exception RestoreError { get; internal set; }
}

/// <summary>Restarts a live connection on a selected VPN profile and rolls back on failure.</summary>
public static class ProfileSwitchService
{
	public static async Task<ProfileSwitchResult> SwitchAsync(
		AppState state,
		VpnProfile target,
		Action stopConnection,
		Func<VpnProfile, Task> startConnection,
		Action persistSelection = null)
	{
		if (state == null) throw new ArgumentNullException(nameof(state));
		if (target == null) throw new ArgumentNullException(nameof(target));
		if (stopConnection == null) throw new ArgumentNullException(nameof(stopConnection));
		if (startConnection == null) throw new ArgumentNullException(nameof(startConnection));

		VpnProfile previous = (state.Profiles ?? new System.Collections.Generic.List<VpnProfile>())
			.FirstOrDefault(profile => profile != null && string.Equals(profile.Id, state.SelectedProfileId, StringComparison.Ordinal))
			?? (state.Profiles ?? new System.Collections.Generic.List<VpnProfile>()).FirstOrDefault(profile => profile != null);
		ProfileSwitchResult result = new ProfileSwitchResult { PreviousProfile = previous, TargetProfile = target };
		state.SelectedProfileId = target.Id;
		persistSelection?.Invoke();

		stopConnection();
		try
		{
			await startConnection(target);
			result.TargetConnected = true;
			return result;
		}
		catch (Exception ex)
		{
			result.TargetError = ex;
		}

		if (previous == null || string.Equals(previous.Id, target.Id, StringComparison.Ordinal))
			return result;

		try
		{
			stopConnection();
			state.SelectedProfileId = previous.Id;
			persistSelection?.Invoke();
			await startConnection(previous);
			result.PreviousRestored = true;
		}
		catch (Exception ex)
		{
			result.RestoreError = ex;
			state.SelectedProfileId = target.Id;
			persistSelection?.Invoke();
		}
		return result;
	}
}

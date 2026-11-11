using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class ProfileSwitchResult
{
    public VpnProfile ConnectedProfile { get; internal set; }
    public bool Cancelled { get; internal set; }
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
			result.ConnectedProfile = target;
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
			result.ConnectedProfile = previous;
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

    private sealed class PreparedSwitch
    {
        internal VpnProfile Seed;
        internal string Root;
        internal bool Restore;
        internal string[] Ids;
        internal Dictionary<string, string> Keys;
        internal bool Current(AppState state)
        {
            var now = ConfiguredFallbackService.BuildPreferred(state.Profiles, Seed.Id, Root, null, Restore ? Seed.Id : null);
            return now.Root?.Id == Root && now.Candidates.Select(p => p.Id).SequenceEqual(Ids) &&
                now.Candidates.All(p => Keys[p.Id] == SubscriptionManager.ConfigKey(p));
        }
    }

    private static PreparedSwitch Prepare(AppState state, VpnProfile seed, string root, bool restore)
    {
        if (seed == null || seed.RetiredFromSubscription || !state.Profiles.Any(p => ReferenceEquals(p, seed)))
            throw new InvalidOperationException("Выбранный профиль больше недоступен.");
        var plan = ConfiguredFallbackService.BuildPreferred(state.Profiles, seed.Id, root, null, restore ? seed.Id : null);
        if (plan.Root == null || plan.Candidates.Count == 0) throw new InvalidOperationException("Нет доступных профилей для переключения.");
        return new PreparedSwitch { Seed = seed, Root = plan.Root.Id, Restore = restore,
            Ids = plan.Candidates.Select(p => p.Id).ToArray(), Keys = plan.Candidates.ToDictionary(p => p.Id, SubscriptionManager.ConfigKey) };
    }

    public static async Task<ProfileSwitchResult> SwitchConfiguredAsync(AppState state, VpnProfile target, Action stopConnection,
        Func<VpnProfile, bool, Task<VpnProfile>> startConnection, Action persistSelection, Func<bool> requested)
    {
        if (state == null || target == null || stopConnection == null || startConnection == null || persistSelection == null || requested == null)
            throw new ArgumentNullException();
        var previous = (state.Profiles ?? new List<VpnProfile>()).FirstOrDefault(p => p != null && p.Id == state.SelectedProfileId);
        string previousRoot = state.FallbackRootProfileId;
        var next = Prepare(state, target, target.Id, false);
        PreparedSwitch prior = null;
        if (previous != null) { try { prior = Prepare(state, previous, previousRoot, true); } catch (InvalidOperationException) { } }
        var result = new ProfileSwitchResult { PreviousProfile = previous, TargetProfile = target };
        if (!requested()) { result.Cancelled = true; return result; }

        async Task<VpnProfile> Start(PreparedSwitch plan)
        {
            if (!requested()) throw new OperationCanceledException("Переключение отменено.");
            if (!plan.Current(state)) throw new InvalidOperationException("Профили или резервная цепочка изменились во время переключения.");
            state.SelectedProfileId = plan.Seed.Id; state.FallbackRootProfileId = plan.Root; persistSelection();
            if (!requested()) throw new OperationCanceledException("Переключение отменено.");
            var winner = await startConnection(plan.Seed, plan.Restore);
            if (!requested()) throw new OperationCanceledException("Переключение отменено.");
            if (winner == null || winner.RetiredFromSubscription || !state.Profiles.Any(p => ReferenceEquals(p, winner)) ||
                !plan.Ids.Contains(winner.Id) || !plan.Current(state) || state.SelectedProfileId != winner.Id || state.FallbackRootProfileId != plan.Root)
                throw new InvalidOperationException("Подключившийся профиль не соответствует выбранной резервной цепочке.");
            persistSelection();
            if (!requested()) throw new OperationCanceledException("Переключение отменено.");
            return winner;
        }
        void CancelSelection()
        {
            state.SelectedProfileId = previous != null && state.Profiles.Any(p => p.Id == previous.Id) ? previous.Id : null;
            state.FallbackRootProfileId = previousRoot != null && state.Profiles.Any(p => p.Id == previousRoot) ? previousRoot : null;
            persistSelection(); result.Cancelled = true;
        }

        // Stop failure aborts before any new selection or launch: no overlapping cores.
        stopConnection();
        try { result.ConnectedProfile = await Start(next); result.TargetConnected = true; return result; }
        catch (Exception error) { result.TargetError = error; }
        try { stopConnection(); }
        catch (Exception cleanup) { result.RestoreError = cleanup; return result; }
        if (result.TargetError is OperationCanceledException || !requested()) { CancelSelection(); return result; }
        if (result.TargetError is TunnelLeaseBusyException || prior == null) return result;
        try { result.ConnectedProfile = await Start(prior); result.PreviousRestored = true; return result; }
        catch (Exception error) { result.RestoreError = error; }
        try { stopConnection(); }
        catch (Exception cleanup) { result.RestoreError = new AggregateException(result.RestoreError, cleanup); return result; }
        if (result.RestoreError is OperationCanceledException || !requested()) { CancelSelection(); return result; }
        state.SelectedProfileId = target.Id; state.FallbackRootProfileId = next.Root; persistSelection();
        return result;
    }
}

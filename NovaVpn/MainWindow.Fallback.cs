using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private bool configuredFallbackBusy;
    private string fallbackFailedProfileId;
    private CancellationTokenSource configuredFallbackCancellation;

    private void EditFallbackProfiles()
    {
        if (configuredFallbackBusy || core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting || profileSwitchInProgress)
        { Toast("Отключите VPN перед изменением резервных подключений.", Warning); return; }
        var plan = ConfiguredFallbackService.Build(state.Profiles, SelectedProfile()?.Id, state.FallbackRootProfileId);
        if (plan.Root == null) { Toast("Сначала добавьте основной профиль.", Warning); return; }
        if (plan.Root.Protocol == "sing-box") { Toast("Резервирование настраивается для отдельных профилей, а не полного конфига.", Warning); return; }
        var window = new FallbackProfilesWindow(plan.Root, state.Profiles, state.Visual) { Owner = this };
        if (window.ShowDialog() != true) return;
        plan.Root.FallbackProfileIds = window.Accepted; state.FallbackRootProfileId = plan.Root.Id;
        if (!snapshotMode) StateStore.Save(state);
        Toast("Резервных профилей: " + window.Accepted.Count, Green);
    }

    private Task StartWithConfiguredFallbackAsync(CancellationToken externalCancellation = default(CancellationToken))
        => StartWithConfiguredFallbackPreferredAsync(null, externalCancellation);

    private async Task StartWithConfiguredFallbackPreferredAsync(string preferredId, CancellationToken externalCancellation)
    {
        if (configuredFallbackBusy) throw new InvalidOperationException("Резервное подключение уже выполняется.");
        if (collectionEditorBusy) throw new InvalidOperationException("Закройте редактор коллекции перед подключением.");
        if (state.Routing?.MultiServer != null) {
            await StartAllowedProfileAsync(null, EffectiveRouting());
            return;
        }
        var selected = SelectedProfile();
        string exclusion = ProfileCollectionService.Exclusion(state, selected);
        if (exclusion != null) throw new InvalidOperationException(exclusion + " Выберите подходящий профиль в коллекции.");
        var plan = ConfiguredFallbackService.BuildPreferred(state.Profiles, selected?.Id, state.FallbackRootProfileId, fallbackFailedProfileId, preferredId);
        plan.Candidates.RemoveAll(p => ProfileCollectionService.Exclusion(state, p) != null);
        if (!plan.IsConfigured || gamingSession != null)
        { await core.StartAsync(selected, EffectiveRouting(), state.Profiles); return; }
        if (SubscriptionManager.IsUpdating || subscriptionReviewBusy) throw new InvalidOperationException("Дождитесь завершения обновления подписок.");
        configuredFallbackBusy = true;
        var configurations = plan.Candidates.ToDictionary(p => p.Id, SubscriptionManager.ConfigKey);
        Func<VpnProfile, bool> current = profile => state.Profiles.Any(p => p.Id == profile.Id && !p.RetiredFromSubscription && SubscriptionManager.ConfigKey(p) == configurations[profile.Id] && ProfileCollectionService.Exclusion(state, p) == null);
        state.FallbackRootProfileId = plan.Root.Id;
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(mapHealthCancellation.Token, externalCancellation))
        {
            configuredFallbackCancellation = cancellation;
            try
            {
                foreach (string warning in plan.Warnings) RecordHistory("Резервирование", warning);
                var connected = await ConfiguredFallbackService.RunAsync(plan, async (profile, token) =>
                {
                    if (!current(profile)) return false;
                    state.SelectedProfileId = profile.Id;
                    await core.StartAsync(profile, EffectiveRouting(), state.Profiles);
                    token.ThrowIfCancellationRequested();
                    if (core.Status != CoreStatus.Connected || core.ActiveProfileId != profile.Id) return false;
                    var verification = core.InitialVerification ?? core.VerifyConnectionAsync();
                    var canceled = new TaskCompletionSource<bool>();
                    using (token.Register(() => canceled.TrySetCanceled()))
                    {
                        await Task.WhenAny(verification, canceled.Task);
                        token.ThrowIfCancellationRequested();
                        var result = await verification;
                        return current(profile) && core.Status == CoreStatus.Connected && core.ActiveProfileId == profile.Id && ReferenceEquals(core.Verification,result) &&
                            result.IsVerifiedFor(profile,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(core.CorePath),DateTime.UtcNow);
                    }
                }, () => core.Stop(), () => !closing && !manualDisconnect && connectionRequested, cancellation.Token,
                (profile, index) => RecordHistory("Резервирование", "Попытка " + (index + 1) + "/" + plan.Candidates.Count + ": " + profile.Name));
                state.SelectedProfileId = connected.Id; fallbackFailedProfileId = null;
                RecordHistory("Резервирование", "Передача данных подтверждена: " + connected.Name);
            }
            catch { state.SelectedProfileId = ProfileCollectionService.Exclusion(state, plan.Root) == null ? plan.Root.Id : selected.Id; throw; }
            finally
            {
                configuredFallbackCancellation = null; configuredFallbackBusy = false;
                if (!snapshotMode && !closing) StateStore.Save(state);
            }
        }
    }
}

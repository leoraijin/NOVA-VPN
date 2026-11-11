using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private readonly ConnectionRecoveryPolicy connectionRecovery = new ConnectionRecoveryPolicy();
    private readonly DispatcherTimer connectionMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
    private bool monitorBusy, monitorSuspended;
    private long networkChangedTimestamp = -1;
    private string monitoredNetwork = "";
    private long recoveryNetworkEpoch;
    private Func<bool> recoveryNetworkAvailable = NetworkInterface.GetIsNetworkAvailable;

    private string AutomaticReconnectProfileId => state.Routing?.MultiServer?.DefaultProfileId ??
        state.Profiles.FirstOrDefault(p => p.Id == state.SelectedProfileId)?.Id;
    private bool WantsAutomaticReconnect => !closing && !manualDisconnect && connectionRequested && state.AutoReconnect &&
        reconnectAttempt < 5 && state.Routing?.MultiServer?.RequiresRebinding != true && AutomaticReconnectProfileId != null && core.Status != CoreStatus.Connected;
    private bool RecoveryNetworkAvailable()
    {
        try { return recoveryNetworkAvailable(); } catch { return false; }
    }
    private bool AutomaticReconnectReady => WantsAutomaticReconnect && !RecoveryOperationBusy &&
        connectionRecovery.NetworkSettled(networkChangedTimestamp) && RecoveryNetworkAvailable();
    private void DeferAutomaticReconnect(double seconds = 2)
    {
        if (!WantsAutomaticReconnect || monitorSuspended) return;
        reconnectTimer.Interval = TimeSpan.FromSeconds(seconds);
        reconnectTimer.Start();
    }
    private bool RecoveryRoutingStillCurrent()
    {
        var applied=core.LastConnectedRouting;
        if(state.Routing?.MultiServer==null&&applied?.MultiServer==null)return true;
        // ID equality is insufficient when rules or DNS changed during an await.
        if(applied==null)return false;
        try{return RoutingMigrationService.Fingerprint(applied)==RoutingMigrationService.Fingerprint(EffectiveRouting());}
        catch{return false;}
    }
    private bool ReconnectionStillCurrent(long networkEpoch) => !closing && !manualDisconnect && connectionRequested &&
        state.AutoReconnect && state.Routing?.MultiServer?.RequiresRebinding != true && !monitorSuspended && networkEpoch == recoveryNetworkEpoch &&
        core.Status == CoreStatus.Connected && AutomaticReconnectProfileId != null && core.ActiveProfileId == AutomaticReconnectProfileId && RecoveryRoutingStillCurrent();
    private async Task TryAutomaticReconnectAsync()
    {
        reconnectTimer.Stop();
        if (snapshotMode || !WantsAutomaticReconnect) return;
        if (!AutomaticReconnectReady) { DeferAutomaticReconnect(); return; }
        long networkEpoch = recoveryNetworkEpoch;
        try
        {
            reconnectAttempt++;
            await StartWithConfiguredFallbackAsync();
            if (!ReconnectionStillCurrent(networkEpoch)) { DeferAutomaticReconnect(); return; }
            currentSessionStartedUtc = DateTime.UtcNow;
            RecordHistory("Переподключено", "Туннель восстановлен автоматически.");
            var connected = SelectedProfile();
            if(state.Routing?.MultiServer==null){connected.ConsecutiveFailures = 0; connected.HealthStatus = "Подключён";}
            StateStore.Save(state); reconnectAttempt = 0;
        }
        catch { DeferAutomaticReconnect(Math.Min(20, reconnectAttempt * 3)); }
    }

    private void InitializeConnectionMonitor()
    {
        if (snapshotMode) return;
        NetworkChange.NetworkAddressChanged += ConnectionAddressChanged;
        SystemEvents.PowerModeChanged += ConnectionPowerChanged;
        connectionMonitor.Tick += async delegate { await MonitorConnectionAsync(); };
        connectionMonitor.Start();
    }

    private void StopConnectionMonitor()
    {
        connectionMonitor.Stop();
        reconnectTimer.Stop();
        if (snapshotMode) return;
        NetworkChange.NetworkAddressChanged -= ConnectionAddressChanged;
        SystemEvents.PowerModeChanged -= ConnectionPowerChanged;
    }

    private void ConnectionAddressChanged(object sender, EventArgs args)
    {
        Dispatcher.BeginInvoke((Action)(() => { if (closing) return; networkChangedTimestamp = Math.Max(0, connectionRecovery.Timestamp); InvalidateNetworkProof(); DeferAutomaticReconnect(); }));
    }

    private void ConnectionPowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (closing) return;
            if (args.Mode == PowerModes.Suspend) { monitorSuspended = true; connectionMonitor.Stop(); reconnectTimer.Stop(); InvalidateNetworkProof(); return; }
            if (args.Mode == PowerModes.Resume)
            {
                monitorSuspended = false; InvalidateNetworkProof();
                networkChangedTimestamp = Math.Max(0, connectionRecovery.Timestamp); connectionMonitor.Start();
                DeferAutomaticReconnect();
                _ = VerifyAfterResumeAsync();
            }
        }));
    }

    private void InvalidateNetworkProof()
    {
        recoveryNetworkEpoch++;
        core.InvalidateNetworkVerification();
        monitoredNetwork = "";
        connectionRecovery.Reset();
        mapHealth.Clear();
        if (currentPage == "home") UpdateVerificationDisplay(); else UpdateSidebarConnectionStatus();
    }
    private async Task VerifyAfterResumeAsync()
    {
        try { await Task.Delay(3500, mapHealthCancellation.Token); await MonitorConnectionAsync(); }
        catch (OperationCanceledException) { }
    }

    private bool RecoveryOperationBusy => closing || monitorSuspended || startupConnecting || profileSwitchInProgress || configuredFallbackBusy || gamingTransitionBusy || core.Status == CoreStatus.Connecting;

    private void HandleRecoveryNetworkAvailability(bool available)
    {
        if (snapshotMode || closing) return;
        reconnectTimer.Stop();
        networkChangedTimestamp = Math.Max(0, connectionRecovery.Timestamp);
        InvalidateNetworkProof();
        if (!connectionRequested || manualDisconnect || !state.AutoReconnect) return;
        if (available) { DeferAutomaticReconnect(); return; }
        // The in-flight start/switch owns its resources and cancellation. A
        // queued network notification must not stop its replacement process.
        if (RecoveryOperationBusy || core.Status == CoreStatus.Disconnected) return;
        try { core.Stop(); }
        catch (Exception error)
        {
            Toast("Сеть недоступна. Не удалось подтвердить остановку VPN: " + LogSanitizer.Sanitize(error.Message), Warning);
            return;
        }
        RecordHistory("Обрыв", "Сеть устройства стала недоступна.");
        Toast("Сеть недоступна. Подключение будет восстановлено автоматически.", Warning);
    }

    private bool RecoveryObservationStillCurrent(long networkEpoch, string profile, DateTime started,
        ConnectionVerification verification, string network)
    {
        return !RecoveryOperationBusy && !manualDisconnect && connectionRequested && state.AutoReconnect &&
            networkEpoch == recoveryNetworkEpoch && connectionRecovery.NetworkSettled(networkChangedTimestamp) &&
            RecoveryNetworkAvailable() && RecoveryRoutingStillCurrent() && core.Status == CoreStatus.Connected && profile == core.ActiveProfileId &&
            started == core.ConnectedAtUtc && verification != null && ReferenceEquals(verification, core.Verification) && verification.Completed &&
            !String.IsNullOrEmpty(network) && network == ProfileHealthEvidence.NetworkKey();
    }

    private async Task MonitorConnectionAsync()
    {
        if (!snapshotMode && !closing)
        {
            if (currentPage == "home") UpdateVerificationDisplay(); else UpdateSidebarConnectionStatus();
        }
        await ReconcileWorkloadsAsync();
        if (snapshotMode || monitorBusy) return;
        if (RecoveryOperationBusy || !connectionRecovery.NetworkSettled(networkChangedTimestamp)) { connectionRecovery.Reset(); return; }
        if (!connectionRequested || manualDisconnect || !state.AutoReconnect) { connectionRecovery.Reset(); return; }
        if (!RecoveryNetworkAvailable()) { monitoredNetwork = ""; connectionRecovery.Reset(); return; }
        monitorBusy = true;
        try
        {
            string network = ProfileHealthEvidence.NetworkKey();
            if (String.IsNullOrEmpty(network)) { monitoredNetwork = ""; connectionRecovery.Reset(); return; }
            if (network != monitoredNetwork) { monitoredNetwork = network; connectionRecovery.Reset(); }
            if (core.Status != CoreStatus.Connected)
            {
                if (reconnectAttempt >= 5) reconnectAttempt = 0;
                if (!reconnectTimer.IsEnabled) { reconnectTimer.Interval = TimeSpan.FromSeconds(2); reconnectTimer.Start(); }
                return;
            }
            long networkEpoch = recoveryNetworkEpoch;
            string profile = core.ActiveProfileId; DateTime started = core.ConnectedAtUtc;
            var verification = await core.VerifyConnectionAsync();
            if (!RecoveryObservationStillCurrent(networkEpoch, profile, started, verification, network)) { connectionRecovery.Reset(); return; }
            bool? healthy = verification.RecoveryHealthy;
            if (!healthy.HasValue) { connectionRecovery.Reset(); return; }
            if (!connectionRecovery.Observe(verification.CheckedTimestamp, healthy.Value, connectionRequested, state.AutoReconnect, false, true)) return;
            RecordHistory("Восстановление", "Две последовательные проверки передачи данных не прошли. Переподключение.");
            core.Stop(); ScheduleReconnect();
        }
        catch (Exception) { if (!closing) RecordHistory("Проверка связи", "Не удалось завершить фоновую проверку. Повтор будет выполнен автоматически."); }
        finally { monitorBusy = false; }
    }
}

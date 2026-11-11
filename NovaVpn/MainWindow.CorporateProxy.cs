using System;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private async Task EditCorporateProxyAsync()
    {
        var profile = SelectedProfile();
        if (profile == null || profile.Protocol == "sing-box") { Toast("Выберите отдельный VPN-профиль.", Warning); return; }
        if (profile.HasMissingCorporateBase) { Toast("Восстановите исходные параметры из полного Windows-пакета или заново импортируйте профиль.", Warning); return; }
        Func<bool> busy = () => core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting || configuredFallbackBusy || profileSwitchInProgress || startupConnecting ||
            collectionEditorBusy || gamingSession != null || gamingCancellation != null || WorkloadProfileInUse(profile.Id);
        if (busy())
        { Toast("Отключите VPN, игровой режим и локальные прокси этого профиля перед изменением цепочки.", Warning); return; }
        string expected = SubscriptionManager.ConfigKey(profile);
        var window = new CorporateProxyWindow(profile, state.Visual, snapshotMode) { Owner = this };
        if (window.ShowDialog() != true || snapshotMode) return;
        if (busy() || !state.Profiles.Contains(profile) || profile.RetiredFromSubscription || SubscriptionManager.ConfigKey(profile) != expected)
        { Toast("Профиль или подключение изменились. Откройте настройки прокси заново.", Warning); return; }
        var previous = profile.CorporateProxy?.Copy(); profile.SetCorporateProxy(window.Accepted);
        try { StateStore.Save(state); }
        catch { profile.SetCorporateProxy(previous); Toast("Не удалось сохранить настройки. Предыдущая цепочка восстановлена.", Danger); return; }
        InvalidateGamingResults(); await ReconcileWorkloadsAsync(); Navigate("servers");
        Toast(profile.CorporateProxy.Enabled ? "Профиль будет подключаться через корпоративный прокси в sing-box." : "Корпоративный прокси для профиля выключен.", Green);
    }
}

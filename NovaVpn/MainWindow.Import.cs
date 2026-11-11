using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private async Task ReviewAndApplyImportAsync(string input)
    {
        if (snapshotMode || closing) return;
        var openedImportState = state;
        if (configuredFallbackBusy) { Toast("Дождитесь резервного подключения или отключите VPN перед импортом.", Warning); return; }
        if ((input ?? "").StartsWith("NOVA-ID1:", StringComparison.Ordinal))
        {
            var transfer = new IdentityWindow(state.SubscriptionIdentities, state.Visual, state,
                () => CanRestoreFullState()) { Owner = this, ImportCode = input };
            if (transfer.ShowDialog() == true)
            {
                if (transfer.FullRestore != null) { RestoreFullState(transfer.FullRestore); BuildShell(); }
                else { RestoreIdentitiesOnly(transfer.Result); }
            }
            return;
        }
        var imported = await LinkParser.ImportWithCancellationAsync(input, mapHealthCancellation.Token);
        if (closing || !ReferenceEquals(openedImportState, state)) return;
        if (configuredFallbackBusy) { Toast("Импорт отложен: выполняется резервное подключение.", Warning); return; }
        Mouse.OverrideCursor = null;
        int added; bool saved = false;
        if (imported.IsSubscription)
        {
            if (!CanReviewSubscriptions() || collectionEditorBusy) return;
            string active = core.ActiveProfileId; int generation = core.RuntimeGeneration;
            var plan = SubscriptionManager.Preview(state, imported);
            if (!ConfirmSubscriptionPlan(plan, imported.Warnings.Concat(imported.RoutingPreviews.Select(p => p.Summary)))) return;
            var owners=RetiredProfileOwners().ToArray();
            Func<bool> canCommit = () => RetiredOwnersUnchanged(owners) && ReferenceEquals(openedImportState, state) && CanReviewSubscriptions() && !collectionEditorBusy && core.ActiveProfileId == active && core.RuntimeGeneration == generation;
            if (!canCommit()) return;
            var commit = SubscriptionCommitPlan.Prepare(state, plan, active, owners);
            commit.Apply(state, canCommit, StateStore.Save); added = plan.Added; saved = true;
        }
        else
        {
            var preview = new ImportPreviewWindow(imported, state.Visual) { Owner = this };
            if (preview.ShowDialog() != true) return;
            if (preview.ReviewRouting)
            {
                if (!CanChangeMigratedRouting()) { Toast("Отключите VPN и изменения Zapret перед переносом правил.", Warning); return; }
                var sample = preview.Accepted.Profiles.FirstOrDefault(p => p.Protocol != "sing-box");
                if (sample == null || core.Engine != VpnEngine.SingBox) { Toast("Для переноса правил выберите sing-box и отдельный профиль сервера.", Warning); return; }
                string expected = RoutingMigrationService.Fingerprint(state.Routing);
                var routingWindow = new ForeignRoutingWindow(preview.Accepted.RoutingPreviews.Single(), sample, state.Routing, state.Visual, false) { Owner = this };
                if (routingWindow.ShowDialog() != true) return;
                var candidate = RoutingOverlayService.Clone(state.Routing); candidate.MigratedPolicy = routingWindow.Accepted.Copy();
                string validation = await core.ValidateAsync(sample, candidate, state.Profiles);
                if (!string.IsNullOrEmpty(validation)) { Toast("Ядро отклонило перенос правил: " + validation, Warning); return; }
                var previousProfiles = state.Profiles; string previousSelection = state.SelectedProfileId; int merged = 0;
                try
                {
                    RoutingMigrationService.Apply(state, routingWindow.Accepted, expected, CanChangeMigratedRouting, current => {
                        current.Profiles = new System.Collections.Generic.List<VpnProfile>(previousProfiles);
                        merged = MergeImportedProfiles(preview.Accepted); StateStore.Save(current);
                    });
                }
                catch { state.Profiles = previousProfiles; state.SelectedProfileId = previousSelection; throw; }
                added = merged; saved = true;
            }
            else added = MergeImportedProfiles(preview.Accepted);
        }
        if (!saved) StateStore.Save(state); await ReconcileWorkloadsAsync(); Navigate("servers"); NotifySubscriptionQuota(); SyncSubscriptionTimer();
        Toast(imported.IsSubscription&&imported.Warnings.Count>0 ? "Подписка импортирована частично. Прежние серверы сохранены." : "Добавлено новых серверов: " + added, imported.IsSubscription&&imported.Warnings.Count>0 ? Warning : Green);
    }

    private bool CanChangeMigratedRouting() => !snapshotMode && !closing && core.Status == CoreStatus.Disconnected && !configuredFallbackBusy && !profileSwitchInProgress && gamingSession == null && state.Zapret?.RoutingChangesApplied != true;

    private async Task ImportFileAsync()
    {
        var file = new OpenFileDialog { Filter = "Экспорт VPN|*.json;*.yaml;*.yml;*.txt|Все файлы|*.*" };
        if (file.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(file.FileName).Length > 4 * 1024 * 1024) throw new FormatException("Файл импорта больше 4 МБ.");
            string text = File.ReadAllText(file.FileName, new UTF8Encoding(false, true));
            await ReviewAndApplyImportAsync(text);
        }
        catch (Exception) { Toast("Не удалось импортировать файл. Проверьте формат экспорта и параметры серверов.", Warning); }
    }

    private async Task ImportQrAsync(string source)
    {
        try
        {
            string[] codes;
            if (source == "file")
            {
                var file = new OpenFileDialog { Filter = "Изображения QR|*.png;*.jpg;*.jpeg;*.bmp" };
                if (file.ShowDialog(this) != true) return;
                codes = await Task.Run(() => QrImportService.ReadFile(file.FileName));
            }
            else if (source == "clipboard") codes = QrImportService.ReadClipboard();
            else
            {
                try
                {
                    Hide(); await Task.Delay(180);
                    using (var region = QrScreenCapture.SelectRegion())
                    {
                        if (region == null) return;
                        codes = QrImportService.Decode(region);
                    }
                }
                finally { Show(); Activate(); }
            }
            string code = ChooseQr(codes);
            if (code != null) await ReviewAndApplyImportAsync(code);
        }
        catch (Exception) { Toast("QR не удалось прочитать или импортировать. Выберите чёткое изображение с полным кодом поддерживаемого формата.", Warning); }
    }

    private string ChooseQr(string[] values)
    {
        string result = null;
        var list = new ListBox { Margin = new Thickness(16), SelectedIndex = 0 };
        foreach (string code in values)
        {
            string label = code.StartsWith("NOVA-ID1:") ? "Зашифрованный перенос NOVA" : Uri.TryCreate(code, UriKind.Absolute, out var uri) ? uri.Scheme + " · " + uri.Host : "Неизвестный формат";
            var item = new ListBoxItem { Content = label, Tag = code, Padding = new Thickness(12) }; list.Items.Add(item);
        }
        list.SelectedIndex = 0;
        var root = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        var proceed = new Button { Content = "Продолжить импорт", Padding = new Thickness(16, 8, 16, 8) };
        actions.Children.Add(cancel); actions.Children.Add(proceed); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions); root.Children.Add(list);
        var window = new Window { Owner = this, Title = "Найдено QR-кодов: " + values.Length + " — выберите содержимое для импорта", Width = 540, Height = 360, Content = root, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        cancel.Click += delegate { window.DialogResult = false; };
        proceed.Click += delegate { if (list.SelectedItem is ListBoxItem item) { result = (string)item.Tag; window.DialogResult = true; } };
        list.MouseDoubleClick += delegate { if (list.SelectedItem is ListBoxItem item) { result = (string)item.Tag; window.DialogResult = true; } };
        list.KeyDown += (s, e) => { if (e.Key == Key.Escape) window.DialogResult = false; if (e.Key == Key.Enter && list.SelectedItem is ListBoxItem item) { result = (string)item.Tag; window.DialogResult = true; } };
        window.ShowDialog(); return result;
    }
}

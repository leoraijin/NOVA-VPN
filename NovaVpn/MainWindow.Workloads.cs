using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private LocalProxyManager workloadProxies;
    private ApplicationBandwidthWorkloads workloadBandwidth;
    private readonly Dictionary<string, string> workloadMessages = new Dictionary<string, string>();
    private readonly WorkloadReconciliationQueue workloadReconciler = new WorkloadReconciliationQueue();
    private bool workloadStopUnconfirmed;
    private bool WorkloadProfileInUse(string id) => state.LocalProxies.Any(p => p.ProfileId == id && workloadProxies.Snapshot(p.Id).Exists);

    private void InitializeWorkloadProxies()
    {
        workloadProxies = new LocalProxyManager(Path.Combine(StateStore.DirectoryPath, "runtime", "workload-proxies"));
        workloadBandwidth=new ApplicationBandwidthWorkloads(workloadProxies);
        workloadProxies.Changed += delegate {
            if (!closing) Dispatcher.BeginInvoke((Action)(() => { if (!closing) { ScheduleRetiredProfileCleanup(); if (currentPage == "workloads") Navigate("workloads", false); } }));
        };
    }

    private UIElement BuildWorkloadsPage()
    {
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        var add = PrimaryButton("Добавить прокси", 160); add.Click += delegate { EditWorkloadProxy(null); };
        var rulesButton=SmallButton("Лимиты приложений");rulesButton.Name="OpenBandwidthRules";rulesButton.ToolTip="Правила можно менять во время работы. Для входов, запущенных без лимитов, сначала нужна остановка.";rulesButton.Click+=delegate{EditBandwidthRules();};
        var headerActions=new WrapPanel();add.Margin=new Thickness(0,0,8,4);rulesButton.Margin=new Thickness(0,0,0,4);headerActions.Children.Add(add);headerActions.Children.Add(rulesButton);
        root.Children.Add(PageHeader("Локальные прокси", "Отдельные HTTP/SOCKS5-входы для браузеров, IDE и других задач", headerActions, null));
        var content = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        var hint = T("Настройте прокси вручную в нужном приложении. Каждый вход направляет трафик только через назначенный профиль. Запуск локального входа ещё не подтверждает доступность внешнего сервера. Доступ с других устройств закрыт.", 11, Muted);
        if(state.ApplicationLimitsEnabled)hint.Text+=" Сохранённые лимиты применяются только к SOCKS-входам NOVA. Основное VPN-подключение пока их не применяет.";
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(hint);
        foreach (var endpoint in state.LocalProxies)
        {
            var status = workloadProxies.Snapshot(endpoint.Id); var profile = state.Profiles.FirstOrDefault(p => p.Id == endpoint.ProfileId);
            var card = new StackPanel(); card.Children.Add(T(endpoint.Name ?? "Без названия", 14, TextColor, FontWeights.SemiBold));
            var address = T((endpoint.InboundType == "http" ? "HTTP" : endpoint.InboundType == "socks" ? "SOCKS5" : "HTTP + SOCKS5") + " · 127.0.0.1:" + endpoint.Port, 12, AccentLight);
            address.TextWrapping = TextWrapping.Wrap; address.Margin = new Thickness(0, 6, 0, 4); card.Children.Add(address);
            var assignment = T("Профиль: " + (profile?.Name ?? "не найден"), 11, Muted); assignment.TextWrapping = TextWrapping.Wrap; card.Children.Add(assignment);
            if(state.ApplicationLimitsEnabled||status.ApplicationLimits){bool enforcing=state.ApplicationLimitsEnabled&&status.ApplicationLimits;var limits=T(enforcing?"Лимиты приложений действуют для этого входа":"Лимиты приложений для этого входа не активны",11,enforcing?Green:Muted);limits.TextWrapping=TextWrapping.Wrap;card.Children.Add(limits);}
            string note = workloadMessages.TryGetValue(endpoint.Id ?? "", out string message) ? message : status.Status;
            var statusText = T(note, 10, status.StopRequested ? Warning : status.Running ? Green : Muted); statusText.TextWrapping = TextWrapping.Wrap; statusText.Margin = new Thickness(0, 6, 0, 10); card.Children.Add(statusText);
            var actions = new WrapPanel();
            Func<string, Action, Button> button = (label, click) => { var b = SmallButton(label); b.Margin = new Thickness(0, 0, 8, 6); b.Click += delegate { click(); }; actions.Children.Add(b); return b; };
            var start = button("Запустить", async () => {
                if (snapshotMode) return;
                try {
                    var current = state.Profiles.FirstOrDefault(p => p.Id == endpoint.ProfileId);
                    string error = ProfileCollectionService.Exclusion(state, current); if (error != null) throw new InvalidOperationException(error);
                    workloadMessages.Remove(endpoint.Id);
                    await workloadBandwidth.StartAsync(state,endpoint,current,core.ProbeCorePath,mapHealthCancellation.Token);
                    await ReconcileWorkloadsAsync();
                } catch (OperationCanceledException) { workloadMessages[endpoint.Id] = "Запуск отменён."; }
                catch (Exception ex) { workloadMessages[endpoint.Id] = LogSanitizer.Sanitize(ex.Message); }
                if (!closing && currentPage == "workloads") Navigate("workloads", false);
            });
            start.IsEnabled = !snapshotMode && !status.Exists && LocalProxyConfiguration.Validate(endpoint, profile) == null;
            var stop = button("Остановить", async () => {
                try { await workloadProxies.StopAsync(endpoint.Id); workloadMessages.Remove(endpoint.Id); }
                catch (Exception) { workloadMessages[endpoint.Id] = "Остановка не подтверждена. Повторите попытку."; }
                if (!closing && currentPage == "workloads") Navigate("workloads", false);
            }); stop.IsEnabled = status.Exists;
            var edit = button("Изменить", () => EditWorkloadProxy(endpoint)); edit.IsEnabled = !status.Exists;
            button("Адрес", () => CopyWorkloadValue((endpoint.InboundType == "socks" ? "socks5://" : "http://") + "127.0.0.1:" + endpoint.Port));
            button("Логин", () => CopyWorkloadValue(endpoint.Username)); button("Пароль", () => CopyWorkloadValue(endpoint.Password));
            var remove = button("Удалить", () => { state.LocalProxies.Remove(endpoint); workloadMessages.Remove(endpoint.Id); if (!snapshotMode) StateStore.Save(state); Navigate("workloads", false); }); remove.IsEnabled = !status.Exists;
            card.Children.Add(actions); content.Children.Add(new Border { Child = card, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(12) });
        }
        if (state.LocalProxies.Count == 0) content.Children.Add(EmptyState("routing", "У каждой задачи может быть свой выход", "Добавьте локальный вход, выберите профиль и укажите полученный адрес в приложении."));
        var scroll = TouchScrollViewer(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetRow(scroll, 1); root.Children.Add(scroll); return root;
    }

    private void EditBandwidthRules()
    {
        try{
            var opened=state;string baseline=ApplicationBandwidthPolicyEdit.Fingerprint(opened);
            var editor=new ApplicationBandwidthWindow(opened,state.Visual,snapshotMode){Owner=this};
            if(editor.ShowDialog()!=true||editor.Accepted==null||snapshotMode)return;
            if(!Object.ReferenceEquals(opened,state))throw new InvalidOperationException("Настройки изменились, пока редактор был открыт. Откройте его заново.");
            workloadBandwidth.SavePolicy(state,baseline,editor.Accepted,StateStore.Save,mapHealthCancellation.Token);
            if(!closing&&currentPage=="workloads")Navigate("workloads",false);
        }catch(OperationCanceledException){}catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}
    }

    private void CopyWorkloadValue(string value)
    {
        if (snapshotMode) return;
        try { Clipboard.SetText(value ?? ""); Toast("Скопировано.", Green); } catch (Exception) { Toast("Буфер обмена недоступен.", Warning); }
    }

    private void EditWorkloadProxy(LocalProxyEndpoint existing)
    {
        if (existing != null && workloadProxies.Snapshot(existing.Id).Exists) return;
        if (existing == null && state.LocalProxies.Count >= LocalProxyConfiguration.MaxRunning) { Toast("Настроено восемь входов. Удалите ненужный перед добавлением нового.", Warning); return; }
        var initial = existing ?? new LocalProxyEndpoint { ProfileId = CollectionCandidates().FirstOrDefault(p => p.Protocol != "sing-box")?.Id, InboundType=state.ApplicationLimitsEnabled?"socks":"mixed" };
        if (existing == null) { while (state.LocalProxies.Any(p => p.Port == initial.Port) && initial.Port < 65535) initial.Port++; }
        var editor = new LocalProxyEditorWindow(initial, CollectionCandidates(), state.Visual) { Owner = this };
        if (editor.ShowDialog() != true) return;
        if (state.LocalProxies.Any(p => p.Id != initial.Id && p.Port == editor.Accepted.Port)) { Toast("Этот порт уже назначен другому локальному входу.", Warning); return; }
        if (existing != null) state.LocalProxies.Remove(existing);
        state.LocalProxies.Add(editor.Accepted); workloadMessages.Remove(editor.Accepted.Id);
        if (!snapshotMode) StateStore.Save(state); Navigate("workloads", false);
    }

    private Task ReconcileWorkloadsAsync()
    {
        if (snapshotMode || closing || workloadProxies == null) return Task.CompletedTask;
        return workloadReconciler.RequestAsync(async () =>
        {
            if (closing || workloadProxies == null || !workloadProxies.HasEntries) { workloadStopUnconfirmed = false; ScheduleRetiredProfileCleanup(); return; }
            var policyStopped=await workloadBandwidth.ReconcileAsync(state);
            var stopped = await workloadProxies.StopInvalidProfilesAsync(state.Profiles, p => ProfileCollectionService.Exclusion(state, p) == null);
            foreach(var entry in policyStopped)if(!stopped.ContainsKey(entry.Key))stopped[entry.Key]=entry.Value;
            workloadStopUnconfirmed = stopped.Values.Any(stoppedSuccessfully => !stoppedSuccessfully);
            foreach (var result in stopped)
            {
                if(policyStopped.ContainsKey(result.Key)){workloadMessages[result.Key]=result.Value?"Остановлен: ограничения приложений изменились. Проверьте правила и запустите вход снова.":"Ограничения изменились, но остановка входа не подтверждена. Повторите остановку.";continue;}
                workloadMessages[result.Key] = result.Value ? "Остановлен: назначенный профиль или разрешения коллекции изменились. Проверьте назначение и запустите снова." : "Профиль изменился, но остановка прокси не подтверждена. Повторите остановку.";
            }
            ScheduleRetiredProfileCleanup();
            if (stopped.Count > 0 && !closing && currentPage == "workloads") Navigate("workloads", false);
        });
    }
}

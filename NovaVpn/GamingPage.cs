using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private GamingRoute gamingSession;
    private CancellationTokenSource gamingCancellation;
    private List<GamingProbeResult> gamingResults = new List<GamingProbeResult>();
    private GamingProbeResult gamingRecommended;
    private DateTime gamingMeasuredUtc;
    private string gamingNetwork, gamingMeasuredKey;
    private readonly Dictionary<string, VpnProfile> gamingCandidates = new Dictionary<string, VpnProfile>();
    private GameDefinition gamingGame = GameCatalog.Games[0];
    private string gamingExe = "", gamingHost = "", gamingPort = "443";
    private bool gamingA2S, gamingAllowDirect;
    private string gamingMessage = "Выберите игру и адрес цели. Подбор выполняется до запуска игры, при отключённом VPN.";
    private string GamingSelectionKey => gamingGame.Name + "|" + gamingExe + "|" + gamingHost + "|" + gamingPort + "|" + gamingA2S + "|" + gamingAllowDirect;

    private UIElement BuildGamingPage()
    {
        var body = new StackPanel();
        body.Children.Add(PageHeader("Игровой режим", "Выбор маршрута для игры · Windows · sing-box", null, null));
        void Text(string value, Color color) {
            var t = T(value, 12, color); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 8, 0, 8); body.Children.Add(t);
        }
        Text(gamingSession == null ? "Режим выключен" : "Маршрут закреплён: " + gamingSession.Game + " → " + (gamingSession.Profile?.Name ?? "напрямую"), gamingSession == null ? Muted : Green);
        Text("NOVA сравнивает только ваши профили. Меньший пинг не гарантирован. TCP-проверка не измеряет UDP-пинг матча; A2S оценивает ответ query-порта конкретного сервера.", Muted);
        bool editable = gamingCancellation == null && gamingSession == null;
        var game = new ComboBox { ItemsSource = GameCatalog.Games, SelectedItem = gamingGame, Foreground = Brushes.Black, Background = Brushes.White, MinHeight = 36, Margin = new Thickness(0, 8, 0, 8), IsEnabled = editable };
        game.SelectionChanged += delegate {
            gamingGame = (GameDefinition)game.SelectedItem;
            gamingExe = ""; gamingHost = ""; gamingPort = gamingGame.Name == "Rust" || gamingGame.Name == "Counter-Strike 2" ? "27015" : "443";
            gamingA2S = gamingGame.Name == "Rust" || gamingGame.Name == "Counter-Strike 2";
            InvalidateGamingResults(); Navigate("gaming");
        };
        body.Children.Add(game);
        string running = GameCatalog.IsRunning(gamingGame.Processes) ? "Запущена: завершите игру перед подбором или сменой маршрута." : "Игра не запущена по известным именам процессов.";
        Text(running, Muted);
        var executable = SmallButton(string.IsNullOrEmpty(gamingExe) ? "Выбрать EXE игры…" : Path.GetFileName(gamingExe) + " · изменить…");
        executable.IsEnabled = editable;
        executable.Click += delegate {
            var picker = new OpenFileDialog { Filter = "Приложение игры (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) { gamingExe = Path.GetFullPath(picker.FileName); InvalidateGamingResults(); Navigate("gaming"); }
        };
        body.Children.Add(executable);
        var discover = SmallButton("Найти установленную игру"); discover.IsEnabled = editable && !snapshotMode && gamingGame.Processes.Length > 0;
        discover.Click += async delegate {
            discover.IsEnabled = false;
            try {
                var selected = gamingGame;
                var known = await Task.Run(() => ApplicationCatalog.Discover(new string[0]));
                var match = known.FirstOrDefault(a => selected.Processes.Contains(Path.GetFileName(a.ExecutablePath), StringComparer.OrdinalIgnoreCase));
                if (gamingGame != selected || gamingSession != null || gamingCancellation != null || closing) return;
                if (match == null) gamingMessage = "Игра не найдена в установленном каталоге и ярлыках. Выберите EXE вручную.";
                else { gamingExe = match.ExecutablePath; InvalidateGamingResults(); gamingMessage = "Найдено: " + gamingExe; }
                Navigate("gaming");
            } catch (Exception ex) { Toast(LogSanitizer.Sanitize(ex.Message), Warning); discover.IsEnabled = true; }
        };
        body.Children.Add(discover);
        if (gamingGame.Name == "Fortnite") {
            Text("Официальные адреса Epic предназначены для регионального ICMP-ping и не являются готовой TCP/UDP-целью этого теста. Укажите доступную TCP-цель нужного региона. Это лишь оценка; пинг матча проверяется в игре.", Warning);
        }
        if (gamingGame.Name == "War Thunder") Text("Для War Thunder здесь нужен доступный тестовый адрес выбранного региона. Автоматическое определение сервера матча пока недоступно.", Warning);
        if (gamingGame.Name == "Rust") Text("Укажите IP сервера и его A2S query-порт из данных сервера. Query-порт может отличаться от порта подключения к игре.", Muted);
        Text("Адрес цели (IPv4 или домен, без https://)", Muted);
        var host = EditorBox(gamingHost, false); host.MinHeight = 34; host.AcceptsReturn = false; host.IsEnabled = editable;
        host.TextChanged += delegate { gamingHost = host.Text; InvalidateGamingResults(); }; body.Children.Add(host);
        Text("Порт цели", Muted);
        var port = EditorBox(gamingPort, false); port.MinHeight = 34; port.AcceptsReturn = false; port.IsEnabled = editable;
        port.TextChanged += delegate { gamingPort = port.Text; InvalidateGamingResults(); }; body.Children.Add(port);
        var method = new ComboBox { ItemsSource = new[] { "TCP CONNECT — предварительная оценка", "A2S UDP — ответ query-порта" }, Foreground = Brushes.Black, Background = Brushes.White, SelectedIndex = gamingA2S ? 1 : 0, MinHeight = 36, Margin = new Thickness(0, 12, 0, 8), IsEnabled = editable };
        method.SelectionChanged += delegate { gamingA2S = method.SelectedIndex == 1; InvalidateGamingResults(); }; body.Children.Add(method);
        var allow = new CheckBox { Content = "Разрешить прямые проверки и прямой маршрут, если он лучше VPN", IsChecked = gamingAllowDirect, IsEnabled = editable && !state.KillSwitch, Margin = new Thickness(0, 8, 0, 8) };
        allow.Checked += delegate { gamingAllowDirect = true; InvalidateGamingResults(); };
        allow.Unchecked += delegate { gamingAllowDirect = false; InvalidateGamingResults(); }; body.Children.Add(allow);
        if (state.KillSwitch) Text("Kill Switch включён: прямые проверки и прямой игровой маршрут запрещены.", Warning);
        Text("Кандидаты: до 8 обычных профилей; сначала избранные, затем по последней задержке до узла. Это предварительный отбор, а не поиск глобального минимума. Полные JSON-конфиги пропускаются. Проверка создаёт отдельные соединения к выбранным провайдерам.", Muted);
        var actions = new WrapPanel();
        var scan = SmallButton("Подобрать маршрут"); scan.IsEnabled = editable && !snapshotMode;
        scan.Click += async delegate { await ScanGamingAsync(); }; actions.Children.Add(scan);
        var cancel = SmallButton("Отменить проверку"); cancel.IsEnabled = gamingCancellation != null;
        cancel.Click += delegate { gamingCancellation?.Cancel(); }; actions.Children.Add(cancel);
        var apply = SmallButton("Включить игровой режим"); apply.IsEnabled = editable && gamingRecommended != null && !snapshotMode;
        apply.Click += async delegate { await ApplyGamingAsync(); }; actions.Children.Add(apply);
        var stop = SmallButton("Выключить режим"); stop.IsEnabled = gamingSession != null;
        stop.Click += async delegate { await StopGamingAsync(); }; actions.Children.Add(stop); body.Children.Add(actions);
        Text(gamingMessage, gamingRecommended != null ? Green : Muted);
        foreach (var result in gamingResults) Text((ReferenceEquals(result, gamingRecommended) ? "✓ " : "") + result.Name + " — " + result.Summary, result.Reliable ? TextColor : Muted);
        Text("При включении NOVA подключает TUN: игра использует выбранный выход, остальные приложения — обычные правила. Блокировки, правила сайтов и расширенные правила сохраняют приоритет. Изменение выхода выполняется до запуска игры. Маршрут не переключается по таймеру.", Muted);
        Text("После смены сети нужна новая проверка. Результат подбора действует 5 минут. При завершении NOVA временный режим сбрасывается; сохранённые правила приложений не переписываются.", Muted);
        Text("Перед прямым сравнением отключите также другие VPN и системные прокси. Базовый результат измеряется по текущему маршруту Windows; NOVA не может подтвердить отсутствие внешнего туннеля.", Muted);
        if (state.GamingHistory != null && state.GamingHistory.Count > 0) {
            Text("Последние подборы (локально)", TextColor);
            foreach (var entry in state.GamingHistory.Take(5)) Text(entry.Utc.ToLocalTime().ToString("g") + " · " + entry.Game + " · " + entry.Result, Muted);
        }
        return TouchScrollViewer(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

    private void InvalidateGamingResults() { gamingRecommended = null; gamingResults.Clear(); }
    private string[] GamingProcessNames() => string.IsNullOrEmpty(gamingExe) ? gamingGame.Processes : new[] { Path.GetFileName(gamingExe) };
    private void RequireGamingIdle()
    {
        if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting || startupConnecting || profileSwitchInProgress)
            throw new InvalidOperationException("Отключите VPN перед подбором или включением игрового режима.");
        if (core.Engine != VpnEngine.SingBox) throw new InvalidOperationException("Выберите sing-box в разделе VPN-ядро.");
        if (GameCatalog.IsRunning(GamingProcessNames())) throw new InvalidOperationException("Завершите игру перед изменением маршрута.");
        if (zapret.IsRunning) throw new InvalidOperationException("Остановите Zapret перед подбором: он может менять результаты прямых проверок.");
    }
    private async Task ScanGamingAsync()
    {
        if (gamingCancellation != null || gamingSession != null) return;
        try {
            RequireGamingIdle();
            if (gamingAllowDirect && state.KillSwitch) throw new InvalidOperationException("Прямой маршрут запрещён Kill Switch.");
            var target = GamingTarget.Parse(gamingHost, gamingPort, gamingA2S);
            if (GamingProcessNames().Length == 0) throw new InvalidOperationException("Выберите EXE игры перед подбором.");
            if (!File.Exists(core.ProbeCorePath)) throw new FileNotFoundException("Не найдено ядро sing-box.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            gamingCandidates.Clear();
            foreach (var p in state.Profiles.Where(p => p.Protocol != "sing-box")
                .OrderByDescending(p => p.IsFavorite).ThenBy(p => p.LastLatency < 0 ? int.MaxValue : p.LastLatency).Take(8))
                gamingCandidates[p.Id] = serializer.Deserialize<VpnProfile>(serializer.Serialize(p));
            if (gamingCandidates.Count == 0) throw new InvalidOperationException("Добавьте хотя бы один обычный VPN-профиль.");
            gamingNetwork = GameCatalog.NetworkKey(); gamingMeasuredKey = GamingSelectionKey;
            gamingCancellation = new CancellationTokenSource();
            var token = gamingCancellation.Token;
            InvalidateGamingResults(); gamingMessage = "Подбор запущен. Это может занять несколько минут."; Navigate("gaming");
            var progress = new Progress<string>(s => { gamingMessage = s; if (!closing && currentPage == "gaming") Navigate("gaming"); });
            gamingResults = await new GamingProbeService().ScanAsync(target, gamingCandidates.Values, core.ProbeCorePath,
                Path.Combine(StateStore.DirectoryPath, "runtime"), gamingAllowDirect, progress, token);
            token.ThrowIfCancellationRequested();
            if (gamingNetwork != GameCatalog.NetworkKey()) throw new InvalidOperationException("Сеть изменилась во время проверки. Повторите подбор.");
            gamingRecommended = GamingRouteSelector.Choose(gamingResults, gamingAllowDirect);
            gamingMeasuredUtc = DateTime.UtcNow;
            gamingMessage = gamingRecommended == null ? "Недостаточно данных для выбора. Проверьте цель, порт и метод. Преимущество VPN не подтверждено." :
                "Рекомендация: " + gamingRecommended.Name + " · " + target.Method + (gamingAllowDirect ? "" : ". Прямой путь не проверялся; улучшение относительно него неизвестно.");
            if (state.GamingHistory == null) state.GamingHistory = new List<GamingHistoryEntry>();
            state.GamingHistory.Insert(0, new GamingHistoryEntry { Game = gamingGame.Name, Method = target.Method, Result = gamingMessage, Utc = DateTime.UtcNow });
            state.GamingHistory = state.GamingHistory.Take(20).ToList(); if (!closing) StateStore.Save(state);
        } catch (OperationCanceledException) { InvalidateGamingResults(); gamingMessage = "Проверка отменена. Маршруты не изменены."; }
        catch (Exception ex) { InvalidateGamingResults(); gamingMessage = LogSanitizer.Sanitize(ex.Message); }
        finally { gamingCancellation?.Dispose(); gamingCancellation = null; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }

    private async Task ApplyGamingAsync()
    {
        try {
            RequireGamingIdle();
            if (gamingRecommended == null || gamingMeasuredKey != GamingSelectionKey || DateTime.UtcNow - gamingMeasuredUtc > TimeSpan.FromMinutes(5) || gamingNetwork != GameCatalog.NetworkKey())
                throw new InvalidOperationException("Результат устарел или параметры изменились. Повторите подбор.");
            if (gamingRecommended.IsDirect && (!gamingAllowDirect || state.KillSwitch)) throw new InvalidOperationException("Прямой маршрут запрещён.");
            if (GamingProcessNames().Length == 0) throw new InvalidOperationException("Выберите EXE игры.");
            var basis = SelectedProfile();
            if (basis == null || basis.Protocol == "sing-box") throw new InvalidOperationException("Выберите обычный профиль на странице Серверы для остальных приложений.");
            gamingSession = new GamingRoute { Game = gamingGame.Name, Processes = GamingProcessNames(), ExecutablePath = gamingExe,
                Profile = gamingRecommended.IsDirect ? null : gamingCandidates[gamingRecommended.ProfileId] };
            try { manualDisconnect = false; connectionRequested = true; await core.StartAsync(basis, EffectiveRouting()); }
            catch { gamingSession = null; connectionRequested = false; core.Stop(); throw; }
            gamingMessage = "Игровой режим включён. Можно запускать игру. Выход закреплён до выключения режима.";
        } catch (Exception ex) { gamingMessage = LogSanitizer.Sanitize(ex.Message); }
        if (!closing) Navigate("gaming");
    }

    private async Task StopGamingAsync()
    {
        if (gamingSession == null) return;
        if (GameCatalog.IsRunning(gamingSession.Processes)) { Toast("Завершите игру перед выключением игрового маршрута.", Warning); return; }
        bool restart = core.Status == CoreStatus.Connected;
        manualDisconnect = true; core.Stop(); gamingSession = null;
        try { if (restart) { manualDisconnect = false; await core.StartAsync(SelectedProfile(), EffectiveRouting()); } }
        catch (Exception ex) { Toast(LogSanitizer.Sanitize(ex.Message), Danger); }
        gamingMessage = "Игровой режим выключен. Используются обычные правила NOVA."; Navigate("gaming");
    }
}

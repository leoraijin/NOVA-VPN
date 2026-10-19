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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private GamingRoute gamingSession;
    private GamingSessionManager gamingSessions;
    private RoutingSettings gamingBaseRouting;
    private DispatcherTimer gamingSessionTimer;
    private DateTime gamingLastRunningUtc, gamingMeasuredUtc;
    private bool gamingTransitionBusy, gamingLibraryBusy, gamingLibraryLoaded;
    private List<InstalledGame> gamingLibrary = new List<InstalledGame>();
    private InstalledGame gamingSelected;
    private List<GameEndpoint> gamingEndpoints = new List<GameEndpoint>();
    private CancellationTokenSource gamingCancellation;
    private List<GamingProbeResult> gamingResults = new List<GamingProbeResult>();
    private GamingProbeResult gamingRecommended;
    private string gamingNetwork, gamingMeasuredKey, gamingSearch = "";
    private readonly Dictionary<string, VpnProfile> gamingCandidates = new Dictionary<string, VpnProfile>();
    private string gamingMessage = "Выберите игру: конфиг будет работать только для её игровых приложений.";
    private GamePreferences GamingOptions => GetGamePreferences(gamingSelected?.Id);
    private string GamingSelectionKey => gamingSelected?.Id + "|" + GamingOptions.Host + "|" + GamingOptions.Port + "|" + GamingOptions.A2S + "|" + GamingOptions.AllowDirect;
    private bool GamingEditable => gamingCancellation == null && gamingSession == null && !gamingTransitionBusy;

    private void InitializeGamingInfrastructure()
    {
        gamingSessions = new GamingSessionManager(snapshotMode ? Path.Combine(Path.GetTempPath(), "NovaSnapshot-" + Guid.NewGuid().ToString("N")) : StateStore.DirectoryPath);
        if (!snapshotMode && gamingSessions.Recover()) gamingMessage = "После аварийного завершения временный игровой маршрут сброшен. Используются обычные настройки NOVA.";
        state.ManualGames = state.ManualGames ?? new List<InstalledGame>(); state.GamePreferences = state.GamePreferences ?? new List<GamePreferences>();
        state.ManualGames.RemoveAll(g => g == null || string.IsNullOrEmpty(g.Id) || g.Executables == null);
        state.GamePreferences.RemoveAll(p => p == null || string.IsNullOrEmpty(p.GameId));
        gamingSessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        gamingSessionTimer.Tick += async delegate { await CheckGamingSessionAsync(); };
        if (snapshotMode) {
            foreach (string name in new[] { "Rust", "Fortnite", "War Thunder" }) gamingLibrary.Add(new InstalledGame { Id = name, Name = name, Source = name == "Fortnite" ? "Epic Games" : "Steam", InstallDirectory = @"D:\Games\" + name, Executables = new List<string> { @"D:\Games\" + name + @"\Game.exe" } });
            gamingLibraryLoaded = true;
        }
    }
    private GamePreferences GetGamePreferences(string id) {
        state.GamePreferences = state.GamePreferences ?? new List<GamePreferences>();
        var option = state.GamePreferences.FirstOrDefault(p => p.GameId == id);
        if (option == null) { option = new GamePreferences { GameId = id }; if (id != null) state.GamePreferences.Add(option); }
        return option;
    }
    private void SaveGameOptions() { if (!snapshotMode) StateStore.Save(state); }
    private void InvalidateGamingResults() { gamingRecommended = null; gamingResults.Clear(); }
    private void SelectGamingGame(InstalledGame game) {
        if (gamingCancellation != null || gamingTransitionBusy) return;
        gamingSelected = game; gamingEndpoints.Clear(); InvalidateGamingResults(); scrollPositions["gaming"] = 0; Navigate("gaming");
    }
    private TextBlock GameText(string text, double size, Color color) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size * state.Visual.TextScale, Foreground = new SolidColorBrush(color), Margin = new Thickness(0, 0, 0, 9) };
    private Border GameCard(UIElement child) {
        var card = new Border { Child = child, Padding = new Thickness(18), Margin = new Thickness(0, 0, 12, 12), CornerRadius = Radius(16), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1) };
        VisualDesign.ApplyGlassSurface(card, state.Visual); return card;
    }
    private UIElement BuildGamingPage()
    {
        if (!gamingLibraryLoaded && !gamingLibraryBusy && !snapshotMode) _ = RefreshGamingLibraryAsync();
        var body = new StackPanel(); var refresh = SmallButton(gamingLibraryBusy ? "Ищем игры…" : "Обновить библиотеку");
        refresh.IsEnabled = !gamingLibraryBusy && gamingCancellation == null && !snapshotMode;
        refresh.Click += async delegate { await RefreshGamingLibraryAsync(); };
        body.Children.Add(PageHeader("Игровой режим", "Библиотека игр · отдельный конфиг для выбранной игры", refresh, null));
        if (gamingSession != null) {
            var banner = new StackPanel(); banner.Children.Add(GameText("СЕССИЯ АКТИВНА · " + gamingSession.Game, 16, Green));
            banner.Children.Add(GameText("Выход: " + (gamingSession.Profile?.Name ?? "напрямую") + (gamingSession.GameOnly ? ". Остальные приложения напрямую." : ". Остальные приложения по обычным правилам."), 12, TextColor));
            banner.Children.Add(GameText(gamingSessions.GameWasSeen ? "Игра запущена, маршрут закреплён." : "Можно запускать игру. Ожидаем игровой процесс.", 12, Muted));
            var end = SmallButton("Завершить игровой режим"); end.IsEnabled = !gamingTransitionBusy; end.Click += async delegate { await StopGamingAsync(); }; banner.Children.Add(end); body.Children.Add(GameCard(banner));
        }
        body.Children.Add(GameText(gamingMessage, 12, gamingRecommended != null ? Green : Muted));
        if (gamingSelected != null) body.Children.Add(BuildGameDetails(gamingSelected));
        body.Children.Add(GameText("МОИ ИГРЫ · " + gamingLibrary.Count, 12, TextColor));
        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 12) };
        var add = SmallButton("Добавить игру…"); add.IsEnabled = gamingCancellation == null && !gamingTransitionBusy && !snapshotMode;
        add.Click += delegate {
            var picker = new OpenFileDialog { Filter = "Игровое приложение (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            try { var game = InstalledGame.FromExecutable(picker.FileName); state.ManualGames.RemoveAll(g => g.Id == game.Id); state.ManualGames.Add(game); gamingLibrary.RemoveAll(g => g.Id == game.Id); gamingLibrary.Add(game); SaveGameOptions(); SelectGamingGame(game); }
            catch (Exception ex) { Toast(LogSanitizer.Sanitize(ex.Message), Warning); }
        };
        DockPanel.SetDock(add, Dock.Right); tools.Children.Add(add);
        var search = EditorBox(gamingSearch, false); search.AcceptsReturn = false; search.MinHeight = 36; search.ToolTip = "Поиск по названию игры или лаунчеру"; search.Margin = new Thickness(0, 0, 12, 0); tools.Children.Add(search); body.Children.Add(tools);
        var cards = new WrapPanel();
        void Render() { cards.Children.Clear(); foreach (var game in gamingLibrary.Where(g => (g.Name + " " + g.Source).IndexOf(gamingSearch, StringComparison.CurrentCultureIgnoreCase) >= 0)) cards.Children.Add(BuildGameLibraryCard(game)); }
        search.TextChanged += delegate { gamingSearch = search.Text; Render(); }; Render(); body.Children.Add(cards);
        if (gamingLibrary.Count == 0) body.Children.Add(GameText(gamingLibraryBusy ? "Читаем библиотеки Steam, Epic Games и сведения Windows…" : "Игры не найдены. Добавьте EXE вручную или обновите библиотеку после установки игры.", 12, Muted));
        body.Children.Add(GameText("Обычные правила хранятся отдельно. Завершение сессии возвращает актуальные настройки. После перезапуска NOVA игровой маршрут автоматически не включается.", 11, Muted));
        return TouchScrollViewer(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
    private UIElement BuildGameLibraryCard(InstalledGame game)
    {
        var panel = new StackPanel(); ImageSource image = null;
        if (!snapshotMode && game.ImagePath != null && File.Exists(game.ImagePath)) {
            try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(game.ImagePath); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 280; bitmap.EndInit(); bitmap.Freeze(); image = bitmap; } catch { }
        }
        if (image == null && game.Executables.Count > 0) image = GetApplicationIcon(game.Executables[0]);
        if (image != null) panel.Children.Add(new Image { Source = image, Height = 72, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) });
        else panel.Children.Add(GameText(game.Name.Substring(0, Math.Min(2, game.Name.Length)).ToUpperInvariant(), 28, Accent));
        panel.Children.Add(GameText(game.Name, 17, TextColor)); panel.Children.Add(GameText(game.Source, 11, Muted));
        var pref = GetGamePreferences(game.Id); bool active = gamingSession?.GameId == game.Id;
        string assigned = pref.ProfileId == "direct" ? "Напрямую" : state.Profiles.FirstOrDefault(p => p.Id == pref.ProfileId)?.Name;
        panel.Children.Add(GameText(active ? "Игровой маршрут включён" : assigned == null ? "Конфиг не выбран" : "Сохранён: " + assigned, 11, active ? Green : Muted));
        var pick = SmallButton(active ? "Открыть сессию" : "Подобрать конфиг"); pick.IsEnabled = gamingCancellation == null && !gamingTransitionBusy;
        pick.Click += async delegate { SelectGamingGame(game); if (!active && gamingSession == null && !snapshotMode) { if (string.IsNullOrEmpty(GamingOptions.Host)) await DetectGamingTargetsAsync(); else await ScanGamingAsync(); } }; panel.Children.Add(pick);
        var details = SmallButton("Настроить игру"); details.IsEnabled = pick.IsEnabled; details.Click += delegate { SelectGamingGame(game); }; panel.Children.Add(details);
        var card = GameCard(panel); card.Width = responsiveMode == "Compact" ? 260 : 280; card.ToolTip = game.InstallDirectory; return card;
    }
    private UIElement BuildGameDetails(InstalledGame game)
    {
        var body = new StackPanel(); var pref = GetGamePreferences(game.Id); bool edit = GamingEditable;
        body.Children.Add(GameText(game.Name + " · " + game.Source, 17, TextColor)); body.Children.Add(GameText(game.Confidence, 11, Muted));
        var profiles = state.Profiles.Where(p => p.Protocol != "sing-box").ToArray(); var names = new List<string> { "Выберите конфиг для игры" }; names.AddRange(profiles.Select(p => p.Name));
        bool canDirect = pref.AllowDirect && !state.KillSwitch; if (canDirect) names.Add("Напрямую — без VPN");
        int index = Array.FindIndex(profiles, p => p.Id == pref.ProfileId) + 1; if (pref.ProfileId == "direct" && canDirect) index = names.Count - 1;
        var choice = new ComboBox { ItemsSource = names, SelectedIndex = index, MinHeight = 36, Foreground = Brushes.Black, Background = Brushes.White, IsEnabled = edit, Margin = new Thickness(0, 0, 0, 10) };
        choice.SelectionChanged += delegate { int n = choice.SelectedIndex; pref.ProfileId = n > 0 && n <= profiles.Length ? profiles[n - 1].Id : canDirect && n == names.Count - 1 ? "direct" : null; gamingRecommended = null; SaveGameOptions(); Navigate("gaming"); }; body.Children.Add(choice);
        var actions = new WrapPanel();
        var scan = SmallButton("Подобрать конфиг"); scan.IsEnabled = edit && !snapshotMode; scan.Click += async delegate { if (string.IsNullOrEmpty(pref.Host)) await DetectGamingTargetsAsync(); else await ScanGamingAsync(); }; actions.Children.Add(scan);
        var apply = SmallButton("Включить только для игры"); apply.IsEnabled = edit && !snapshotMode && pref.ProfileId != null; apply.Click += async delegate { await ApplyGamingAsync(); }; actions.Children.Add(apply);
        var cancel = SmallButton("Отменить"); cancel.IsEnabled = gamingCancellation != null; cancel.Click += delegate { gamingCancellation?.Cancel(); }; actions.Children.Add(cancel); body.Children.Add(actions);
        var direct = new CheckBox { Content = "Разрешить прямое сравнение и выход без VPN", IsChecked = pref.AllowDirect, IsEnabled = edit && !state.KillSwitch, Margin = new Thickness(0, 10, 0, 10) };
        direct.Checked += delegate { pref.AllowDirect = true; InvalidateGamingResults(); SaveGameOptions(); Navigate("gaming"); }; direct.Unchecked += delegate { pref.AllowDirect = false; if (pref.ProfileId == "direct") pref.ProfileId = null; InvalidateGamingResults(); SaveGameOptions(); Navigate("gaming"); }; body.Children.Add(direct);
        foreach (var result in gamingResults) body.Children.Add(GameText((ReferenceEquals(result, gamingRecommended) ? "✓ " : "") + result.Name + " — " + result.Summary, 11, result.Reliable ? TextColor : Muted));
        foreach (var endpoint in gamingEndpoints) { var e = endpoint; var button = SmallButton(e.Label); button.IsEnabled = edit; button.Click += delegate { UseGamingEndpoint(e); Navigate("gaming"); }; body.Children.Add(button); }
        var advanced = new StackPanel(); advanced.Children.Add(GameText("Игровые EXE: " + string.Join(", ", game.Executables.Select(Path.GetFileName)), 11, Muted));
        var executable = SmallButton("Выбрать игровой EXE…"); executable.IsEnabled = edit && !snapshotMode;
        executable.Click += delegate {
            var picker = new OpenFileDialog { Filter = "Игровое приложение (*.exe)|*.exe", CheckFileExists = true, InitialDirectory = game.InstallDirectory };
            if (picker.ShowDialog(this) == true) { game.Executables = new List<string> { Path.GetFullPath(picker.FileName) }; state.ManualGames.RemoveAll(g => g.Id == game.Id); state.ManualGames.Add(game); InvalidateGamingResults(); SaveGameOptions(); Navigate("gaming"); }
        }; advanced.Children.Add(executable);
        var detect = SmallButton("Определить сервер автоматически"); detect.IsEnabled = edit && !snapshotMode; detect.Click += async delegate { await DetectGamingTargetsAsync(); }; advanced.Children.Add(detect);
        advanced.Children.Add(GameText("Для определения запустите игру при подключённом обычном VPN NOVA. Соединения лаунчера не учитываются. Для применения нового выхода завершите игру.", 11, Muted));
        advanced.Children.Add(GameText("Цель проверки: адрес и порт", 11, Muted));
        var host = EditorBox(pref.Host ?? "", false); host.AcceptsReturn = false; host.MinHeight = 34; host.IsEnabled = edit; host.TextChanged += delegate { pref.Host = host.Text.Trim(); InvalidateGamingResults(); }; advanced.Children.Add(host);
        var port = EditorBox(pref.Port.ToString(), false); port.AcceptsReturn = false; port.MinHeight = 34; port.IsEnabled = edit; port.TextChanged += delegate { pref.Port = int.TryParse(port.Text, out int n) ? n : 0; InvalidateGamingResults(); }; advanced.Children.Add(port);
        var method = new ComboBox { ItemsSource = new[] { "TCP — предварительная оценка", "A2S UDP — ответ query-порта" }, SelectedIndex = pref.A2S ? 1 : 0, MinHeight = 34, Foreground = Brushes.Black, Background = Brushes.White, IsEnabled = edit };
        method.SelectionChanged += delegate { pref.A2S = method.SelectedIndex == 1; InvalidateGamingResults(); }; advanced.Children.Add(method);
        advanced.Children.Add(GameText("TCP не измеряет игровой UDP-пинг. A2S требует совместимого query-порта. Если точная проба протокола недоступна, назначьте конфиг вручную; снижение пинга не гарантируется.", 11, Muted));
        var onExit = new CheckBox { Content = "Завершать режим после выхода из игры", IsChecked = pref.StopWhenGameExits, IsEnabled = edit, Margin = new Thickness(0, 10, 0, 10) };
        onExit.Checked += delegate { pref.StopWhenGameExits = true; SaveGameOptions(); }; onExit.Unchecked += delegate { pref.StopWhenGameExits = false; SaveGameOptions(); }; advanced.Children.Add(onExit);
        var onFailure = new CheckBox { Content = "При обрыве завершать режим и возвращать обычные правила", IsChecked = pref.EndOnDisconnect, IsEnabled = edit, Margin = new Thickness(0, 0, 0, 10) };
        onFailure.Checked += delegate { pref.EndOnDisconnect = true; SaveGameOptions(); }; onFailure.Unchecked += delegate { pref.EndOnDisconnect = false; SaveGameOptions(); }; advanced.Children.Add(onFailure);
        advanced.Children.Add(GameText("При снятом флажке сохраняется игровой выход и действует обычная настройка автопереподключения. Возврат обычных правил может изменить IP и разорвать матч.", 11, Muted));
        body.Children.Add(new Expander { Header = "Сервер, приложения и поведение сессии", Content = advanced, Margin = new Thickness(0, 10, 0, 0) }); return GameCard(body);
    }
    private async Task RefreshGamingLibraryAsync()
    {
        if (gamingLibraryBusy || snapshotMode) return; gamingLibraryBusy = true;
        try {
            var manual = state.ManualGames.ToArray();
            var library = await Task.Run(() => GameDiscoveryService.Discover(manual));
            var icons = await Task.Run(() => library.Where(g => g.Executables.Count > 0).Take(200).Select(g => g.Executables[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, ExtractApplicationIcon, StringComparer.OrdinalIgnoreCase));
            if (closing) return;
            foreach (var icon in icons) applicationIconCache[icon.Key] = icon.Value;
            gamingLibrary = library; if (gamingSelected != null) gamingSelected = library.FirstOrDefault(g => g.Id == gamingSelected.Id);
            gamingLibraryLoaded = true; gamingMessage = "Найдено игр: " + library.Count + ". Выберите карточку для подбора или назначения конфига.";
        } catch (Exception ex) { gamingLibraryLoaded = true; gamingMessage = "Не все библиотеки доступны: " + LogSanitizer.Sanitize(ex.Message); }
        finally { gamingLibraryBusy = false; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }
    private static bool SupportsGameA2S(InstalledGame game) => game.Name.IndexOf("Rust", StringComparison.OrdinalIgnoreCase) >= 0 || game.Name.IndexOf("Counter-Strike", StringComparison.OrdinalIgnoreCase) >= 0;
    private void UseGamingEndpoint(GameEndpoint endpoint) {
        if (endpoint.Network == "udp" && !SupportsGameA2S(gamingSelected)) { gamingMessage = "Для обнаруженного UDP-протокола пока нет измерительной пробы. Можно назначить конфиг вручную."; return; }
        GamingOptions.Host = endpoint.Address; GamingOptions.Port = endpoint.Port; GamingOptions.A2S = endpoint.Network == "udp"; InvalidateGamingResults(); SaveGameOptions();
    }
    private async Task DetectGamingTargetsAsync()
    {
        if (!GamingEditable || gamingSelected == null) return;
        try {
            if (gamingSelected.Executables.Count == 0) throw new InvalidOperationException("Выберите игровой EXE в подробностях карточки.");
            if (core.Status != CoreStatus.Connected) throw new InvalidOperationException("Для первого определения сервера подключите обычный VPN NOVA, запустите игру и повторите определение. Либо назначьте конфиг вручную.");
            gamingCancellation = new CancellationTokenSource(); var token = gamingCancellation.Token; var game = gamingSelected; var endpoints = new Dictionary<string, GameEndpoint>();
            for (int i = 0; i < 5; i++) {
                foreach (var endpoint in await core.GameEndpointsAsync(game, token)) endpoints[endpoint.Network + endpoint.Address + ":" + endpoint.Port] = endpoint;
                gamingMessage = "Определяем соединения «" + game.Name + "»… " + (i + 1) + "/5"; if (currentPage == "gaming") Navigate("gaming"); await Task.Delay(800, token);
            }
            gamingEndpoints = endpoints.Values.OrderByDescending(e => e.Network == "udp").ThenByDescending(e => e.Bytes).Take(12).ToList();
            var candidate = gamingEndpoints.FirstOrDefault(e => e.Network == "udp" && SupportsGameA2S(game)) ?? gamingEndpoints.FirstOrDefault(e => e.Network == "tcp");
            if (candidate != null) { UseGamingEndpoint(candidate); gamingMessage = "Цель обнаружена. Нажмите «Подобрать конфиг». " + (candidate.Network == "tcp" ? "TCP — предварительная оценка; это может быть служебное соединение игры." : "Этот порт будет проверен на поддержку A2S query."); }
            else gamingMessage = gamingEndpoints.Count == 0 ? "Соединения игровых EXE не найдены. Войдите на сервер и повторите определение." : "Обнаружен игровой UDP, но его точная проба недоступна. Назначьте конфиг вручную либо задайте подходящую цель в подробностях.";
        } catch (OperationCanceledException) { gamingMessage = "Определение отменено."; }
        catch (Exception ex) { gamingMessage = LogSanitizer.Sanitize(ex.Message); }
        finally { gamingCancellation?.Dispose(); gamingCancellation = null; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }
    private async Task ScanGamingAsync()
    {
        if (!GamingEditable || gamingSelected == null) return;
        try {
            if (core.Status == CoreStatus.Connecting || startupConnecting || profileSwitchInProgress) throw new InvalidOperationException("Дождитесь завершения подключения.");
            if (gamingSelected.Executables.Count == 0) throw new InvalidOperationException("Выберите игровой EXE.");
            var pref = GamingOptions;
            if (pref.AllowDirect && (state.KillSwitch || core.Status == CoreStatus.Connected)) throw new InvalidOperationException(state.KillSwitch ? "Прямой путь запрещён Kill Switch." : "Для прямого сравнения отключите обычный VPN. Без прямого сравнения можно проверять профили при работающем VPN.");
            if (zapret.IsRunning) throw new InvalidOperationException("Остановите Zapret перед сравнением.");
            var target = GamingTarget.Parse(pref.Host, pref.Port.ToString(), pref.A2S);
            if (!File.Exists(core.ProbeCorePath)) throw new FileNotFoundException("Не найдено ядро sing-box.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; gamingCandidates.Clear();
            foreach (var p in state.Profiles.Where(p => p.Protocol != "sing-box").OrderByDescending(p => p.IsFavorite).ThenBy(p => p.LastLatency < 0 ? int.MaxValue : p.LastLatency).Take(8)) gamingCandidates[p.Id] = serializer.Deserialize<VpnProfile>(serializer.Serialize(p));
            if (gamingCandidates.Count == 0) throw new InvalidOperationException("Добавьте обычные VPN-профили для сравнения.");
            SaveGameOptions(); gamingNetwork = GameCatalog.NetworkKey(); gamingMeasuredKey = GamingSelectionKey; gamingCancellation = new CancellationTokenSource(); var token = gamingCancellation.Token; InvalidateGamingResults();
            var progress = new Progress<string>(s => { gamingMessage = s; if (!closing && currentPage == "gaming") Navigate("gaming"); });
            gamingResults = await new GamingProbeService().ScanAsync(target, gamingCandidates.Values, core.ProbeCorePath, Path.Combine(StateStore.DirectoryPath, "runtime"), pref.AllowDirect, progress, token);
            token.ThrowIfCancellationRequested(); if (gamingNetwork != GameCatalog.NetworkKey()) throw new InvalidOperationException("Сеть изменилась. Повторите подбор.");
            gamingRecommended = GamingRouteSelector.Choose(gamingResults, pref.AllowDirect); gamingMeasuredUtc = DateTime.UtcNow;
            if (gamingRecommended != null) pref.ProfileId = gamingRecommended.IsDirect ? "direct" : gamingRecommended.ProfileId;
            gamingMessage = gamingRecommended == null ? "Недостаточно ответов для рекомендации. Проверьте цель; конфиг можно назначить вручную." : "Рекомендован: " + gamingRecommended.Name + ". " + target.Method + (pref.AllowDirect ? "" : ". Прямой путь не сравнивался.");
            state.GamingHistory = state.GamingHistory ?? new List<GamingHistoryEntry>(); state.GamingHistory.Insert(0, new GamingHistoryEntry { Game = gamingSelected.Name, Method = target.Method, Result = gamingMessage, Utc = DateTime.UtcNow }); state.GamingHistory = state.GamingHistory.Take(20).ToList(); if (!closing) SaveGameOptions();
        } catch (OperationCanceledException) { InvalidateGamingResults(); gamingMessage = "Проверка отменена, маршруты не изменялись."; }
        catch (Exception ex) { InvalidateGamingResults(); gamingMessage = LogSanitizer.Sanitize(ex.Message); }
        finally { gamingCancellation?.Dispose(); gamingCancellation = null; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }
    private async Task ApplyGamingAsync()
    {
        if (!GamingEditable || gamingSelected == null) return; gamingTransitionBusy = true;
        try {
            if (core.Engine != VpnEngine.SingBox) throw new InvalidOperationException("Игровой режим работает с ядром sing-box.");
            if (core.Status == CoreStatus.Connecting || startupConnecting || profileSwitchInProgress) throw new InvalidOperationException("Дождитесь завершения операции.");
            if (gamingSelected.IsRunning) throw new InvalidOperationException("Завершите игру перед сменой выхода, чтобы не разорвать активный матч.");
            var paths = gamingSelected.Executables.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (paths.Length == 0) throw new InvalidOperationException("Игровые EXE больше не найдены. Обновите библиотеку.");
            var pref = GamingOptions; var chosen = pref.ProfileId == "direct" ? null : state.Profiles.FirstOrDefault(p => p.Id == pref.ProfileId && p.Protocol != "sing-box");
            if (pref.ProfileId == null || (pref.ProfileId != "direct" && chosen == null)) throw new InvalidOperationException("Выберите конфиг для игры.");
            if (chosen == null && (!pref.AllowDirect || state.KillSwitch)) throw new InvalidOperationException("Прямой игровой выход запрещён.");
            if (gamingRecommended != null && (gamingMeasuredKey != GamingSelectionKey || DateTime.UtcNow - gamingMeasuredUtc > TimeSpan.FromMinutes(5) || gamingNetwork != GameCatalog.NetworkKey())) throw new InvalidOperationException("Рекомендация устарела. Повторите подбор либо выберите конфиг вручную.");
            bool connected = core.Status == CoreStatus.Connected; var basis = state.Profiles.FirstOrDefault(p => p.Id == core.ActiveProfileId) ?? SelectedProfile() ?? chosen;
            if (basis == null || basis.Protocol == "sing-box") throw new InvalidOperationException("Выберите обычный профиль NOVA.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var route = new GamingRoute { GameId = gamingSelected.Id, Game = gamingSelected.Name, ExecutablePaths = paths, Processes = paths.Select(Path.GetFileName).ToArray(), GameOnly = !connected, Profile = chosen == null ? null : serializer.Deserialize<VpnProfile>(serializer.Serialize(chosen)) };
            var frozen = RoutingOverlayService.Clone(connected ? core.LastConnectedRouting ?? state.Routing : state.Routing); frozen.Gaming = null; var effective = GamingRouting.Effective(frozen, route, state.KillSwitch);
            string error = await core.ValidateAsync(basis, effective); if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
            gamingSessions.Prepare(route.GameId, connected, basis.Id); gamingBaseRouting = frozen; gamingSession = route;
            try { manualDisconnect = true; reconnectTimer.Stop(); core.Stop(); manualDisconnect = false; connectionRequested = true; await core.StartAsync(basis, effective); gamingSessions.Activate(); gamingLastRunningUtc = DateTime.UtcNow; gamingSessionTimer.Start(); }
            catch { gamingSession = null; gamingBaseRouting = null; core.Stop(); if (connected) { manualDisconnect = false; connectionRequested = true; await core.StartAsync(basis, GamingRouting.Effective(state.Routing, null, state.KillSwitch)); } else { manualDisconnect = true; connectionRequested = false; } gamingSessions.Finish(); throw; }
            gamingMessage = "Маршрут включён только для «" + route.Game + "». После завершения сессии вернутся актуальные обычные правила."; SaveGameOptions();
        } catch (Exception ex) { gamingMessage = LogSanitizer.Sanitize(ex.Message); }
        finally { gamingTransitionBusy = false; if (!closing) Navigate("gaming"); }
    }
    private async Task StopGamingAsync(string reason = "Игровой режим выключен.")
    {
        if (gamingSession == null || gamingTransitionBusy) return; gamingTransitionBusy = true; gamingSessionTimer.Stop();
        bool restore = gamingSessions.BaseWasConnected; string profileId = gamingSessions.BaseProfileId;
        gamingSessions.Restoring();
        try {
            manualDisconnect = true; connectionRequested = false; reconnectTimer.Stop(); core.Stop();
            gamingSession = null; gamingBaseRouting = null;
            if (restore) { var profile = state.Profiles.FirstOrDefault(p => p.Id == profileId) ?? SelectedProfile(); if (profile == null) throw new InvalidOperationException("Обычный профиль удалён. VPN отключён."); manualDisconnect = false; connectionRequested = true; await core.StartAsync(profile, GamingRouting.Effective(state.Routing, null, state.KillSwitch)); }
            gamingSessions.Finish(); applicationRoutingPending = false; gamingMessage = reason + (restore ? " Обычный VPN и актуальные правила восстановлены." : " Восстановлено обычное подключение без VPN.");
        } catch (Exception ex) {
            if (gamingSession != null) { gamingSessions.AbortRestore(); gamingSessionTimer.Start(); gamingMessage = "Не удалось остановить ядро, игровой маршрут ещё активен. Повторите завершение. " + LogSanitizer.Sanitize(ex.Message); }
            else {
                gamingSessions.Finish(); manualDisconnect = !restore; connectionRequested = restore;
                gamingMessage = "Игровой маршрут снят. " + LogSanitizer.Sanitize(ex.Message);
                if (restore && state.AutoReconnect) ScheduleReconnect();
            }
        }
        finally { gamingTransitionBusy = false; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }
    private async Task CheckGamingSessionAsync()
    {
        if (gamingSession == null || gamingTransitionBusy || closing) return; var route = gamingSession; var pref = GetGamePreferences(route.GameId);
        if (route.Profile != null && !state.Profiles.Any(p => p.Id == route.Profile.Id)) { await StopGamingAsync("Игровой конфиг удалён."); return; }
        if (route.ExecutablePaths != null && !route.ExecutablePaths.Any(File.Exists)) { await StopGamingAsync("Файлы игры больше не найдены."); return; }
        var game = new InstalledGame { Executables = route.ExecutablePaths?.ToList() ?? new List<string>() }; bool running = await Task.Run(() => game.IsRunning);
        if (gamingSession != route || closing) return;
        if (running) { gamingSessions.GameWasSeen = true; gamingLastRunningUtc = DateTime.UtcNow; }
        if (pref.StopWhenGameExits && gamingSessions.GameWasSeen && !running && DateTime.UtcNow - gamingLastRunningUtc > TimeSpan.FromSeconds(6)) { await StopGamingAsync("Игра завершена."); return; }
        if (!gamingSessions.GameWasSeen && DateTime.UtcNow - gamingSessions.StartedUtc > TimeSpan.FromMinutes(15)) { await StopGamingAsync("Игра не была запущена в течение 15 минут."); return; }
        if (pref.EndOnDisconnect && core.Status != CoreStatus.Connected && core.Status != CoreStatus.Connecting) await StopGamingAsync("Игровое соединение прервано.");
    }
}

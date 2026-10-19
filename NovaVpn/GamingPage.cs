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
    private AutomaticGamingResult gamingAutomaticResult;
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
    private string gamingMessage = "Нажмите «Подобрать конфиг». При первом подборе NOVA запустит игру для определения сервера.";
    private GamePreferences GamingOptions => GetGamePreferences(gamingSelected?.Id);
    private string GamingSelectionKey => gamingSelected?.Id + "|" + GamingOptions.Host + "|" + GamingOptions.Port + "|" + GamingOptions.A2S + "|" + GamingOptions.AllowDirect;
    private bool GamingEditable => gamingCancellation == null && gamingSession == null && !gamingTransitionBusy;

    private void InitializeGamingInfrastructure()
    {
        gamingSessions = new GamingSessionManager(snapshotMode ? Path.Combine(Path.GetTempPath(), "NovaSnapshot-" + Guid.NewGuid().ToString("N")) : StateStore.DirectoryPath);
        if (!snapshotMode && gamingSessions.Recover()) gamingMessage = "После аварийного завершения временный игровой маршрут сброшен. Используются обычные настройки NOVA.";
        if (!snapshotMode) { try { GameUdpDiscovery.Recover(Path.Combine(StateStore.DirectoryPath, "runtime")); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
    private void InvalidateGamingResults() { gamingRecommended = null; gamingResults.Clear(); gamingAutomaticResult = null; }
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
        bool ready = gamingSelected?.Id == game.Id && gamingAutomaticResult?.ProfileId != null;
        bool fresh = ready && DateTime.UtcNow - gamingMeasuredUtc <= TimeSpan.FromMinutes(5) && gamingNetwork == GameCatalog.NetworkKey();
        if (ready) {
            panel.Children.Add(GameText(gamingAutomaticResult.Message, 12, Green));
            panel.Children.Add(GameText(gamingAutomaticResult.GameProtocol ? "Проверен ответ query-сервера" : "Предварительная оценка; игровой пинг не подтверждён", 11, Muted));
        }
        var pick = SmallButton(active ? "Открыть сессию" : fresh ? "Играть" : "Подобрать конфиг");
        pick.IsEnabled = gamingCancellation == null && !gamingTransitionBusy && (gamingSession == null || active);
        pick.Click += async delegate {
            if (active) { SelectGamingGame(game); return; }
            if (fresh) { await PlayGamingAsync(); return; }
            SelectGamingGame(game);
            if (!snapshotMode) await PickGamingAutomaticallyAsync();
        }; panel.Children.Add(pick);
        if (gamingSelected?.Id == game.Id && gamingCancellation != null) {
            var cancel = SmallButton("Отменить подбор"); cancel.Click += delegate { gamingCancellation?.Cancel(); }; panel.Children.Add(cancel);
        }
        var card = GameCard(panel); card.Width = responsiveMode == "Compact" ? 260 : 280; card.ToolTip = game.InstallDirectory; return card;
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
            gamingLibraryLoaded = true; gamingMessage = "Найдено игр: " + library.Count + ". Нажмите «Подобрать конфиг» на карточке игры.";
        } catch (Exception ex) { gamingLibraryLoaded = true; gamingMessage = "Не все библиотеки доступны: " + LogSanitizer.Sanitize(ex.Message); }
        finally { gamingLibraryBusy = false; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }
    private async Task PickGamingAutomaticallyAsync()
    {
        if (!GamingEditable || gamingSelected == null || snapshotMode) return;
        var game = gamingSelected;
        gamingCancellation = new CancellationTokenSource(); var token = gamingCancellation.Token;
        GameUdpDiscovery udpObserver = null;
        InvalidateGamingResults(); GamingOptions.ProfileId = null;
        try {
            if (!game.Executables.Any(File.Exists)) throw new InvalidOperationException("Файлы игры не найдены. Обновите библиотеку после установки игры.");
            if (!File.Exists(core.ProbeCorePath)) throw new InvalidOperationException("Не найден компонент проверки серверов. Переустановите NOVA.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var profiles = state.Profiles.Where(p => p.Protocol != "sing-box").Select(p => serializer.Deserialize<VpnProfile>(serializer.Serialize(p))).ToArray();
            if (profiles.Length == 0) throw new InvalidOperationException("Добавьте ваши серверы в NOVA — подбор будет использовать их.");
            gamingNetwork = GameCatalog.NetworkKey();
            var endpoints = new Dictionary<string, GameEndpoint>();
            var preference = GamingOptions;
            string networkHash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) networkHash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(gamingNetwork))).Replace("-", "");
            if (preference.ObservedNetwork == networkHash && DateTime.TryParse(preference.ObservedUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime observedUtc) && observedUtc <= DateTime.UtcNow && DateTime.UtcNow - observedUtc < TimeSpan.FromMinutes(30)) {
                foreach (var endpoint in preference.LastEndpoints ?? new List<SavedGameEndpoint>()) {
                    if (System.Net.IPAddress.TryParse(endpoint.Address, out var ip) && !System.Net.IPAddress.IsLoopback(ip) && endpoint.Port > 0 && endpoint.Port <= 65535 && (endpoint.Network == "tcp" || endpoint.Network == "udp"))
                        endpoints[endpoint.Network + endpoint.Address + ":" + endpoint.Port] = endpoint.ToEndpoint();
                }
            }
            bool usingHistory = endpoints.Count > 0;
            if (!usingHistory || game.IsRunning) udpObserver = new GameUdpDiscovery(game, Path.Combine(StateStore.DirectoryPath, "runtime"));
            if (!usingHistory && !game.IsRunning) {
                try { GameLaunchService.Start(game); }
                catch (Exception ex) { gamingMessage = LogSanitizer.Sanitize(ex.Message); }
            }
            for (int attempt = 0; attempt < 60; attempt++) {
                token.ThrowIfCancellationRequested();
                var observed = new List<GameEndpoint>();
                if (core.Status == CoreStatus.Connected && core.Engine == VpnEngine.SingBox) {
                    try { observed.AddRange(await core.GameEndpointsAsync(game, token)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* Read-only native fallback below; main connection is never restarted. */ }
                }
                observed.AddRange(await Task.Run(() => GameSocketDiscovery.Read(game), token));
                if (udpObserver != null) observed.AddRange(udpObserver.Snapshot());
                if (observed.Count > 0 && usingHistory) { endpoints.Clear(); usingHistory = false; }
                foreach (var endpoint in observed) endpoints[endpoint.Network + endpoint.Address + ":" + endpoint.Port] = endpoint;
                gamingMessage = endpoints.Count == 0 ? "Запустите «" + game.Name + "» и войдите на сервер. NOVA продолжит подбор автоматически…" : "Соединения игры найдены. Подбираем маршрут на ваших серверах…";
                if (!closing && currentPage == "gaming") Navigate("gaming");
                if (endpoints.Count > 0 && (usingHistory || attempt >= 2)) break;
                await Task.Delay(2000, token);
            }
            udpObserver?.Dispose(); udpObserver = null;
            if (!usingHistory && endpoints.Count > 0) {
                preference.LastEndpoints = endpoints.Values.OrderByDescending(e => e.Network == "udp").ThenByDescending(e => e.Bytes).Take(12).Select(e => new SavedGameEndpoint { Address = e.Address, Port = e.Port, Network = e.Network }).ToList();
                preference.ObservedNetwork = networkHash; preference.ObservedUtc = DateTime.UtcNow.ToString("O");
            }
            var progress = new Progress<string>(message => { gamingMessage = message; if (!closing && currentPage == "gaming") Navigate("gaming"); });
            var result = await new AutomaticGamingService().SelectAsync(game, endpoints.Values, profiles, core.Status == CoreStatus.Connected ? core.ActiveProfileId : null,
                core.ProbeCorePath, Path.Combine(StateStore.DirectoryPath, "runtime"), progress, token, core.Status == CoreStatus.Disconnected && !state.KillSwitch);
            token.ThrowIfCancellationRequested();
            if (gamingNetwork != GameCatalog.NetworkKey()) throw new InvalidOperationException("Сеть изменилась во время подбора. Нажмите «Подобрать конфиг» ещё раз.");
            gamingAutomaticResult = result; gamingMessage = result.Message;
            if (usingHistory && result.ProfileId != null) gamingMessage += " Проверен предыдущий сервер игры; новый матч может использовать другой сервер.";
            if (result.ProfileId != null) {
                GamingOptions.ProfileId = result.ProfileId; GamingOptions.AllowDirect = result.ProfileId == "direct";
                gamingMeasuredUtc = DateTime.UtcNow;
                gamingRecommended = result.Measurements.FirstOrDefault();
                gamingMeasuredKey = GamingSelectionKey;
            }
            state.GamingHistory = state.GamingHistory ?? new List<GamingHistoryEntry>();
            state.GamingHistory.Insert(0, new GamingHistoryEntry { Game = game.Name, Method = result.GameProtocol ? "Ответ игрового query-сервера" : "Предварительная проверка соединений игры", Result = result.Message, Utc = DateTime.UtcNow });
            state.GamingHistory = state.GamingHistory.Take(20).ToList(); SaveGameOptions();
        } catch (OperationCanceledException) { InvalidateGamingResults(); gamingMessage = "Подбор отменён. Обычное подключение сохранено."; }
        catch (Exception ex) { InvalidateGamingResults(); gamingMessage = LogSanitizer.Sanitize(ex.Message); }
        finally { udpObserver?.Dispose(); gamingCancellation?.Dispose(); gamingCancellation = null; if (!closing && currentPage == "gaming") Navigate("gaming"); }
    }
    private async Task PlayGamingAsync()
    {
        if (gamingAutomaticResult?.ProfileId == null || !GamingEditable) return;
        if (DateTime.UtcNow - gamingMeasuredUtc > TimeSpan.FromMinutes(5) || gamingNetwork != GameCatalog.NetworkKey()) { await PickGamingAutomaticallyAsync(); return; }
        var game = gamingSelected;
        if (game.IsRunning) { gamingMessage = "Маршрут готов. Завершите текущий сеанс игры и нажмите «Играть»: NOVA не меняет выход во время матча."; Navigate("gaming"); return; }
        bool unchanged = gamingAutomaticResult.ProfileId == "direct" && core.Status == CoreStatus.Disconnected && !state.KillSwitch;
        if (!unchanged) {
            await ApplyGamingAsync();
            if (gamingSession?.GameId != game.Id) return;
        }
        try {
            GameLaunchService.Start(game);
        } catch (Exception ex) { await StopGamingAsync("Не удалось запустить игру. " + LogSanitizer.Sanitize(ex.Message)); }
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
            if (gamingRecommended != null && (gamingMeasuredKey != GamingSelectionKey || DateTime.UtcNow - gamingMeasuredUtc > TimeSpan.FromMinutes(5) || gamingNetwork != GameCatalog.NetworkKey())) throw new InvalidOperationException("Результат устарел. Нажмите «Подобрать конфиг» ещё раз.");
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

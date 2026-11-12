using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace NovaVpn;

public sealed partial class MainWindow : Window
{
	private sealed class NavItem
	{
		public Button Button;

		public Border Indicator;

		public Viewbox Icon;

		public TextBlock Label;
	}


	private Color Bg;

	private Color Sidebar;

	private Color Surface;

	private Color Surface2;

	private Color BorderColor;

	private Color TextColor;

	private Color Muted;

	private Color Accent;

	private Color AccentLight;

	private Color Teal;

	private Color Green;

	private Color Danger;

	private Color Warning;

	private ThemePalette palette;

	private readonly AppState state;

	private readonly CoreManager core;

	private readonly ZapretManager zapret = new ZapretManager();

	private bool appUpdateInProgress;
	private bool applicationRoutingPending;
	private List<ApplicationEntry> applicationCatalog;
	private readonly Dictionary<string, ImageSource> applicationIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private Grid contentHost;

	private Grid shellRoot;

	private readonly Dictionary<string, double> scrollPositions = new Dictionary<string, double>();

	private readonly Dictionary<string, NavItem> navItems = new Dictionary<string, NavItem>();

	private string currentPage = "home";

	private string responsiveMode = "Wide";

	private bool responsiveBuildQueued;


	private TextBlock statusText;
    private long coreStatusNotificationRevision;

	private Border statusDot;

	private Button connectButton;
	private ConnectionControl connectionControl;
	private Button homePauseButton;
	private Button homeBestButton;
	private TextBlock liveExternalIpText;

	private TextBlock connectButtonLabel;

	private TextBlock connectHeadline;
	private Panel protectionIndicators;

	private TextBlock connectCaption;

	private bool closing;

	private bool manualDisconnect;

	private bool profileSwitchInProgress;

	private int reconnectAttempt;

	private bool connectionRequested;

	private readonly System.Threading.SemaphoreSlim trafficModeGate = new System.Threading.SemaphoreSlim(1, 1);

	private readonly DispatcherTimer reconnectTimer;

	private readonly DispatcherTimer subscriptionTimer;

	private readonly DispatcherTimer visualTimer;
	private readonly DispatcherTimer mapHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
	private readonly System.Threading.CancellationTokenSource mapHealthCancellation = new System.Threading.CancellationTokenSource();
	private readonly Dictionary<string, ServerMapHealth> mapHealth = new Dictionary<string, ServerMapHealth>();
	private InteractiveServerMap liveServerMap, expandedServerMap;
	private Window serverMapWindow;
	private bool mapHealthBusy;
	private int mapHealthCursor;

	private readonly List<double> downloadSamples = new List<double>();

	private readonly List<double> uploadSamples = new List<double>();

	private long lastReceivedBytes;

	private long lastSentBytes;

	private DateTime lastTrafficSampleUtc;

	private TextBlock liveDownloadText;

	private TextBlock liveUploadText;

	private TextBlock liveSessionText;

	private Polyline downloadLine;

	private Polyline uploadLine;

	private Canvas trafficCanvas;
	private TextBlock trafficModeStatusText;

	private string directExternalIp = "—";

	private string tunnelExternalIp = "—";

	private Forms.NotifyIcon trayIcon;



	private HwndSource hotkeySource;

	private const int HotkeyId = 0x4E56;

	private const int WmHotkey = 0x0312;

	private DateTime currentSessionStartedUtc;

	private bool snapshotMode;
	public bool WindowsStartupLaunch { get; set; }
	private readonly System.Threading.CancellationTokenSource startupCancellation = new System.Threading.CancellationTokenSource();
	private bool startupSettingsBusy, startupConnecting, startupRecoveryFailed;
    private string startupRecoveryError;

	public MainWindow(bool snapshot = false)
	{
		snapshotMode = snapshot;
		state = snapshotMode ? StateStore.CreateEphemeral() : StateStore.Load();
        if (!snapshotMode)
        {
            try { WindowsStartupService.Recover(Assembly.GetExecutingAssembly().Location, StartupPreferences()); }
            catch (Exception error) { startupRecoveryFailed = true; startupRecoveryError = "Не удалось восстановить автозапуск: " + LogSanitizer.Sanitize(error.Message); }
        }
		state.Visual.MigrateWindowsDesign();
		if (snapshotMode) PrepareSnapshotData();
		ApplyVisualPalette();
		core = new CoreManager(state.CoreEcoMode);
        InitializeWorkloadProxies();
        core.SelectEngine(state.EngineSelection == 1 ? VpnEngine.Reality : VpnEngine.SingBox);
        InitializeGamingInfrastructure();
        if (!snapshotMode) CleanupRetiredProfiles();
		zapret.StrategyProgress += message => Dispatcher.BeginInvoke((Action)(() =>
		{
			if (!closing && currentPage == "home") RefreshCurrentPage();
		}));
		if (!snapshotMode && state.Zapret != null && state.Zapret.StandardDomains.Count == 0 && zapret.IsInstalled)
		{
			state.Zapret.StandardDomains = ZapretDomainService.ReadStandardDomains(zapret.CurrentPath);
		}
		if (!snapshotMode)
		{
			ZapretRoutingTransitionService.SetActive(state, RoutingOverlayService.IsZapretMode(state.Zapret?.Mode));
			StateStore.Save(state);
		}
		core.RuntimeStatusChanged += CoreStatusChanged;
		core.VerificationChanged += delegate(ConnectionVerification verification)
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				if (closing || !ReferenceEquals(core.Verification, verification)) return;
                var currentProfile = state.Profiles.FirstOrDefault(p => p.Id == verification.ProfileId);
				if (!snapshotMode && !multiServerApplyBusy && core.Status==CoreStatus.Connected && core.ActiveProfileId==currentProfile?.Id && verification.IsVerifiedFor(currentProfile,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(core.CorePath),DateTime.UtcNow) && state.LastWorkingProfileId != verification.ProfileId) {
					state.LastWorkingProfileId = verification.ProfileId; StateStore.Save(state);
				}
				if (verification.Completed && verification.ProfileProbe?.Supported == true && !string.IsNullOrEmpty(verification.ProfileId)) {
					var health = verification.ProfileProbe;
                    if (currentProfile != null && health.IsFreshFor(currentProfile))
                    {
					    if (!verification.InternetOk) { health.Success = false; health.Message = "Туннель включён, но контрольный HTTPS-запрос не прошёл"; }
					    mapHealth[verification.ProfileId] = health;
					    liveServerMap?.RefreshStatus(); expandedServerMap?.RefreshStatus();
                    }
				}
                if(verification.Completed&&verification.MultiExits!=null) {
                    var group=verification.MultiExits;
                    if(!string.IsNullOrEmpty(group.NetworkKey)&&!string.IsNullOrEmpty(group.CoreKey)&&group.NetworkKey==ProfileHealthEvidence.NetworkKey()&&group.CoreKey==ProfileHealthEvidence.CoreKey(core.CorePath)) {
                        foreach(var exit in group.Results) {
                            var current=state.Profiles.FirstOrDefault(p=>p.Id==exit.Exit.ProfileId);
                            if(current==null||current.RetiredFromSubscription||ExitProbeTarget.Fingerprint(current)!=exit.Exit.ProfileFingerprint||
                                DateTime.UtcNow<exit.CheckedUtc||DateTime.UtcNow-exit.CheckedUtc>=TimeSpan.FromMinutes(3))continue;
                            mapHealth[current.Id]=new ServerMapHealth{Success=exit.Phase==ExitProbePhase.Verified,
                                Supported=exit.Phase==ExitProbePhase.Verified||exit.Phase==ExitProbePhase.Failed,LatencyMs=exit.LatencyMs,CheckedUtc=exit.CheckedUtc,
                                ProfileKey=SubscriptionManager.ConfigKey(current),NetworkKey=group.NetworkKey,CoreKey=group.CoreKey,Target=exit.Target??"",
                                Message=exit.Phase==ExitProbePhase.Verified?"HTTPS через назначенный выход подтверждён":"HTTPS через назначенный выход не подтверждён"};
                        }
                        liveServerMap?.RefreshStatus();expandedServerMap?.RefreshStatus();
                    }
                }
				if (currentPage == "home") UpdateVerificationDisplay(); else UpdateSidebarConnectionStatus();
			});
		};
		core.UnexpectedExit += ScheduleReconnect;
		reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
		reconnectTimer.Tick += async delegate { await TryAutomaticReconnectAsync(); };

		subscriptionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30.0) };
		subscriptionTimer.Tick += async delegate
		{
			subscriptionTimer.Stop();
			await UpdateSubscriptionsIfDueAsync(true);
			SyncSubscriptionTimer();
		};
		if (!snapshotMode)
		{
			NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
		}
		visualTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		visualTimer.Tick += delegate { UpdateLiveVisuals(); };
		mapHealthTimer.Tick += async delegate { await RefreshMapHealthAsync(); };
        InitializeConnectionMonitor();
		if (!snapshotMode) base.Loaded += async delegate { mapHealthTimer.Start(); await RefreshMapHealthAsync(); };
		base.Title = "NOVA VPN";
		// Explicit window icon is also used when Windows pins a running WPF window.
		// Do not depend on the shell extracting it from a custom-chrome window.
		base.Icon = BitmapDecoder.Create(new Uri("pack://application:,,,/" + typeof(MainWindow).Assembly.GetName().Name + ";component/Assets/nova-desktop-v3.ico", UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(frame => frame.PixelWidth).First();
		base.Width = Math.Min(1180.0, SystemParameters.WorkArea.Width);
		base.Height = Math.Min(760.0, SystemParameters.WorkArea.Height);
		base.MinWidth = Math.Min(680.0, SystemParameters.WorkArea.Width);
		base.MinHeight = Math.Min(460.0, SystemParameters.WorkArea.Height);
		responsiveMode = ResponsiveModeForWidth(base.Width);
		base.SizeChanged += MainWindowSizeChanged;
		UseLayoutRounding = true;
		base.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.CanResize;
		base.Background = new SolidColorBrush(Bg);
		base.Foreground = new SolidColorBrush(TextColor);
		base.FontFamily = new FontFamily("Segoe UI");
		WindowChrome.SetWindowChrome(this, new WindowChrome
		{
			CaptionHeight = 0.0,
			CornerRadius = new CornerRadius(0.0),
			GlassFrameThickness = new Thickness(0.0),
			ResizeBorderThickness = new Thickness(7.0),
			UseAeroCaptionButtons = false
		});
		BuildShell();
        WindowsWindowBounds.Attach(this);
        SourceInitialized += delegate { RefreshWindowMaterial(); };
        Activated += delegate { RefreshWindowMaterial(); };
        Deactivated += delegate { RefreshWindowMaterial(); };
		if (!snapshotMode)
		{
			InitializeTray();
			base.SourceInitialized += delegate { RegisterGlobalHotkey(); };
		}
		Navigate("home");
        if (startupRecoveryError != null) Dispatcher.BeginInvoke((Action)(() => Toast(startupRecoveryError, Warning)));
		RoutedEventHandler value = async delegate
		{
            ScheduleRetiredProfileCleanup();
			await UpdateSubscriptionsIfDueAsync(false);
			SyncSubscriptionTimer();
			if (RoutingOverlayService.IsZapretMode(state.Zapret?.Mode) && !zapret.IsRunning)
			{
				try
				{
					if (zapret.IsInstalled)
					{
						await zapret.StartSelectedAsync(state.Zapret);
						if (!zapret.IsRunning) throw new InvalidOperationException("Zapret не подтвердил запуск.");
						StateStore.Save(state);
					}
					else
					{
						ZapretRoutingTransitionService.SetActive(state, false);
						state.Zapret.Mode = "VPN";
						StateStore.Save(state);
					}
				}
				catch (Exception ex)
				{
					ZapretRoutingTransitionService.SetActive(state, false);
					state.Zapret.Mode = "VPN";
					StateStore.Save(state);
					Toast("Zapret не запустился при восстановлении режима: " + LogSanitizer.Sanitize(ex.Message), Warning);
				}
			}
			if (await CheckApplicationUpdatesOnStartupAsync()) return;
			if (core.Status != CoreStatus.Connected) { directExternalIp = await ExternalIpService.GetAsync(); if (currentPage == "home") RefreshCurrentPage(); }
			if (!startupRecoveryFailed && !WindowsStartupLaunch && state.AutoConnect && HasConnectionSelection && state.PauseUntilUtc <= DateTime.UtcNow)
			{
				await ToggleConnectionAsync();
			}
			ShowReleaseNotesIfNeeded();
		};
		if (!snapshotMode)
		{
			base.Loaded += value;
		}
		base.Closing += delegate
		{
            gamingSessionTimer?.Stop();
            gamingCancellation?.Cancel();
            GamingProbeService.StopAll();
			startupCancellation.Cancel();
			closing = true;
            subscriptionRetirementTimer?.Stop();
            try { workloadProxies?.Dispose(); } catch (Exception) { RecordHistory("Локальные прокси", "Не все собственные процессы подтвердили остановку перед закрытием."); }
            workloadBandwidth?.Dispose();
            StopConnectionMonitor();
			reconnectTimer.Stop();
			subscriptionTimer.Stop();
			visualTimer.Stop();
			mapHealthTimer.Stop();
			mapHealthCancellation.Cancel();
			if (!snapshotMode)
			{
				NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
			}
			UnregisterGlobalHotkey();
			core.Dispose();
            gamingSessions?.Finish();
			if (trayIcon != null)
			{
				trayIcon.Visible = false;
				trayIcon.Dispose();
			}
			if (!snapshotMode)
			{
				try
				{
					zapret.StopAndVerify();
					ZapretRoutingTransitionService.SetActive(state, false);
					if (state.Zapret != null) state.Zapret.Mode = "VPN";
				}
				catch { }
				StateStore.Save(state);
			}
		};
		base.StateChanged += delegate
		{
			SyncVisualTimerState();
			if (base.WindowState == WindowState.Minimized && state.MinimizeToTray)
			{
				Hide();
				base.ShowInTaskbar = false;
				ShowNotification("NOVA работает в области уведомлений.");
			}
		};
		base.IsVisibleChanged += delegate { SyncVisualTimerState(); };
	}

	public static string ResponsiveModeForWidth(double width)
	{
		if (double.IsNaN(width) || double.IsInfinity(width) || width < 800.0) return "Compact";
		if (width < 1080.0) return "Balanced";
		return "Wide";
	}

	private void MainWindowSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (WindowState == WindowState.Minimized || e.NewSize.Width < 320.0) return;
		string nextMode = ResponsiveModeForWidth(e.NewSize.Width);
		if (string.Equals(nextMode, responsiveMode, StringComparison.Ordinal) || responsiveBuildQueued || closing) return;
		responsiveMode = nextMode;
		responsiveBuildQueued = true;
		base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
		{
			responsiveBuildQueued = false;
			if (!closing) BuildShell();
		});
	}

    private System.Windows.Controls.Primitives.ToggleButton themeSwitch;
    private bool micaEnabled;
    private bool snapshotNativeMaterial;
    public void EnableNativeMaterialForSnapshotPreview()
    {
        if (!snapshotMode) throw new InvalidOperationException("Native material preview requires isolated snapshot state.");
        snapshotNativeMaterial = true;
        RefreshWindowMaterial();
    }
    private void RefreshWindowMaterial()
    {
        micaEnabled = WindowsBackdrop.Apply(this,state.Visual.DarkMode,(snapshotMode && !snapshotNativeMaterial) || state.Visual.HighContrast || !IsActive);
        Background = micaEnabled ? Brushes.Transparent : new SolidColorBrush(Bg);
        if (shellRoot != null) shellRoot.Background = micaEnabled ? Brushes.Transparent : new SolidColorBrush(Bg);
    }
    private UIElement BuildSidebar()
    {
        bool compact = responsiveMode == "Compact";
        var root = new Grid { Background = Brushes.Transparent, Margin = new Thickness(8,0,8,8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new TextBlock { Text = compact ? "N" : "NOVA VPN", FontSize = compact ? 20 : 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextColor), Margin = new Thickness(compact ? 15 : 16,18,8,24) };
        root.Children.Add(brand);
        var links = new StackPanel();
        links.Children.Add(Nav("home", "home", "Главная"));
        links.Children.Add(Nav("servers", "servers", "Серверы"));
        links.Children.Add(Nav("routing", "routing", "Маршруты"));
        links.Children.Add(Nav("diagnostics", "diagnostics", "Диагностика"));
        links.Children.Add(Nav("engine", "shield", "VPN-ядро"));
        links.Children.Add(Nav("workloads", "routing", "Локальные прокси"));
        Grid.SetRow(links,1);root.Children.Add(links);
        var footer = new StackPanel();
        footer.Children.Add(Nav("settings", "settings", "Настройки"));
        var theme = new System.Windows.Controls.Primitives.ToggleButton {
            Content = ThemeSwitchContent(compact), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(3),
            IsChecked = state.Visual.DarkMode, MinHeight = 36, Margin = new Thickness(4,8,4,8),
            Background = new SolidColorBrush(Surface), Foreground = new SolidColorBrush(TextColor),
            BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1),
            ToolTip = "Light / Dark — сменить тему", FontSize = 12
        };
        themeSwitch = theme;
        AutomationProperties.SetName(theme,"Тёмная тема — Light / Dark");
        theme.Click += delegate { ChangeColorMode(theme.IsChecked == true); };
        footer.Children.Add(theme);
        statusDot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Muted), Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
        statusText = T("Не подключено",11,Muted); statusText.VerticalAlignment = VerticalAlignment.Center;
        UpdateSidebarConnectionStatus();
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4,4,4,4) };
        status.Children.Add(statusDot); if (!compact) status.Children.Add(statusText);
        footer.Children.Add(status);Grid.SetRow(footer,2);root.Children.Add(footer);
        return root;
    }

    private UIElement ThemeSwitchContent(bool compact)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        if (!compact) row.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (compact ? 1 : 2); i++) {
            bool dark = compact ? state.Visual.DarkMode : i == 1;
            bool selected = dark == state.Visual.DarkMode;
            var label = new TextBlock { Text = dark ? "Dark" : "Light", FontSize = 12,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(selected ? Accent : Muted) };
            var segment = new Border { Child = label, CornerRadius = new CornerRadius(4),
                MinHeight = 28, Background = new SolidColorBrush(selected ? Surface2 : Surface),
                BorderBrush = new SolidColorBrush(selected ? BorderColor : Surface), BorderThickness = new Thickness(1) };
            Grid.SetColumn(segment,i); row.Children.Add(segment);
        }
        return row;
    }

    private void ChangeColorMode(bool dark)
    {
        if (state.Visual.DarkMode == dark) return;
        bool restoreThemeFocus = themeSwitch?.IsKeyboardFocusWithin == true;
        var inputs = Descendants<TextBox>(contentHost).Select(x => new { x.Text, x.CaretIndex, x.SelectionStart, x.SelectionLength }).ToList();
        var scrolls = Descendants<ScrollViewer>(contentHost).Select(x => x.VerticalOffset).ToList();
        state.Visual.DarkMode = dark;
        if (!snapshotMode) StateStore.Save(state);
        BuildShell();
        var nextInputs = Descendants<TextBox>(contentHost).ToList();
        for (int i=0; i<Math.Min(inputs.Count,nextInputs.Count); i++) {
            if (nextInputs[i].IsReadOnly) continue;
            nextInputs[i].Text = inputs[i].Text; nextInputs[i].CaretIndex = inputs[i].CaretIndex;
            nextInputs[i].Select(inputs[i].SelectionStart,inputs[i].SelectionLength);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,(Action)delegate {
            var nextScrolls = Descendants<ScrollViewer>(contentHost).ToList();
            for (int i=0;i<Math.Min(scrolls.Count,nextScrolls.Count);i++) nextScrolls[i].ScrollToVerticalOffset(scrolls[i]);
            if (restoreThemeFocus) themeSwitch?.Focus();
        });
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child = VisualTreeHelper.GetChild(root,i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

	private UIElement BuildTitlebar()
	{
		Grid grid = new Grid();
		grid.Background = Brushes.Transparent;
		Grid grid2 = grid;
		grid2.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
		{
			if (e.ClickCount == 2)
			{
				ToggleMaximize();
			}
			else if (e.ButtonState == MouseButtonState.Pressed)
			{
				DragMove();
			}
		};
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(50.0)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(50.0)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(46.0)
		});
		TextBlock textBlock = T("NOVA VPN", 12.0, Muted);
		textBlock.VerticalAlignment = VerticalAlignment.Center;
		textBlock.Margin = new Thickness(34.0, 0.0, 0.0, 0.0);
		grid2.Children.Add(textBlock);
		Button button = WindowButton("—");
		button.Click += delegate
		{
			base.WindowState = WindowState.Minimized;
		};
		Grid.SetColumn(button, 1);
		grid2.Children.Add(button);
		Button button2 = WindowButton("□");
		button2.Click += delegate
		{
			ToggleMaximize();
		};
		Grid.SetColumn(button2, 2);
		grid2.Children.Add(button2);
		Button close = WindowButton("×");
		close.FontSize = 19.0;
		close.Click += delegate
		{
			Close();
		};
		close.MouseEnter += delegate
		{
			close.Background = new SolidColorBrush(C("#C83B4D"));
		};
		close.MouseLeave += delegate
		{
			close.Background = Brushes.Transparent;
		};
		Grid.SetColumn(close, 3);
		grid2.Children.Add(close);
		return grid2;
	}

	private Button WindowButton(string text)
	{
		// Caption controls must not inherit the glass material or its rim/shadow.
		FrameworkElementFactory face = new FrameworkElementFactory(typeof(Border));
		face.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
		FrameworkElementFactory icon = new FrameworkElementFactory(typeof(ContentPresenter));
		icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		face.AppendChild(icon);
		Button button = new Button
		{
			Content = text,
			Width = 50.0,
			Height = 50.0,
			Background = Brushes.Transparent,
			Foreground = new SolidColorBrush(TextColor),
			BorderThickness = new Thickness(0.0),
			FontSize = 14.0,
			Cursor = Cursors.Hand,
			Focusable = false,
			Style = new Style(typeof(Button)),
			Template = new ControlTemplate(typeof(Button)) { VisualTree = face }
		};
		button.MouseEnter += delegate
		{
			button.Background = new SolidColorBrush(Color.FromArgb(22, TextColor.R, TextColor.G, TextColor.B));
		};
		button.MouseLeave += delegate
		{
			button.Background = Brushes.Transparent;
		};
		EnableButtonMotion(button);
		return button;
	}

	private void ApplyVisualPalette()
	{
		palette = ThemePalette.Create(state.Visual);
		Bg = palette.Bg;
		Sidebar = palette.Sidebar;
		Surface = palette.Surface;
		Surface2 = palette.Surface2;
		BorderColor = state.Visual.HighContrast ? palette.Text : palette.Border;
		TextColor = palette.Text;
		Muted = palette.Muted;
		Accent = palette.Accent;
		AccentLight = palette.AccentLight;
		Teal = palette.Teal;
		Green = palette.Success;
		Danger = palette.Danger;
		Warning = palette.Warning;
	}

	private void BuildShell()
	{
		ApplyVisualPalette();
		VisualResourceManager.Apply(Application.Current, palette, state.Visual);
		base.FontFamily = VisualDesign.InterfaceFont(state.Visual);
		base.Background = new SolidColorBrush(Bg);
		base.Foreground = new SolidColorBrush(TextColor);
		navItems.Clear();
		RefreshWindowMaterial();
        Grid outer = new Grid { Background = micaEnabled ? Brushes.Transparent : new SolidColorBrush(Bg) };
		UIElement backdrop = BuildBackdrop();
		if (backdrop != null) outer.Children.Add(backdrop);
		Grid app = new Grid();
		double sidebarWidth = responsiveMode == "Compact" ? 72.0 : responsiveMode == "Balanced" ? 202.0 : state.Visual.Density == "Compact" ? 205.0 : state.Visual.Density == "Comfortable" ? 244.0 : 228.0;
		if (palette.IsGlass) sidebarWidth += 20;
        if (palette.Name == ThemePalette.MaterialYou && responsiveMode != "Compact") sidebarWidth = 200;
		app.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sidebarWidth) });
		app.ColumnDefinitions.Add(new ColumnDefinition());
		app.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50.0) });
		app.RowDefinitions.Add(new RowDefinition());
		UIElement sidebar = BuildSidebar();
		if (palette.IsGlass)
		{
			((Grid)sidebar).Background = Brushes.Transparent;
			sidebar = new Border { Background = new SolidColorBrush(Sidebar), CornerRadius = Radius(22), BorderBrush = new SolidColorBrush(Color.FromArgb(170,255,255,255)), BorderThickness = new Thickness(1), Margin = new Thickness(10), Child = sidebar };
			VisualDesign.ApplyGlassSurface((Border)sidebar, state.Visual, true);
		}
		Grid.SetRowSpan(sidebar, 2); app.Children.Add(sidebar);
		UIElement titlebar = BuildTitlebar(); Grid.SetColumn(titlebar, 1); app.Children.Add(titlebar);
		double margin = responsiveMode == "Compact" ? 14.0 : responsiveMode == "Balanced" ? 22.0 : state.Visual.Density == "Compact" ? 24.0 : state.Visual.Density == "Comfortable" ? 40.0 : 34.0;
		contentHost = new Grid { Margin = new Thickness(margin, responsiveMode == "Compact" ? 12.0 : 20.0, margin, responsiveMode == "Compact" ? 16.0 : 28.0) };
		Grid.SetColumn(contentHost, 1); Grid.SetRow(contentHost, 1); app.Children.Add(contentHost);
		outer.Children.Add(app);
		tourPageRoot = app;
		app.IsHitTestVisible = !tourActive;
		KeyboardNavigation.SetTabNavigation(app, tourActive ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
		outer.SizeChanged += delegate { QueueTourRender(); };
		shellRoot = outer;
		base.Content = outer;
		Navigate(currentPage);
	}

	private UIElement BuildBackdrop() => null;

	private ScrollViewer TouchScrollViewer(ScrollViewer viewer)
	{
		ScrollViewer.SetPanningMode(viewer, PanningMode.VerticalOnly);
		SmoothScrollBehavior.Attach(viewer, () => MotionAllowed);
		return viewer;
	}

	private UIElement Nav(string key, string icon, string label)
	{
		bool compact = responsiveMode == "Compact";
		Button button = new Button();
		button.MinHeight = Math.Max(40, 36 * state.Visual.TextScale);
		button.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		button.Background = Brushes.Transparent;
		button.BorderThickness = new Thickness(0.0);
		button.Cursor = Cursors.Hand;
		button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
		button.Template = VisualDesign.ButtonTemplate(state.Visual, 24, false);
		button.ToolTip = label;
		AutomationProperties.SetName(button, label);
		Button button2 = button;
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(4.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(47.0) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = compact ? new GridLength(0.0) : new GridLength(1.0, GridUnitType.Star) });
		Border border = new Border();
		border.Width = palette.Name == ThemePalette.Windows ? 3.0 : 0.0;
		border.Height = 22.0;
		border.CornerRadius = new CornerRadius(2.0);
		border.Background = Brushes.Transparent;
		Border border2 = border;
		grid.Children.Add(border2);
		Viewbox textBlock = IconFactory.Create(icon, 19.0, Muted);
		textBlock.HorizontalAlignment = HorizontalAlignment.Center;
		textBlock.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(textBlock, 1);
		grid.Children.Add(textBlock);
		TextBlock textBlock2 = T(label, 12.5, Muted, FontWeights.SemiBold);
        textBlock2.TextTrimming = TextTrimming.None; textBlock2.TextWrapping = TextWrapping.Wrap; textBlock2.Margin = new Thickness(0,8,8,8);
		textBlock2.VerticalAlignment = VerticalAlignment.Center;
		if (compact) textBlock2.Visibility = Visibility.Collapsed;
		Grid.SetColumn(textBlock2, 2);
		grid.Children.Add(textBlock2);
		button2.Content = grid;
		button2.Click += delegate
		{
			if (key == "tour") StartGuidedTour(); else Navigate(key);
		};
		navItems[key] = new NavItem
		{
			Button = button2,
			Indicator = border2,
			Icon = textBlock,
			Label = textBlock2
		};
		EnableButtonMotion(button2);
		return button2;
	}

	private void Navigate(string page, bool animate = true)
	{
		if (page == "gaming") page = "home";
		if (page == "learning") { StartGuidedTour(); return; }
		tourAnchors.Clear();
		bool pageChanged = page != currentPage;
		ScrollViewer previousScroll = FindVisualChild<ScrollViewer>(contentHost);
		if (previousScroll != null) scrollPositions[currentPage] = previousScroll.VerticalOffset;
		currentPage = page;
		foreach (KeyValuePair<string, NavItem> navItem in navItems)
		{
			bool flag = navItem.Key == page;
			navItem.Value.Button.Background = (flag ? new SolidColorBrush(C("#1B1D31")) : Brushes.Transparent);
			navItem.Value.Indicator.Background = (flag ? new SolidColorBrush(Accent) : Brushes.Transparent);
			if (flag && animate && pageChanged && MotionAllowed && !state.Visual.ReduceMotion)
			{
				ScaleTransform selectedScale = new ScaleTransform(1, 0.2); navItem.Value.Indicator.RenderTransformOrigin = new Point(0.5, 0.5); navItem.Value.Indicator.RenderTransform = selectedScale;
				BeginMotionAnimation(selectedScale, ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(AnimMs(180))) { EasingFunction = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut } });
			}
			System.Windows.Shapes.Path iconPath = navItem.Value.Icon.Child as System.Windows.Shapes.Path;
			if (iconPath != null) iconPath.Stroke = new SolidColorBrush(flag ? AccentLight : Muted);
			navItem.Value.Label.Foreground = new SolidColorBrush(flag ? TextColor : Muted);
		}
		UIElement element = page switch
		{
			"servers" => BuildServersPage(),
			"routing" => BuildRoutingPage(),
			"diagnostics" => BuildDiagnosticsPage(),
			"settings" => BuildSettingsPage(),
            "engine" => BuildEnginePage(),
            "workloads" => BuildWorkloadsPage(),
			_ => BuildHomePage(),
		};
		// Build the replacement before detaching the visible page. Background
		// status updates must never restart a full-page opacity animation.
		contentHost.Children.Clear();
		contentHost.Children.Add(element);
		if (animate && pageChanged) AnimatePage(element);
		SyncVisualTimerState();
		SyncSubscriptionTimer();
		if (!snapshotMode && scrollPositions.TryGetValue(page, out double savedOffset))
		{
			base.Dispatcher.BeginInvoke((Action)delegate { ScrollViewer next = FindVisualChild<ScrollViewer>(contentHost); next?.ScrollToVerticalOffset(savedOffset); }, DispatcherPriority.Loaded);
		}
		QueueTourRender();
	}

	public void ShowPageForSnapshot(string page)
	{
		Navigate(string.IsNullOrWhiteSpace(page) ? "home" : page);
	}


	private UIElement BuildHomePage()
	{
		Grid grid = new Grid();
		grid.VerticalAlignment = VerticalAlignment.Top;
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(18.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(18.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(18.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		Button pauseButton = SmallButton("Пауза 15 минут");
        pauseButton.IsEnabled = gamingSession == null;
		homePauseButton = pauseButton;
		pauseButton.Visibility = core.Status == CoreStatus.Connected ? Visibility.Visible : Visibility.Collapsed;
		pauseButton.Click += delegate
		{
			if (core.Status == CoreStatus.Connected)
			{
				manualDisconnect = true; connectionRequested = false; core.Stop();
				try { StopZapretForPause(); }
				catch (Exception ex) { Toast("Не удалось полностью остановить Zapret: " + LogSanitizer.Sanitize(ex.Message), Warning); }
				state.PauseUntilUtc = DateTime.UtcNow.AddMinutes(15);
				RecordHistory("Пауза", "VPN приостановлен на 15 минут.");
				StateStore.Save(state);
				UpdateHomeConnectionStatus();
			}
		};
		WrapPanel headerActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
		if (pauseButton != null) { pauseButton.Margin = new Thickness(0, 0, 8, 0); headerActions.Children.Add(pauseButton); }
        homeBestButton = null;
        Grid element = PageHeader("Главная", "Подключение и состояние сети", headerActions, null);
		grid.Children.Add(element);
		Border border = new Border();
		border.Background = SoftSurface(true);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		border.CornerRadius = Radius(22.0);
		border.Padding = new Thickness((responsiveMode == "Wide" ? 30 : responsiveMode == "Balanced" ? 22 : 17) * VisualDesign.Density(state.Visual));
		Border border2 = border;
		Grid.SetRow(border2, 2);
		Grid grid2 = new Grid();
		if (responsiveMode == "Wide")
		{
			grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(palette.IsGlass ? 260.0 : 250.0) });
			grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		}
		else if (responsiveMode == "Balanced")
		{
			grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190.0) });
			grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		}
		else
		{
			grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		}
		StackPanel stackPanel = new StackPanel();
		stackPanel.VerticalAlignment = VerticalAlignment.Center;
		StackPanel stackPanel2 = stackPanel;
		stackPanel2.Children.Add(Label("ВЫБРАННЫЙ СЕРВЕР"));
		VpnProfile vpnProfile = SelectedProfile();
		Border border3 = new Border();
		border3.Margin = new Thickness(0.0, 12.0, 12.0, 0.0);
		border3.Padding = new Thickness(16.0);
		border3.CornerRadius = Radius(15.0);
		border3.Background = new SolidColorBrush(Surface2);
		border3.BorderBrush = new SolidColorBrush(BorderColor);
		border3.BorderThickness = new Thickness(1.0);
		border3.Cursor = Cursors.Hand;
		Border border4 = border3;
		border4.MouseLeftButtonUp += delegate
		{
			Navigate("servers");
		};
		Grid grid3 = new Grid();
		grid3.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(45.0)
		});
		grid3.ColumnDefinitions.Add(new ColumnDefinition());
		grid3.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(22.0)
		});
		Border element2 = ProtocolMark((vpnProfile == null) ? "?" : vpnProfile.ProtocolLabel);
		grid3.Children.Add(element2);
		StackPanel stackPanel3 = new StackPanel();
		stackPanel3.VerticalAlignment = VerticalAlignment.Center;
		StackPanel stackPanel4 = stackPanel3;
		stackPanel4.Children.Add(T(vpnProfile?.Name ?? (state.Routing?.MultiServer != null ? "Основной сервер недоступен" : "Добавить сервер"), 13.0, TextColor, FontWeights.SemiBold));
		stackPanel4.Children.Add(T(vpnProfile?.Endpoint ?? (state.Routing?.MultiServer != null ? "Другие назначения могут работать" : "Вставьте ключ или подписку"), 10.5, Muted));
		Grid.SetColumn(stackPanel4, 1);
		grid3.Children.Add(stackPanel4);
		TextBlock textBlock = T("›", 23.0, Muted);
		textBlock.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(textBlock, 2);
		grid3.Children.Add(textBlock);
		border4.Child = grid3;
		stackPanel2.Children.Add(border4);
		StackPanel stackPanel5 = new StackPanel();
		stackPanel5.Orientation = Orientation.Horizontal;
		stackPanel5.Margin = new Thickness(2.0, 16.0, 0.0, 0.0);
		StackPanel stackPanel6 = stackPanel5;
		stackPanel6.Children.Add(StatusMini("МАРШРУТ", RouteLabel()));
		stackPanel6.Children.Add(StatusMini("DNS", (state.Routing.DnsMode == "System") ? "Системный" : "Защищённый"));
		stackPanel6.Children.Add(StatusMini("СЕАНС", SessionLabel()));
		stackPanel2.Children.Add(stackPanel6);
		double connectionFrameSize = responsiveMode == "Wide" ? (palette.IsGlass ? 260.0 : 236.0) : responsiveMode == "Balanced" ? 190.0 : 180.0;
		connectionControl = new ConnectionControl();
		connectionControl.Apply(state.Visual, core.Status, MotionAllowed);
		connectButton = connectionControl.ActionButton;
		TourAnchor("home.connect", connectionControl);
		connectButtonLabel = connectionControl.Label;
        UpdatePausedConnectionLabel();
		connectButton.Click += async delegate { await ToggleConnectionAsync(); };
		Viewbox grid5 = new Viewbox { Width = connectionFrameSize, Height = connectionFrameSize, Child = connectionControl, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		StackPanel stackPanel9 = new StackPanel();
		stackPanel9.VerticalAlignment = VerticalAlignment.Center;
		stackPanel9.Margin = new Thickness(24.0, 0.0, 0.0, 0.0);
		StackPanel stackPanel10 = stackPanel9;
		TourAnchor("home.status", stackPanel10);
		stackPanel10.Children.Add(Label("СОСТОЯНИЕ"));
		connectHeadline = T(StatusHeadline(), responsiveMode == "Wide" ? 24.0 : 20.0, TextColor, FontWeights.SemiBold);
		connectHeadline.Margin = new Thickness(0.0, 11.0, 0.0, 5.0);
		stackPanel10.Children.Add(connectHeadline);
		connectCaption = T(StatusCaption(), palette.IsGlass ? 13.0 : 11.0, Muted);
		connectCaption.TextWrapping = TextWrapping.Wrap;
		connectCaption.MaxWidth = 230.0;
		connectCaption.HorizontalAlignment = HorizontalAlignment.Left;
		stackPanel10.Children.Add(connectCaption);
		WrapPanel protection = new WrapPanel { Margin = new Thickness(0.0, 12.0, 0.0, 0.0) };
		protectionIndicators = protection;
		UpdateVerificationDisplay();
		stackPanel10.Children.Add(protection);
		if (state.Visual.ShowConnectionMap) stackPanel10.Children.Add(BuildConnectionPath(vpnProfile));
		if (responsiveMode == "Wide")
		{
			grid2.Children.Add(stackPanel2);
			Grid.SetColumn(grid5, 1); grid2.Children.Add(grid5);
			Grid.SetColumn(stackPanel10, 2); grid2.Children.Add(stackPanel10);
		}
		else if (responsiveMode == "Balanced")
		{
			grid2.Children.Add(stackPanel2);
			Grid.SetColumn(grid5, 1); grid2.Children.Add(grid5);
			stackPanel10.Margin = new Thickness(4.0, 16.0, 4.0, 0.0);
			Grid.SetRow(stackPanel10, 1); Grid.SetColumnSpan(stackPanel10, 2); grid2.Children.Add(stackPanel10);
		}
		else
		{
			grid2.Children.Add(stackPanel2);
			Grid connectionSummary = new Grid { Margin = new Thickness(0, 16, 0, 0) };
			connectionSummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(connectionFrameSize) });
			connectionSummary.ColumnDefinitions.Add(new ColumnDefinition());
			connectionSummary.Children.Add(grid5);
			stackPanel10.Margin = new Thickness(16, 0, 0, 0);
			Grid.SetColumn(stackPanel10, 1); connectionSummary.Children.Add(stackPanel10);
			Grid.SetRow(connectionSummary, 1); grid2.Children.Add(connectionSummary);
		}
        if (palette.Name == ThemePalette.MaterialYou || palette.Name == ThemePalette.OneUi || palette.Name == ThemePalette.Windows || responsiveMode != "Wide") {
            // Move existing live controls into each design's desktop composition.
            grid2.Children.Clear();
            if (grid5.Parent is Panel oldPanel) oldPanel.Children.Remove(grid5);
            if (stackPanel10.Parent is Panel statusPanel) statusPanel.Children.Remove(stackPanel10);
            grid2.ColumnDefinitions.Clear(); grid2.RowDefinitions.Clear();
            grid2.ColumnDefinitions.Add(new ColumnDefinition());
            bool wide = responsiveMode == "Wide";
            grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wide ? 260 : 165) });
            for (int row = 0; row < 3; row++) grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(stackPanel2, 0); Grid.SetRow(stackPanel2, 2); Grid.SetColumnSpan(stackPanel2, 2);
            stackPanel2.Margin = new Thickness(0, 18, 0, 0);
            Grid.SetColumn(stackPanel10, 0); Grid.SetRow(stackPanel10, 0); Grid.SetColumnSpan(stackPanel10, 1);
            stackPanel10.Margin = new Thickness(0); connectCaption.MaxWidth = 520;
            Grid.SetColumn(grid5, 1); Grid.SetRow(grid5, 0);
            grid5.Width = 240; grid5.Height = palette.Name == ThemePalette.Windows ? 80 : 112;
            grid5.Stretch = Stretch.Uniform; connectionControl.Height = palette.Name == ThemePalette.Windows ? 72 : 102;
            if (palette.Name == ThemePalette.MaterialYou) {
                border2.CornerRadius = new CornerRadius(40,40,12,40);
                connectHeadline.FontSize = 40 * state.Visual.TextScale; connectHeadline.FontWeight = FontWeights.Bold;
                border2.Background = new SolidColorBrush(palette.SoftSelection);
            } else if (palette.Name == ThemePalette.OneUi) {
                border2.CornerRadius = new CornerRadius(28);
                connectHeadline.FontSize = 32 * state.Visual.TextScale;
                grid5.HorizontalAlignment = HorizontalAlignment.Right;
            } else {
                border2.CornerRadius = new CornerRadius(8);
                border4.CornerRadius = new CornerRadius(4);
                connectHeadline.FontSize = 26 * state.Visual.TextScale;
            }
            if (!wide) {
                grid5.Width = 160; grid5.Height = palette.IsGlass ? 160 : 92;
                grid5.VerticalAlignment = VerticalAlignment.Top;
                if (palette.IsGlass) connectionControl.Height = 220;
                connectHeadline.FontSize = 24 * state.Visual.TextScale;
                connectHeadline.TextWrapping = TextWrapping.Wrap;
                connectHeadline.TextTrimming = TextTrimming.None;
            }
            grid2.Children.Add(stackPanel10); grid2.Children.Add(grid5); grid2.Children.Add(stackPanel2);
        }
		border2.Child = grid2;
		VisualDesign.ApplyGlassSurface(border2, state.Visual, palette.IsGlass);
        if (palette.Name == ThemePalette.MaterialYou) border2.Background = new SolidColorBrush(palette.SoftSelection);
		grid.Children.Add(border2);
        grid.RowDefinitions.Insert(4, new RowDefinition { Height = GridLength.Auto });
        var dualVpn = BuildDualVpnControl();
        Grid.SetRow(dualVpn, 4);
        grid.Children.Add(dualVpn);
        Border zapretPanel = BuildZapretPanel(vpnProfile);
		Grid.SetRow(zapretPanel, 5);
		grid.Children.Add(zapretPanel);
		UIElement insights = BuildHomeInsights(vpnProfile);
		Grid.SetRow(insights, 7);
		grid.Children.Add(insights);
		return TouchScrollViewer(new ScrollViewer
		{
			Content = grid,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Padding = new Thickness(0, 0, 8, 0)
		});
	}

    private bool DualVpnEnabled => state.DualVpnSelectionEnabled || state.Routing?.MultiServer != null;

    private UIElement BuildDualVpnControl()
    {
        var assignments = state.Routing?.MultiServer;
        bool enabled = DualVpnEnabled;
        bool editable = CanEditServerAssignments();
        var content = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var title = T("Два VPN-конфига", 18, TextColor, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(title);
        var description = T(enabled ? "Включено · отдельные серверы для приложений и сайтов" :
            "Основной VPN для всего, VPN-2 — для выбранных приложений и сайтов", 12, Muted);
        description.TextWrapping = TextWrapping.Wrap;
        description.TextTrimming = TextTrimming.None;
        description.Margin = new Thickness(0, 5, 0, 0);
        labels.Children.Add(description);
        heading.Children.Add(labels);
        var toggle = ToggleButton(enabled);
        toggle.Name = "DualVpnToggle";
        toggle.IsEnabled = editable;
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Два VPN-конфига: " + (enabled ? "включено" : "выключено"));
        toggle.ToolTip = enabled ? "Выключить два конфига и вернуть прежнюю маршрутизацию" : "Разрешить выбор VPN-2";
        Grid.SetColumn(toggle, 1);
        heading.Children.Add(toggle);
        content.Children.Add(heading);
        if (enabled)
        {
            string ProfileName(string id) => id == null ? "не выбран" :
                state.Profiles.FirstOrDefault(p => p.Id == id && !p.RetiredFromSubscription)?.Name ?? "недоступен";
            var names = T("Основной VPN: " + ProfileName(assignments?.DefaultProfileId ?? SelectedProfile()?.Id) +
                "\nVPN-2: " + ProfileName(assignments?.SecondaryProfileId), 12, TextColor);
            names.Name = "DualVpnProfiles";
            names.TextWrapping = TextWrapping.Wrap;
            names.TextTrimming = TextTrimming.None;
            names.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(names);
        }
        var configure = SmallButton("Выбрать сервер");
        configure.Name = "ConfigureDualVpn";
        configure.IsEnabled = !closing;
        configure.HorizontalAlignment = HorizontalAlignment.Left;
        configure.Margin = new Thickness(0, 12, 0, 0);
        configure.Foreground = new SolidColorBrush(TextColor);
        content.Children.Add(configure);
        if (core.Engine != VpnEngine.SingBox)
        {
            var hint = T("Для двух конфигов выберите sing-box во вкладке «VPN-ядро».", 12, Muted);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 8, 0, 0);
            content.Children.Add(hint);
        }
        toggle.Click += async delegate
        {
            if (!CanEditServerAssignments()) return;
            toggle.IsEnabled = false;
            try
            {
                if (assignments != null) await ApplyServerAssignmentsAsync(state, null, true);
                else
                {
                    bool previous = state.DualVpnSelectionEnabled;
                    state.DualVpnSelectionEnabled = !previous;
                    try { StateStore.Save(state); }
                    catch { state.DualVpnSelectionEnabled = previous; throw; }
                }
            }
            catch (Exception error) { Toast(LogSanitizer.Sanitize(error.Message), Warning); }
            finally { if (!closing && currentPage == "home") RefreshCurrentPage(); }
        };
        configure.Click += delegate { Navigate("servers"); };
        var card = new Border
        {
            Name = "DualVpnCard", Background = new SolidColorBrush(palette.SoftSelection),
            BorderBrush = new SolidColorBrush(enabled ? Accent : BorderColor), BorderThickness = new Thickness(1),
            CornerRadius = Radius(16), Padding = new Thickness(responsiveMode == "Compact" ? 16 : 20),
            Margin = new Thickness(0, 0, 0, 18), Child = content
        };
        return TourAnchor("home.dual-vpn", card);
    }

    private async Task ChooseProfileRoleAsync(VpnProfile profile)
    {
        if (!DualVpnEnabled)
        {
            await SelectProfileAsync(profile);
            return;
        }
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Surface),
            Foreground = new SolidColorBrush(TextColor),
            BorderBrush = new SolidColorBrush(BorderColor),
            FontFamily = VisualDesign.InterfaceFont(state.Visual),
            FontSize = 14 * state.Visual.TextScale,
            Padding = new Thickness(8)
        };
        AddVpnRoleMenuItems(menu, profile);
        var choices = new StackPanel();
        var title = T(profile.Name, 18, TextColor, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness(0, 0, 0, 20);
        choices.Children.Add(title);
        var dialog = new Window
        {
            Title = "Использовать конфиг", Owner = serverMapWindow ?? this,
            Width = Math.Min(480, SystemParameters.WorkArea.Width),
            MaxHeight = SystemParameters.WorkArea.Height,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Bg), Foreground = new SolidColorBrush(TextColor),
            FontFamily = VisualDesign.InterfaceFont(state.Visual), ShowInTaskbar = false
        };
        MenuItem selected = null;
        foreach (MenuItem item in menu.Items)
        {
            var button = SmallButton(item.IsChecked ? item.Header + " · уже выбран" : item.Header.ToString());
            button.Foreground = new SolidColorBrush(TextColor);
            button.Height = double.NaN;
            button.MinHeight = 54 * state.Visual.TextScale;
            button.Padding = new Thickness(16, 12, 16, 12);
            button.Margin = new Thickness(0, 0, 0, 12);
            button.Content = new TextBlock { Text = button.Content.ToString(), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            button.IsEnabled = item.IsEnabled;
            button.Click += delegate { selected = item; dialog.DialogResult = true; };
            choices.Children.Add(button);
        }
        var cancel = SmallButton("Отмена");
        cancel.Foreground = new SolidColorBrush(TextColor);
        cancel.IsCancel = true;
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 4, 0, 0);
        cancel.Click += delegate { dialog.DialogResult = false; };
        choices.Children.Add(cancel);
        dialog.Content = new ScrollViewer { Content = choices, Padding = new Thickness(24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        dialog.SourceInitialized += delegate { WindowsBackdrop.Apply(dialog, state.Visual.DarkMode, true); };
        if (dialog.ShowDialog() == true && selected != null)
            selected.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private void AddVpnRoleMenuItems(ContextMenu menu, VpnProfile profile)
    {
        var assignments = state.Routing?.MultiServer;
        if (!DualVpnEnabled) return;
        var primary = SelectedProfile();
        string primaryId = assignments?.DefaultProfileId ?? primary?.Id;
        string secondaryId = assignments?.SecondaryProfileId;
        foreach (bool secondary in new[] { false, true })
        {
            bool current = profile.Id == (secondary ? secondaryId : primaryId);
            var item = new MenuItem
            {
                Header = secondary ? "Использовать как VPN-2" : "Использовать как основной VPN",
                IsCheckable = true,
                IsChecked = current,
                IsEnabled = !current && assignments?.RequiresRebinding != true && MultiServerRoutingWindow.Compatible(profile)
                    && (!secondary || assignments != null || (primary != null && MultiServerRoutingWindow.Compatible(primary)))
                    && profile.Id != (secondary ? primaryId : secondaryId)
            };
            item.Click += async delegate
            {
                try
                {
                    if (!CanEditServerAssignments() || !DualVpnEnabled) return;
                    var opened = state;
                    var candidate = RoutingOverlayService.Clone(opened.Routing);
                    var next = candidate.MultiServer;
                    var chosen = opened.Profiles.FirstOrDefault(p => p.Id == profile.Id);
                    if (next?.RequiresRebinding == true || chosen == null || !MultiServerRoutingWindow.Compatible(chosen))
                        throw new InvalidOperationException("Этот конфиг недоступен для назначения.");
                    if (next == null)
                    {
                        if (!secondary) { await SelectProfileAsync(chosen); return; }
                        var currentPrimary = SelectedProfile();
                        if (currentPrimary == null || !MultiServerRoutingWindow.Compatible(currentPrimary))
                            throw new InvalidOperationException("Сначала выберите совместимый основной VPN.");
                        var source = MultiServerRoutingHistory.AssignmentEditorSource(opened);
                        if (source.MigratedPolicy != null)
                            throw new InvalidOperationException("Преобразуйте перенесённую политику в разделе «Маршрутизация».");
                        next = new MultiServerSettings { DefaultProfileId = currentPrimary.Id,
                            Rules = MultiServerRoutingWindow.InitialRules(source) };
                        if (next.Rules.Count > 0 || source.Mode == "Direct" || source.DefaultRoute == "Direct")
                        {
                            if (MessageBox.Show(serverMapWindow ?? this,
                                "Применить два VPN? Трафик без отдельных правил пойдёт через основной VPN.",
                                "Два VPN-конфига", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                            if (!ReferenceEquals(opened, state) || !DualVpnEnabled || state.Routing.MultiServer != null ||
                                SelectedProfile()?.Id != currentPrimary.Id ||
                                RoutingMigrationService.Fingerprint(source) != RoutingMigrationService.Fingerprint(MultiServerRoutingHistory.AssignmentEditorSource(state)))
                                throw new InvalidOperationException("Настройки изменились. Выберите сервер заново.");
                        }
                        candidate = MultiServerRoutingWindow.CreateCandidate(source, next, source.EnableIpv6);
                    }
                    if (secondary) next.SetSecondaryProfile(chosen.Id);
                    else next.DefaultProfileId = chosen.Id;
                    next.Revision++;
                    next.Validate();
                    await ApplyServerAssignmentsAsync(opened, candidate);
                }
                catch (Exception error) { Toast(LogSanitizer.Sanitize(error.Message), Warning); }
                finally { if (!closing) RefreshCurrentPage(); }
            };
            menu.Items.Add(item);
        }
    }

	private Border BuildZapretPanel(VpnProfile selectedProfile)
	{
		Border card = new Border { Padding = new Thickness(22 * VisualDesign.Density(state.Visual)), Background = SoftSurface(false), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(22) };
		Grid grid = new Grid();
		bool showRegionMap = responsiveMode == "Wide";
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		if (showRegionMap) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel left = new StackPanel();
		left.Children.Add(Label("РЕЖИМ ТРАФИКА"));
		left.Children.Add(T("Выбран режим: " + TrafficModeLabel(state.Zapret.Mode), 16, TextColor, FontWeights.SemiBold));
		trafficModeStatusText = T(ZapretStatusCaption(), 11, Muted);
		left.Children.Add(trafficModeStatusText);
		WrapPanel modes = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
		foreach (string mode in new[] { "VPN", "VPN+Zapret", "Zapret" })
		{
			bool isSelected = string.Equals(state.Zapret.Mode, mode, StringComparison.OrdinalIgnoreCase);
			Button modeButton = SmallButton((isSelected ? "✓  " : "") + TrafficModeLabel(mode));
			modeButton.Margin = new Thickness(0, 0, 7, 0);
			modeButton.Background = new SolidColorBrush(isSelected ? Accent : Surface2);
			modeButton.Foreground = new SolidColorBrush(isSelected ? palette.AccentForeground : TextColor);
			modeButton.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
			modeButton.BorderBrush = new SolidColorBrush(isSelected ? Accent : BorderColor);
			modeButton.Template = TrafficModeButtonTemplate();
			modeButton.ToolTip = (isSelected ? "Выбран: " : "Переключить на: ") + TrafficModeLabel(mode);
			System.Windows.Automation.AutomationProperties.SetItemStatus(modeButton, isSelected ? "Выбран" : "Не выбран");
			string selectedMode = mode;
			modeButton.Click += async delegate { await ApplyTrafficModeAsync(selectedMode); };
			modes.Children.Add(modeButton);
		}
		left.Children.Add(modes);
		TourAnchor("home.mode", modes);
		left.Children.Add(T("Конфиг Zapret", 10, Muted));
		ComboBox strategyChoice = new ComboBox { MinWidth = 220, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 8) };
		strategyChoice.SetResourceReference(StyleProperty, "NovaSelectorStyle");
		foreach (string strategy in zapret.GetStrategies())
		{
			ComboBoxItem item = new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(strategy), Tag = strategy };
			strategyChoice.Items.Add(item);
			if (string.Equals(strategy, string.IsNullOrWhiteSpace(state.Zapret.SelectedStrategy) ? "general.bat" : state.Zapret.SelectedStrategy, StringComparison.OrdinalIgnoreCase)) strategyChoice.SelectedItem = item;
		}
		strategyChoice.IsEnabled = strategyChoice.Items.Count > 0 && trafficModeGate.CurrentCount > 0;
		strategyChoice.SelectionChanged += async delegate
		{
			ComboBoxItem selected = strategyChoice.SelectedItem as ComboBoxItem;
			if (selected == null) return;
			state.Zapret.SelectedStrategy = (string)selected.Tag;
			StateStore.Save(state);
			if (zapret.IsRunning) await ApplyTrafficModeAsync(state.Zapret.Mode);
			else Toast("Выбран конфиг Zapret: " + selected.Content, Green);
		};
		left.Children.Add(strategyChoice);
		TextBlock strategyText = T(zapret.StrategyStatus, 10, Muted);
		strategyText.TextWrapping = TextWrapping.Wrap;
		strategyText.MaxWidth = 520;
		left.Children.Add(strategyText);
		if (zapret.LifecycleState == ZapretLifecycleState.Starting)
		{
			Button cancel = SmallButton("Отменить запуск");
			cancel.Click += delegate
			{
				zapret.StopAndVerify();
				ZapretRoutingTransitionService.SetActive(state, false);
				state.Zapret.Mode = "VPN";
				StateStore.Save(state);
				RefreshCurrentPage();
			};
			left.Children.Add(cancel);
		}
		WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
		Button check = SmallButton("Проверка обновлений"); check.Margin = new Thickness(0, 0, 8, 8); check.Click += async delegate { await CheckZapretUpdatesAsync(); }; actions.Children.Add(check);
		Button update = SmallButton("Обновить Zapret"); update.Margin = new Thickness(0, 0, 0, 8); update.Click += async delegate { await UpdateZapretAsync(); }; actions.Children.Add(update);
		left.Children.Add(actions);
		if (!showRegionMap) {
			var mapButton = SmallButton("Карта серверов ↗");
			mapButton.Click += (s, e) => ShowServerMap();
			left.Children.Add(mapButton);
		}
		grid.Children.Add(left);
		if (showRegionMap)
		{
			UIElement routeMap = BuildServerRegionMap(selectedProfile);
			Grid.SetColumn(routeMap, 1);
			grid.Children.Add(routeMap);
		}
		StackPanel right = new StackPanel { Margin = new Thickness(showRegionMap ? 12 : 20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
		right.Children.Add(T("ZAPRET", 8.5, Muted, FontWeights.Bold));
		right.Children.Add(T(zapret.ReadInstalledVersion(), 12, TextColor, FontWeights.SemiBold));
		string zapretCaption = zapret.LifecycleState == ZapretLifecycleState.Stopping ? "Останавливается…" : zapret.LifecycleState == ZapretLifecycleState.Starting ? "Запускается…" : zapret.IsRunning ? "Процесс запущен" : zapret.IsInstalled ? "Установлен · остановлен" : "Не установлен";
		right.Children.Add(T(zapretCaption, 9, zapret.IsRunning ? Green : zapret.LifecycleState == ZapretLifecycleState.Error ? Danger : Muted));
		Grid.SetColumn(right, showRegionMap ? 2 : 1); grid.Children.Add(right);
		card.Child = grid;
		VisualDesign.ApplyGlassSurface(card, state.Visual);
		return card;
	}

	private UIElement BuildServerRegionMap(VpnProfile profile)
	{
		ServerRegionSelection region = ServerRegionMapService.Resolve(profile);
		StackPanel panel = new StackPanel
		{
			Width = 250,
			VerticalAlignment = VerticalAlignment.Center,
			ToolTip = region.HasCityCoordinates
				? "Встроенная офлайн-карта. Маркер поставлен по названию города в профиле сервера; приложение не определяет и не отправляет фактические координаты сервера."
				: region.HasCountry
					? "Встроенная офлайн-карта. Подсвечена страна сервера; маркер показывает географическую точку подписи страны, а не точное положение дата-центра."
				: "Встроенная офлайн-карта Natural Earth. В имени выбранного профиля не удалось определить страну подключения."
		};
		panel.Children.Add(Label("РЕГИОН ПОДКЛЮЧЕНИЯ"));
		panel.Children.Add(T(region.CountryName, 12, TextColor, FontWeights.SemiBold));
		if (!string.IsNullOrWhiteSpace(region.CityName))
			panel.Children.Add(T(region.CityName + " · узел из профиля", 9, Muted));
		else if (region.HasCountry)
			panel.Children.Add(T("Страна VPN-сервера", 9, Muted));
		else
			panel.Children.Add(T("Расположение по данным профиля", 9, Muted));

		InteractiveServerMap map = ServerRegionMapService.BuildInteractiveCanvas(state.Profiles.ToArray(), profile, 250, 132, palette, mapHealth,
			async target => await ChooseProfileRoleAsync(target), false);
		liveServerMap = map;
		map.Margin = new Thickness(0, 5, 0, 0);
		System.Windows.Automation.AutomationProperties.SetName(map, region.HasCountry
			? "Географическая карта региона VPN-сервера: " + region.CountryName + (region.CityName.Length > 0 ? ", " + region.CityName : "")
			: "Офлайн-карта мира; страна сервера в профиле не определена");
		panel.Children.Add(map);
		var expand = new Button { Content = "Все серверы на карте ↗", FontSize = 10, Padding = new Thickness(5),
			Margin = new Thickness(0, 4, 0, 0), Background = new SolidColorBrush(Surface), Foreground = new SolidColorBrush(TextColor) };
		expand.Click += (s, e) => ShowServerMap();
		panel.Children.Add(expand);
		return panel;
	}

	private void ShowServerMap()
	{
		if (serverMapWindow != null) { serverMapWindow.Activate(); return; }
		var dialog = new Window { Owner = this, Title = "Серверы на карте", Width = Math.Min(940, SystemParameters.WorkArea.Width),
			Height = Math.Min(620, SystemParameters.WorkArea.Height), MinWidth = 480, MinHeight = 320,
			WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Bg), Foreground = new SolidColorBrush(TextColor) };
		var grid = new Grid { Margin = new Thickness(20) };
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition());
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		var header = new StackPanel();
		header.Children.Add(T("Серверы на карте · нажмите точку или страну", 18, TextColor, FontWeights.SemiBold));
		header.Children.Add(T("Зелёный — HTTPS работает · красный — проверка не прошла · серый — нет свежей проверки", 11, Muted));
		grid.Children.Add(header);
		expandedServerMap = ServerRegionMapService.BuildInteractiveCanvas(state.Profiles.ToArray(), SelectedProfile(), 820, 400, palette, mapHealth,
			async target => { if (!DualVpnEnabled) dialog.Close(); await ChooseProfileRoleAsync(target); }, true);
		var viewport = new ServerMapViewport(expandedServerMap);
		var view = new Viewbox { Child = viewport, Stretch = Stretch.Uniform, Margin = new Thickness(0, 12, 0, 12) };
		var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
		Button zoomIn = SmallButton("+"), zoomOut = SmallButton("−"), resetMap = SmallButton("Вся карта");
		zoomIn.ToolTip = "Приблизить · Ctrl + колесо вверх"; zoomOut.ToolTip = "Отдалить · Ctrl + колесо вниз";
		zoomIn.Click += (s, e) => viewport.ZoomAt(new Point(viewport.Width / 2, viewport.Height / 2), 1.2);
		zoomOut.Click += (s, e) => viewport.ZoomAt(new Point(viewport.Width / 2, viewport.Height / 2), 1 / 1.2);
		resetMap.Click += (s, e) => viewport.ResetView();
		controls.Children.Add(zoomIn); controls.Children.Add(zoomOut); controls.Children.Add(resetMap); header.Children.Add(controls);
		var unknown = state.Profiles.Where(p => !ServerRegionMapService.Resolve(p).HasCountry).ToArray();
		if (unknown.Length > 0) {
			var withoutRegion = SmallButton("Без указанной страны: " + unknown.Length + " · выбрать из списка");
			withoutRegion.Click += (s, e) => expandedServerMap?.Choose(withoutRegion, unknown);
			header.Children.Add(withoutRegion);
		}
		Grid.SetRow(view, 1); grid.Children.Add(view);
		var caption = T("Ctrl + колёсико — масштаб · перетащите карту левой кнопкой мыши · клик — выбор сервера.\nЗелёный — свежий HTTPS-тест, красный — ошибка, серый — нет свежей проверки. Координаты определены по названию профиля.", 11, Muted);
		caption.TextWrapping = TextWrapping.Wrap;
		Grid.SetRow(caption, 2); grid.Children.Add(caption);
		dialog.Content = grid;
		serverMapWindow = dialog;
		dialog.Closed += (s, e) => { expandedServerMap = null; serverMapWindow = null; };
		dialog.Show();
		_ = RefreshMapHealthAsync();
	}

	private async Task RefreshMapHealthAsync()
	{
		liveServerMap?.RefreshStatus(); expandedServerMap?.RefreshStatus();
		if (snapshotMode || closing || mapHealthBusy || !IsVisible || WindowState == WindowState.Minimized ||
			(currentPage != "home" && expandedServerMap == null) || state.Profiles.Count == 0) return;
		mapHealthBusy = true;
		try
		{
			var profiles = state.Profiles.ToArray();
			var candidates = new List<VpnProfile>();
			var selected = SelectedProfile();
			if (selected != null && (!mapHealth.TryGetValue(selected.Id, out var selectedHealth) ||
				(selectedHealth.ProfileKey != SubscriptionManager.ConfigKey(selected) || selectedHealth.Supported && DateTime.UtcNow - selectedHealth.CheckedUtc > TimeSpan.FromSeconds(90)))) candidates.Add(selected);
			for (int i = 0; i < profiles.Length && candidates.Count < 8; i++)
			{
				var profile = profiles[mapHealthCursor++ % profiles.Length];
				if (candidates.Contains(profile)) continue;
				if (!mapHealth.TryGetValue(profile.Id, out var checkedResult) ||
					(checkedResult.ProfileKey != SubscriptionManager.ConfigKey(profile) || checkedResult.Supported && DateTime.UtcNow - checkedResult.CheckedUtc > TimeSpan.FromSeconds(90))) candidates.Add(profile);
			}
			using (var gate = new System.Threading.SemaphoreSlim(2))
			{
				var token = mapHealthCancellation.Token;
				string corePath = core.ProbeCorePath, directory = System.IO.Path.Combine(StateStore.DirectoryPath, "runtime", "map-probes");
				var tasks = candidates.Select(async profile => {
					await gate.WaitAsync(token);
					try {
						var value = await Task.Run(() => ServerMapHealthService.TestAsync(profile, corePath, directory, token), token);
						if (!closing && state.Profiles.Any(p => ReferenceEquals(p, profile))) {
							if (core.Status == CoreStatus.Connected && core.Verification.Completed && core.Verification.MultiExits == null && core.Verification.ProfileId == profile.Id && !core.Verification.InternetOk) {
								value.Success = false; value.Message = "Контрольный HTTPS-запрос активного соединения не прошёл";
							}
							if (!mapHealth.TryGetValue(profile.Id, out var newer) || newer.CheckedUtc <= value.CheckedUtc) mapHealth[profile.Id] = value;
							liveServerMap?.RefreshStatus(); expandedServerMap?.RefreshStatus();
						}
					} finally { gate.Release(); }
				}).ToArray();
				await Task.WhenAll(tasks);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { Debug.WriteLine("Map checks: " + LogSanitizer.Sanitize(ex.Message)); }
		finally { mapHealthBusy = false; }
	}

	private static string TrafficModeLabel(string mode)
	{
		return mode == "VPN+Zapret" ? "VPN + Zapret" : mode == "Zapret" ? "Zapret" : "VPN";
	}

	private ControlTemplate TrafficModeButtonTemplate()
	{
		// Keep selection opaque: glass templates intentionally soften backgrounds,
		// which made all three modes look identical at high transparency.
		FrameworkElementFactory face = new FrameworkElementFactory(typeof(Border));
		face.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
		face.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		face.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		face.SetValue(Border.BorderThicknessProperty, new Thickness(1));
		face.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
		content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
		face.AppendChild(content);
		return new ControlTemplate(typeof(Button)) { VisualTree = face };
	}

	private string ZapretStatusCaption()
	{
		if (zapret.LifecycleState == ZapretLifecycleState.Starting) return "Zapret запускается…";
		if (zapret.LifecycleState == ZapretLifecycleState.Stopping) return "Zapret останавливается…";
		bool vpnActive = core.Status == CoreStatus.Connected;
		if (vpnActive && zapret.IsRunning) return "Сейчас работают: VPN + Zapret";
		if (vpnActive) return "Сейчас работает: только VPN";
		if (zapret.IsRunning) return "Сейчас работает: только Zapret (без VPN-туннеля)";
		if (core.Status == CoreStatus.Connecting) return "VPN подключается…";
		return "Сейчас отключено · выбранный режим сохранён";
	}

	private async Task ApplyTrafficModeAsync(string mode)
	{
        if (snapshotMode || closing) return;
        if (configuredFallbackBusy) { Toast("Дождитесь резервного подключения или отключите VPN перед сменой режима.", Warning); return; }
        if (gamingSession != null || gamingCancellation != null) { Toast("Сначала выключите игровой режим или отмените подбор.", Warning); return; }
		if (zapret.LifecycleState == ZapretLifecycleState.Starting) zapret.StopAndVerify();
		await trafficModeGate.WaitAsync();
		try
		{
            if (snapshotMode || closing || configuredFallbackBusy || profileSwitchInProgress || gamingSession != null || gamingCancellation != null) return;
			if (mode != "VPN" && mode != "VPN+Zapret" && mode != "Zapret") throw new ArgumentException("Неизвестный режим трафика.", nameof(mode));
			bool vpnWasActive = core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting;
			if (vpnWasActive)
			{
				manualDisconnect = true;
				connectionRequested = false;
				core.Stop();
			}
			if (zapret.IsRunning) zapret.StopAndVerify();
			state.Zapret.Mode = mode;
			ZapretRoutingTransitionService.SetActive(state, mode != "VPN");
			StateStore.Save(state);
			if (mode != "VPN")
			{
				if (!zapret.IsInstalled && !await UpdateZapretCoreAsync(false)) throw new InvalidOperationException("Не удалось установить Zapret.");
				if (!zapret.IsRunning) await zapret.StartSelectedAsync(state.Zapret);
				if (!zapret.IsRunning) throw new InvalidOperationException("Zapret не подтвердил запуск.");
			}
			if (mode != "Zapret" && HasConnectionSelection)
			{
				manualDisconnect = false;
				connectionRequested = true;
				await StartWithConfiguredFallbackAsync();
				currentSessionStartedUtc = DateTime.UtcNow;
			}
			else
			{
				manualDisconnect = true;
				connectionRequested = false;
			}
			StateStore.Save(state);
			Toast("Режим: " + mode, Green);
		}
		catch (Exception ex)
		{
			try { core.Stop(); } catch { }
			try { zapret.StopAndVerify(); } catch { }
			ZapretRoutingTransitionService.SetActive(state, false);
			state.Zapret.Mode = "VPN";
			if (closing || ex is OperationCanceledException)
			{
				manualDisconnect = true;
				connectionRequested = false;
				StateStore.Save(state);
				return;
			}
			manualDisconnect = false;
			connectionRequested = HasConnectionSelection;
			StateStore.Save(state);
			if (HasConnectionSelection)
			{
				try { await StartWithConfiguredFallbackAsync(); }
				catch { manualDisconnect = true; connectionRequested = false; }
			}
			Toast("Не удалось применить режим: " + LogSanitizer.Sanitize(ex.Message) + ". Включён безопасный режим VPN.", Danger);
		}
		finally
		{
			trafficModeGate.Release();
			RefreshCurrentPage();
		}
	}

	private async Task CheckZapretUpdatesAsync()
	{
		try
		{
			ZapretReleaseInfo latest = await zapret.CheckAsync(state.Zapret);
			state.Zapret.LatestVersion = latest.Version;
			state.Zapret.LastCheckedUtcText = DateTime.UtcNow.ToString("o");
			bool updateAvailable = zapret.IsUpdateAvailable(latest.Version, zapret.ReadInstalledVersion());
			state.Zapret.LastUpdateMessage = updateAvailable
				? "Доступна версия " + latest.Version + ". Установка ожидает нажатия кнопки «Обновить Zapret»."
				: "Установлена последняя версия Zapret.";
			StateStore.Save(state);
			if (!updateAvailable)
			{
				Toast("Установлена последняя версия Zapret.", Green);
			}
			else
			{
				Toast("Найдена новая версия " + latest.Version + ". Для установки нажмите «Обновить Zapret».", Warning);
			}
		}
		catch (Exception ex) { Toast("Проверка обновлений не удалась: " + LogSanitizer.Sanitize(ex.Message), Warning); }
		RefreshCurrentPage();
	}

	private async Task UpdateApplicationAsync(Button button)
	{
		if (appUpdateInProgress) return;
		if (snapshotMode) return;
		appUpdateInProgress = true;
		button.IsEnabled = false;
		try
		{
			using (AppUpdateService updater = new AppUpdateService())
			{
				Toast("Ищу последнюю версию на GitHub…", Muted);
				AppReleaseInfo latest = await updater.GetLatestAsync();
				Version installedVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
				if (AppUpdateService.IsOlderThanInstalled(installedVersion, latest.Version))
				{
					Toast("GitHub пока предлагает более старую версию " + latest.Version + ". Откат не выполнялся.", Warning);
					return;
				}
				if (!AppUpdateService.IsUpdateAvailable(installedVersion, latest.Version))
				{
					Toast("Установлена последняя версия NOVA VPN.", Green);
					return;
				}
				await DownloadAndLaunchApplicationUpdateAsync(updater, latest);
			}
		}
		catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			Toast("Запуск обновления отменён. NOVA VPN продолжает работать.", Warning);
		}
		catch (Exception ex)
		{
			Toast("Не удалось установить обновление NOVA VPN: " + LogSanitizer.Sanitize(ex.Message), Danger);
		}
		finally
		{
			appUpdateInProgress = false;
			if (!closing) button.IsEnabled = true;
		}
	}

	private async Task DownloadAndLaunchApplicationUpdateAsync(AppUpdateService updater, AppReleaseInfo latest)
	{
		string updateDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NOVA VPN Updates");
		string installerPath = System.IO.Path.Combine(updateDirectory, "NOVA VPN Setup " + latest.Version.ToString(3) + " " + Guid.NewGuid().ToString("N") + ".exe");
		bool installerStarted = false;
		try
		{
			Toast("Скачиваю и проверяю установщик NOVA VPN " + latest.Version.ToString(3) + "…", Muted);
			await updater.DownloadInstallerAsync(latest, installerPath);
			if (closing) return;
			int processId;
			using (Process current = Process.GetCurrentProcess()) processId = current.Id;
			ProcessStartInfo startInfo = AppUpdateService.CreateInstallerStartInfo(installerPath, AppDomain.CurrentDomain.BaseDirectory, processId);
			using (Process process = Process.Start(startInfo))
			{
				if (process == null) throw new InvalidOperationException("Не удалось запустить установщик обновления.");
			}
			installerStarted = true;
			Application.Current.Shutdown();
		}
		finally
		{
			if (!installerStarted) { try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { } }
		}
	}

	private async Task<bool> CheckApplicationUpdatesOnStartupAsync()
	{
		if (!ShouldCheckForApplicationUpdates(snapshotMode, closing, appUpdateInProgress)) return false;
		appUpdateInProgress = true;
		bool installChosen = false;
		Border checkingBanner = new Border { Background = new SolidColorBrush(Surface2), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(10), Padding = new Thickness(18, 12, 18, 12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0), Child = T("Проверка наличия обновлений…", 13, TextColor) };
		contentHost.Children.Add(checkingBanner); Panel.SetZIndex(checkingBanner, 200);
		try
		{
			using (AppUpdateService updater = new AppUpdateService())
			{
				AppReleaseInfo latest = await updater.GetLatestAsync();
				contentHost.Children.Remove(checkingBanner);
				Version installed = Assembly.GetExecutingAssembly().GetName().Version;
				if (!AppUpdateService.IsUpdateAvailable(installed, latest.Version)) return false;

				if (closing || state.SuppressUpdatePrompts) return false;
				int choice = ShowApplicationUpdatePrompt(latest.Version);
				if (choice == 2) { state.SuppressUpdatePrompts = true; StateStore.Save(state); }
				if (choice != 0) return false;
				installChosen = true;

				await DownloadAndLaunchApplicationUpdateAsync(updater, latest);
				return true;
			}
		}
		catch (Exception ex)
		{
			if (installChosen && !closing)
				Toast("Не удалось подготовить обновление NOVA VPN: " + LogSanitizer.Sanitize(ex.Message), Warning);
			return false;
		}
		finally
		{
			contentHost.Children.Remove(checkingBanner);
			appUpdateInProgress = false;
		}
	}

	private int ShowApplicationUpdatePrompt(Version version)
	{
		int choice = 1;
		Window dialog = new Window { Owner = this, Title = "Обновление NOVA VPN", Width = 540, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Bg), Foreground = new SolidColorBrush(TextColor), FontFamily = VisualDesign.InterfaceFont(state.Visual) };
		StackPanel body = new StackPanel { Margin = new Thickness(24) };
		body.Children.Add(T("Хотите обновиться?", 22, TextColor, FontWeights.SemiBold));
		TextBlock details = T("На GitHub доступна NOVA VPN " + version.ToString(3) + ". Установщик будет скачан и запущен. Ваши ключи, профили и настройки сохраняются.", 13, Muted); details.TextWrapping = TextWrapping.Wrap; details.Margin = new Thickness(0, 12, 0, 20); body.Children.Add(details);
		WrapPanel actions = new WrapPanel();
		string[] labels = { "Да", "Возможно позже", "Больше не показывать" };
		for (int i = 0; i < labels.Length; i++) { int answer = i; Button button = SmallButton(labels[i]); button.Margin = new Thickness(0, 0, 8, 8); button.IsDefault = i == 0; button.Click += delegate { choice = answer; dialog.Close(); }; actions.Children.Add(button); }
		body.Children.Add(actions); dialog.Content = body; dialog.ShowDialog(); return choice;
	}

	private void ShowReleaseNotesIfNeeded()
	{
		if (snapshotMode || closing) return;
		Version currentVersion = ReleaseNotesService.CurrentVersion;
		IReadOnlyList<ReleaseNoteEntry> pending = ReleaseNotesService.GetPendingNotes(state.LastSeenReleaseNotesVersion, currentVersion);
		if (pending.Count == 0) return;

		bool openUpdateSettings = ShowReleaseNotesPrompt(pending, currentVersion);
		state.LastSeenReleaseNotesVersion = currentVersion.ToString(3);
		StateStore.Save(state);
		if (openUpdateSettings && !closing) Navigate("settings");
	}

	private bool ShowReleaseNotesPrompt(IReadOnlyList<ReleaseNoteEntry> entries, Version currentVersion)
	{
		bool openUpdateSettings = false;
		Window dialog = new Window
		{
			Owner = this,
			Title = "Что нового — NOVA VPN",
			Width = 600,
			Height = 520,
			MaxHeight = 680,
			ResizeMode = ResizeMode.NoResize,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = new SolidColorBrush(Bg),
			Foreground = new SolidColorBrush(TextColor),
			FontFamily = VisualDesign.InterfaceFont(state.Visual)
		};
		Grid layout = new Grid { Margin = new Thickness(24) };
		layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		layout.RowDefinitions.Add(new RowDefinition());
		layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
		header.Children.Add(T("Что нового", 23, TextColor, FontWeights.SemiBold));
		TextBlock subtitle = T("NOVA VPN " + currentVersion.ToString(3) + " · изменения после последнего запуска", 12, Muted);
		subtitle.Margin = new Thickness(0, 5, 0, 0);
		header.Children.Add(subtitle);
		layout.Children.Add(header);

		StackPanel noteList = new StackPanel();
		foreach (ReleaseNoteEntry entry in entries)
		{
			TextBlock versionHeading = T("Версия " + entry.Version.ToString(3), 14, TextColor, FontWeights.SemiBold);
			versionHeading.Margin = new Thickness(0, 4, 0, 8);
			noteList.Children.Add(versionHeading);
			foreach (string item in entry.Items)
			{
				TextBlock note = T("•  " + item, 12, Muted);
				note.TextWrapping = TextWrapping.Wrap;
				note.Margin = new Thickness(4, 0, 4, 10);
				noteList.Children.Add(note);
			}
		}
		ScrollViewer notesScroll = new ScrollViewer
		{
			Content = noteList,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Margin = new Thickness(0, 0, 0, 14)
		};
		Grid.SetRow(notesScroll, 1);
		SmoothScrollBehavior.Attach(notesScroll, () => MotionAllowed);
		layout.Children.Add(notesScroll);

		StackPanel footer = new StackPanel();
		Border updateLocation = new Border
		{
			Padding = new Thickness(12),
			Margin = new Thickness(0, 0, 0, 16),
			Background = new SolidColorBrush(Surface2),
			BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1),
			CornerRadius = Radius(10)
		};
		StackPanel locationText = new StackPanel();
		locationText.Children.Add(T("Где искать обновления", 12, TextColor, FontWeights.SemiBold));
		TextBlock locationHint = T("NOVA проверяет GitHub при запуске. Вручную: Настройки → Обновление приложения → «Обновить».", 11, Muted);
		locationHint.TextWrapping = TextWrapping.Wrap;
		locationHint.Margin = new Thickness(0, 4, 0, 0);
		locationText.Children.Add(locationHint);
		updateLocation.Child = locationText;
		footer.Children.Add(updateLocation);
		WrapPanel actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
		Button closeButton = SmallButton("Понятно");
		closeButton.Margin = new Thickness(0, 0, 8, 0);
		closeButton.IsDefault = true;
		closeButton.Click += delegate { dialog.Close(); };
		Button settingsButton = PrimaryButton("Открыть настройки", 166);
		settingsButton.Click += delegate { openUpdateSettings = true; dialog.Close(); };
		actions.Children.Add(closeButton);
		actions.Children.Add(settingsButton);
		footer.Children.Add(actions);
		Grid.SetRow(footer, 2);
		layout.Children.Add(footer);
		dialog.Content = layout;
		dialog.ShowDialog();
		return openUpdateSettings;
	}

	private static bool ShouldCheckForApplicationUpdates(bool isSnapshot, bool isClosing, bool updateInProgress)
	{
		return !isSnapshot && !isClosing && !updateInProgress;
	}

	private async Task UpdateZapretAsync(bool notify = true)
	{
        if (snapshotMode || closing) return;
        if (gamingSession != null || gamingCancellation != null) { Toast("Завершите игровую сессию перед обновлением Zapret.", Warning); return; }
		await trafficModeGate.WaitAsync();
		bool restartVpn = core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting;
		try
		{
            if (snapshotMode || closing || configuredFallbackBusy || profileSwitchInProgress || gamingSession != null || gamingCancellation != null) return;
			if (restartVpn)
			{
				manualDisconnect = true;
				connectionRequested = false;
				core.Stop();
			}
			bool updated = await UpdateZapretCoreAsync(notify);
			if (!updated && !zapret.IsRunning)
			{
				ZapretRoutingTransitionService.SetActive(state, false);
				state.Zapret.Mode = "VPN";
				StateStore.Save(state);
			}
			if (restartVpn && HasConnectionSelection)
			{
				manualDisconnect = false;
				connectionRequested = true;
				await StartWithConfiguredFallbackAsync();
				currentSessionStartedUtc = DateTime.UtcNow;
				StateStore.Save(state);
			}
		}
		catch (OperationCanceledException)
		{
			try { zapret.StopAndVerify(); } catch { }
			ZapretRoutingTransitionService.SetActive(state, false);
			state.Zapret.Mode = "VPN";
			manualDisconnect = true;
			connectionRequested = false;
			state.Zapret.Mode = "VPN";
			StateStore.Save(state);
		}
		catch (Exception ex)
		{
			if (restartVpn && HasConnectionSelection && core.Status != CoreStatus.Connected && core.Status != CoreStatus.Connecting)
			{
				try
				{
					if (!zapret.IsRunning)
					{
						ZapretRoutingTransitionService.SetActive(state, false);
						state.Zapret.Mode = "VPN";
					}
					manualDisconnect = false;
					connectionRequested = true;
					await StartWithConfiguredFallbackAsync();
				}
				catch { }
			}
			if (notify) Toast("Не удалось применить обновление Zapret: " + LogSanitizer.Sanitize(ex.Message), Danger);
		}
		finally
		{
			trafficModeGate.Release();
			if (notify) RefreshCurrentPage();
		}
	}

	private async Task<bool> UpdateZapretCoreAsync(bool notify)
	{
		try
		{
			bool shouldRun = state.Zapret.Mode != "VPN";
			ZapretReleaseInfo latest = await zapret.UpdateAsync(state.Zapret);
			state.Zapret.InstalledVersion = latest.Version;
			state.Zapret.LatestVersion = latest.Version;
			state.Zapret.LastCheckedUtcText = DateTime.UtcNow.ToString("o");
			state.Zapret.LastUpdateMessage = "Обновлено из " + latest.SourceUrl;
			foreach (string domain in ZapretDomainService.ReadStandardDomains(zapret.CurrentPath))
			{
				if (!state.Zapret.StandardDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) state.Zapret.StandardDomains.Add(domain);
			}
			ZapretRoutingTransitionService.SetActive(state, RoutingOverlayService.IsZapretMode(state.Zapret.Mode));
			StateStore.Save(state);
			if (shouldRun) await zapret.StartSelectedAsync(state.Zapret);
			StateStore.Save(state);
			if (notify) Toast("Zapret обновлён до " + latest.Version, Green);
			return true;
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception ex)
		{
			Toast("Не удалось обновить Zapret: " + LogSanitizer.Sanitize(ex.Message), Danger);
			return false;
		}
	}

	private RoutingSettings EffectiveRouting()
	{
        return GamingRouting.Effective(gamingSession != null ? gamingBaseRouting ?? state.Routing : state.Routing, gamingSession, state.KillSwitch);
	}

    private IWindowsStartupPreferences StartupPreferences() => new WindowsStartupPreferenceAdapter(
        () => Dispatcher.Invoke(() => new WindowsStartupPreferences(state.StartWithWindows, state.StartConnectedWithWindows)),
        value => Dispatcher.Invoke(() => {
            state.StartWithWindows = value.Open; state.StartConnectedWithWindows = value.Connect;
            StateStore.Save(state);
            using (var stream = new System.IO.FileStream(StateStore.StatePath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read)) stream.Flush(true);
        }));

	private async Task SetWindowsStartupAsync(bool connect)
	{
		if (startupSettingsBusy || snapshotMode) return;
		startupSettingsBusy = true;
		try {
			bool enabled = !(connect ? state.StartConnectedWithWindows : state.StartWithWindows);
			await WindowsStartupService.ApplyAsync(enabled, connect, Assembly.GetExecutingAssembly().Location, StartupPreferences());
            startupRecoveryFailed = false; startupRecoveryError = null; Navigate("settings");
		} catch (Exception ex) { startupRecoveryFailed = true; Toast("Не удалось завершить изменение автозапуска: " + LogSanitizer.Sanitize(ex.Message), Danger); }
		finally { startupSettingsBusy = false; }
	}

	public async Task RunWindowsBackgroundStartupAsync()
	{
		if (snapshotMode) return;
        if (startupRecoveryFailed) { ShowInTaskbar = true; Show(); return; }
		if (!state.StartConnectedWithWindows) { Application.Current.Shutdown(); return; }
		ShowInTaskbar = false;
		var token = startupCancellation.Token;
		startupConnecting = true;
		try {
			for (int i = 0; i < 15 && !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); i++) await Task.Delay(2000, token);
            if(MultiServerRoutingHistory.MissingRestoredProfile(state)){ShowNotification("Прежний сервер недоступен. Выберите сервер вручную перед автоподключением.");return;}
            if (state.Routing?.MultiServer is MultiServerSettings assignments) {
                if (assignments.RequiresRebinding) { ShowNotification("Заново назначьте серверы для импортированных правил."); return; }
                string expectedRouting = RoutingMigrationService.Fingerprint(state.Routing);
                string expectedPrimary = assignments.DefaultProfileId;
                token.ThrowIfCancellationRequested();manualDisconnect=false;connectionRequested=true;
                await StartAllowedProfileAsync(null, EffectiveRouting());
                var verified=await (core.InitialVerification??core.VerifyConnectionAsync());
                token.ThrowIfCancellationRequested();
                if(manualDisconnect||!connectionRequested||core.Status!=CoreStatus.Connected||core.ActiveProfileId!=expectedPrimary||
                    RoutingMigrationService.Fingerprint(state.Routing)!=expectedRouting||!ReferenceEquals(core.Verification,verified))return;
                if(HasVerifiedConnection(verified))RecordHistory("Автоподключение Windows","Подключены и проверены назначенные серверы.");
                else ShowNotification("Туннель назначений включён. Проверка отдельных выходов не подтверждена; назначения сохранены.");
                return; // Never enter legacy best-server/fallback selection for a manual multi-server policy.
            }
            var configured = ConfiguredFallbackService.Build(state.Profiles, state.SelectedProfileId, state.FallbackRootProfileId);
            if (configured.IsConfigured)
            {
                manualDisconnect = false; connectionRequested = true;
                try { await StartWithConfiguredFallbackAsync(token); RecordHistory("Автоподключение Windows", "Подключён профиль из заданной резервной цепочки."); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { manualDisconnect = true; connectionRequested = false; ShowNotification("Ни один профиль из резервной цепочки не подключился. Откройте NOVA из трея."); return; }
            }
			var candidates = CollectionCandidates();
            bool collectionActive = !string.IsNullOrEmpty(state.ActiveProfileCollectionId);
            if (collectionActive) await ProbeCollectionForStartupAsync(token);
			string preferred = state.LastConnectedProfileId ?? state.LastWorkingProfileId ?? state.SelectedProfileId;
			while (candidates.Count > 0) {
				token.ThrowIfCancellationRequested();
				var target = collectionActive ? CollectionBestAvailable() : await StartupProfileSelector.SelectAsync(candidates, preferred,
					p => ServerMapHealthService.TestAsync(p, core.ProbeCorePath, System.IO.Path.Combine(StateStore.DirectoryPath, "runtime", "startup-probes"), token), token);
				if (target == null) break;
				candidates.Remove(target); preferred = null;
				token.ThrowIfCancellationRequested();
				if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting || !state.Profiles.Contains(target)) return;
				state.SelectedProfileId = target.Id; StateStore.Save(state);
				try {
					manualDisconnect = false; connectionRequested = true;
					await StartAllowedProfileAsync(target, EffectiveRouting());
					var verified = await core.InitialVerification;
					token.ThrowIfCancellationRequested();
					if (state.SelectedProfileId != target.Id || core.Status!=CoreStatus.Connected || core.ActiveProfileId!=target.Id || !ReferenceEquals(core.Verification,verified)) return;
                    if (verified.IsVerifiedFor(target,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(core.CorePath),DateTime.UtcNow)) {
						RecordHistory("Автоподключение Windows", "Подключён проверенный профиль в фоне."); return;
					}
				} catch (OperationCanceledException) { throw; }
				catch (Exception ex) { Debug.WriteLine("Startup: " + LogSanitizer.Sanitize(ex.Message)); }
				if (token.IsCancellationRequested) return;
                target.ConsecutiveFailures = Math.Min(1000, target.ConsecutiveFailures + 1);
				manualDisconnect = true; connectionRequested = false; core.Stop();
			}
			if (trayIcon != null) trayIcon.Text = "NOVA VPN — нет доступного профиля";
			ShowNotification("Автоподключение: рабочий VPN-профиль не найден. Откройте NOVA из трея.");
		} catch (OperationCanceledException) { }
		catch (Exception ex) { Debug.WriteLine("Background startup: " + LogSanitizer.Sanitize(ex.Message)); ShowNotification("Не удалось подключить VPN автоматически. Откройте NOVA из трея."); }
		finally { startupConnecting = false; }
	}

	public void ConfigureSnapshot(string theme, string background, string density, string serverView, double textScale, bool highContrast)
	{
		if (!snapshotMode) return;
		if (!string.IsNullOrWhiteSpace(theme)) { state.Visual.DarkMode = ThemePalette.IsLegacyDark(theme); state.Visual.ThemeName = ThemePalette.Windows; }
		if (!string.IsNullOrWhiteSpace(background)) state.Visual.BackgroundMode = background;
		if (!string.IsNullOrWhiteSpace(density)) state.Visual.Density = density;
		if (!string.IsNullOrWhiteSpace(serverView)) state.Visual.ServerViewMode = serverView;
		if (textScale > 0) state.Visual.TextScale = textScale;
		state.Visual.HighContrast = highContrast;
		BuildShell();
	}

	public Window CreateAppearanceSnapshotWindow()
	{
		return new AppearanceWindow(state.Visual, state.ShowAnimations);
	}

	private void PrepareSnapshotData()
	{
		state.Profiles.Add(new VpnProfile { Name = "DE · Frankfurt Nova", Protocol = "vless", Server = "de.example.test", Port = 443, LastLatency = 42, AverageLatency = 46, LastLossPercent = 0, HealthStatus = "Быстрый", LastTestMessage = "Тестовый профиль", IsFavorite = true });
		state.Profiles.Add(new VpnProfile { Name = "NL · Amsterdam", Protocol = "trojan", Server = "nl.example.test", Port = 443, LastLatency = 86, AverageLatency = 91, LastLossPercent = 0, HealthStatus = "Быстрый", LastTestMessage = "Тестовый профиль" });
		state.Profiles.Add(new VpnProfile { Name = "US · New York", Protocol = "hysteria2", Server = "us.example.test", Port = 8443, LastLatency = 238, AverageLatency = 244, LastLossPercent = 33, HealthStatus = "Нестабильный", LastTestMessage = "Тестовый профиль" });
		state.SelectedProfileId = state.Profiles[0].Id;
		applicationCatalog = new List<ApplicationEntry>
		{
			new ApplicationEntry { Name = "Браузер", ExecutablePath = @"C:\Program Files\Browser\browser.exe", Source = "Пример" },
			new ApplicationEntry { Name = "Discord", ExecutablePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "discord.exe"), Source = "Пример" },
			new ApplicationEntry { Name = "Игра", ExecutablePath = @"C:\Games\Game\game.exe", Source = "Пример" }
		};
		state.Routing.ProxyProcessPaths.Add(applicationCatalog[1].ExecutablePath);
		state.Routing.DirectProcessPaths.Add(applicationCatalog[2].ExecutablePath);
	}

	private UIElement BuildHomeInsights(VpnProfile profile)
	{
		Grid root = new Grid();
		if (state.Visual.ShowLiveGraph)
		{
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(94) });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.Children.Add(BuildTrafficGraph());
		}
		else root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		List<string> order = (state.Visual.HomeCardOrder ?? new List<string>()).Where(key => !state.Visual.HiddenHomeCards.Contains(key)).ToList();
		if (order.Count == 0) return root;
		int columns = responsiveMode == "Wide" ? order.Count : responsiveMode == "Balanced" ? Math.Min(3, order.Count) : Math.Min(2, order.Count);
		int rows = (order.Count + columns - 1) / columns;
		UniformGrid cards = new UniformGrid { Columns = columns, Rows = rows };
		for (int i = 0; i < order.Count; i++)
		{
			string key = order[i];
			switch (key)
			{
				case "Speed": cards.Children.Add(InfoCard("СКОРОСТЬ", CurrentSpeedLabel(), "↓ входящая  ·  ↑ исходящая", i)); break;
				case "Protocol": cards.Children.Add(InfoCard("ПРОТОКОЛ", profile == null ? "—" : profile.ProtocolLabel, "Тип подключения", i)); break;
				case "Latency": cards.Children.Add(InfoCard("HTTPS-ОТКЛИК", profile == null || profile.LastLatency < 0 ? "—" : profile.LastLatency + " мс", "Через VPN-профиль", i)); break;
				case "Session": cards.Children.Add(InfoCard("СЕАНС", SessionLabel(), core.Status == CoreStatus.Connected ? "Защищённое время" : "Не запущен", i)); break;
				default: cards.Children.Add(InfoCard("ВНЕШНИЙ IP", core.Status == CoreStatus.Connected ? tunnelExternalIp : directExternalIp, core.Status == CoreStatus.Connected ? "После туннеля" : "До подключения", i)); break;
			}
		}
		Grid.SetRow(cards, state.Visual.ShowLiveGraph ? 2 : 0); root.Children.Add(cards); return root;
	}

	private UIElement BuildTrafficGraph()
	{
		if (snapshotMode && downloadSamples.Count == 0)
		{
			downloadSamples.AddRange(new double[] { 0.4, 1.2, 0.8, 2.4, 2.0, 3.6, 2.8, 4.5, 3.2, 5.1, 4.2, 5.8 });
			uploadSamples.AddRange(new double[] { 0.2, 0.3, 0.4, 0.9, 0.6, 1.2, 0.7, 1.4, 1.1, 1.7, 1.2, 1.9 });
		}
		Border card = new Border { Padding = new Thickness(14, 10, 14, 8), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(15) };
		Grid grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
		StackPanel stats = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; stats.Children.Add(Label("ЖИВОЙ ТРАФИК"));
		liveDownloadText = T("↓  " + FormatRate(downloadSamples.LastOrDefault()), 13, Teal, FontWeights.SemiBold); liveDownloadText.Margin = new Thickness(0, 6, 0, 2); stats.Children.Add(liveDownloadText);
		liveUploadText = T("↑  " + FormatRate(uploadSamples.LastOrDefault()), 10.5, AccentLight, FontWeights.SemiBold); stats.Children.Add(liveUploadText); grid.Children.Add(stats);
		trafficCanvas = new Canvas { Height = 62, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
		for (int i = 1; i <= 2; i++) trafficCanvas.Children.Add(new Line { X1 = 0, X2 = 2000, Y1 = i * 20, Y2 = i * 20, Stroke = new SolidColorBrush(BorderColor), StrokeThickness = 0.7, Opacity = 0.5 });
		downloadLine = new Polyline { Stroke = new SolidColorBrush(Teal), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
		uploadLine = new Polyline { Stroke = new SolidColorBrush(AccentLight), StrokeThickness = 1.7, Opacity = 0.8, StrokeLineJoin = PenLineJoin.Round };
		trafficCanvas.Children.Add(downloadLine); trafficCanvas.Children.Add(uploadLine); Grid.SetColumn(trafficCanvas, 1); grid.Children.Add(trafficCanvas); card.Child = grid;
		VisualDesign.ApplyGlassSurface(card, state.Visual);
		card.Loaded += delegate { RedrawTrafficGraph(); }; trafficCanvas.SizeChanged += delegate { RedrawTrafficGraph(); }; return card;
	}

	private UIElement BuildConnectionPath(VpnProfile profile)
	{
		StackPanel path = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), ToolTip = "Визуальный путь текущего соединения" };
		path.Children.Add(PathNode("shield", "ПК", core.Status == CoreStatus.Connected ? Green : Muted)); path.Children.Add(PathArrow()); path.Children.Add(PathNode("routing", "NOVA", core.Status == CoreStatus.Connected ? AccentLight : Muted)); path.Children.Add(PathArrow()); path.Children.Add(PathNode("servers", profile == null ? "Сервер" : ServerGroupingService.CountryCode(profile), core.Status == CoreStatus.Connected ? Teal : Muted)); path.Children.Add(PathArrow()); path.Children.Add(PathNode("globe", "Интернет", core.Verification.InternetOk ? Green : Muted)); return path;
	}

	private UIElement PathNode(string icon, string label, Color color)
	{
		StackPanel node = new StackPanel { Width = label == "Интернет" ? 68 : 38 }; Viewbox glyph = IconFactory.Create(icon, 16, color); glyph.HorizontalAlignment = HorizontalAlignment.Center; node.Children.Add(glyph); TextBlock copy = T(label, 7.5, color, FontWeights.SemiBold); copy.HorizontalAlignment = HorizontalAlignment.Center; copy.Margin = new Thickness(0, 3, 0, 0); node.Children.Add(copy); return node;
	}

	private TextBlock PathArrow() { TextBlock arrow = T("›", 16, Muted); arrow.VerticalAlignment = VerticalAlignment.Center; arrow.Margin = new Thickness(1, 0, 1, 8); return arrow; }

	private async Task ChooseBestProfileAsync()
	{
		if (core.Status == CoreStatus.Connecting || profileSwitchInProgress)
		{
			Toast("Дождитесь завершения текущего подключения или переключения.", Warning);
			return;
		}
		if (state.Profiles.Count == 0)
		{
			Toast("Сначала добавьте хотя бы один конфиг.", Warning);
			Navigate("servers");
			return;
		}
		ShowBusySkeleton("Проверяем HTTPS через профили", Math.Max(3, Math.Min(7, state.Profiles.Count)));
		await Dispatcher.Yield(DispatcherPriority.Background);
		await HealthCheckService.TestAllAsync(CollectionCandidates());
		VpnProfile best = CollectionBestAvailable();
		if (best == null) Toast("Доступные серверы не найдены.", Warning);
		else await SelectProfileAsync(best);
		Navigate("servers");
	}

	private async Task SelectProfileAsync(VpnProfile profile)
	{
        if (snapshotMode || closing) return;
        if (configuredFallbackBusy) { Toast("Дождитесь резервного подключения или отключите VPN.", Warning); return; }
        if (gamingSession != null || gamingCancellation != null) { Toast("Сначала выключите игровой режим или отмените подбор.", Warning); return; }
		startupCancellation.Cancel();
		if (profile == null || closing) return;
        string collectionError = ProfileCollectionService.Exclusion(state, profile);
        if (collectionError != null) { Toast(collectionError, Warning); return; }
		if (profileSwitchInProgress)
		{
			Toast("Переключение конфига уже выполняется.", Warning);
			return;
		}
		if (core.Status == CoreStatus.Connecting)
		{
			Toast("Дождитесь завершения текущего подключения, затем выберите конфиг.", Warning);
			return;
		}

		profileSwitchInProgress = true;
		bool gateAcquired = false;
		try
		{
			await trafficModeGate.WaitAsync();
			gateAcquired = true;
			if (closing) return;
			if (core.Status == CoreStatus.Connecting)
			{
				Toast("Дождитесь завершения текущего подключения, затем выберите конфиг.", Warning);
				return;
			}
			VpnProfile current = SelectedProfile();
			if (current != null && string.Equals(current.Id, profile.Id, StringComparison.Ordinal))
			{
				Toast("Этот конфиг уже выбран.", Muted);
				return;
			}

			bool reconnect = core.Status == CoreStatus.Connected;
			if (!reconnect)
			{
                state.FallbackRootProfileId = profile.Id; fallbackFailedProfileId = null;
				state.SelectedProfileId = profile.Id;
				StateStore.Save(state);
				Toast("Выбран конфиг: " + profile.Name, Green);
				return;
			}

			RecordHistory("Переключение", "Автоматический перезапуск туннеля на конфиге «" + profile.Name + "».");
			reconnectTimer.Stop();
			manualDisconnect = false;
			connectionRequested = true;
			ShowBusySkeleton("Переключаем VPN на «" + profile.Name + "»", 3);
			await Dispatcher.Yield(DispatcherPriority.Background);

            if (closing || manualDisconnect || !connectionRequested) return;
            ProfileSwitchResult result = await ProfileSwitchService.SwitchConfiguredAsync(
                state, profile, () => core.Stop(), async (target, restoring) =>
                {
                    fallbackFailedProfileId = null;
                    await StartWithConfiguredFallbackPreferredAsync(restoring ? target.Id : null, default(System.Threading.CancellationToken));
                    var connected = SelectedProfile();
                    if (connected == null || core.Status != CoreStatus.Connected || core.ActiveProfileId != connected.Id)
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(core.LastError) ? "Новое VPN-подключение не запустилось." : core.LastError);
                    return connected;
                }, () => StateStore.Save(state), () => !closing && !manualDisconnect && connectionRequested);
			StateStore.Save(state);

            if (result.Cancelled)
            {
                manualDisconnect = true; connectionRequested = false; reconnectTimer.Stop();
                Toast("Переключение отменено. VPN отключён.", Muted);
            }
			else if (result.TargetConnected)
			{
                state.FallbackRootProfileId = profile.Id; fallbackFailedProfileId = null;
                StateStore.Save(state);
				manualDisconnect = false;
				connectionRequested = true;
				currentSessionStartedUtc = DateTime.UtcNow;
				result.ConnectedProfile.ConsecutiveFailures = 0;
				result.ConnectedProfile.HealthStatus = "Подключён";
				result.ConnectedProfile.LastTestMessage = "Конфиг подключён автоматически после переключения.";
				reconnectAttempt = 0;
				RecordHistory("Переключено", "Соединение автоматически восстановлено на новом конфиге.");
				Toast("VPN переключён на «" + result.ConnectedProfile.Name + "».", Green);
			}
			else if (result.PreviousRestored)
			{
				manualDisconnect = false;
				connectionRequested = true;
				currentSessionStartedUtc = DateTime.UtcNow;
				profile.ConsecutiveFailures++;
                result.ConnectedProfile.ConsecutiveFailures = 0;
				result.ConnectedProfile.HealthStatus = "Подключён";
				result.ConnectedProfile.LastTestMessage = "Подключение восстановлено через прежнюю резервную цепочку.";
				Toast("Не удалось подключить «" + profile.Name + "». Подключение восстановлено через «" + result.ConnectedProfile.Name + "»: " + LogSanitizer.Sanitize(result.TargetError?.Message), Warning);
			}
			else
			{
				manualDisconnect = true;
				connectionRequested = false;
				profile.ConsecutiveFailures++;
				profile.HealthStatus = "Ошибка подключения";
				profile.LastTestMessage = LogSanitizer.Sanitize(result.TargetError?.Message);
				RecordHistory("Ошибка", "Не удалось подключить новый или восстановить прежний VPN-конфиг.");
				string message = "Не удалось подключить новый конфиг: " + LogSanitizer.Sanitize(result.TargetError?.Message);
				if (result.RestoreError != null) message += ". Не удалось вернуть прежний: " + LogSanitizer.Sanitize(result.RestoreError.Message);
				Toast(message, Danger);
			}
		}
		catch (Exception ex)
		{
			manualDisconnect = core.Status != CoreStatus.Connected;
			connectionRequested = core.Status == CoreStatus.Connected;
			Toast("Не удалось переключить VPN-конфиг: " + LogSanitizer.Sanitize(ex.Message), Danger);
		}
		finally
		{
			if (gateAcquired) trafficModeGate.Release();
			profileSwitchInProgress = false;
			if (!closing) RefreshCurrentPage();
		}
	}

    private string serverSearch = "";
    private bool serverFavoritesOnly;
    private bool serverSortByLatency;
	private UIElement BuildServersPage()
	{
		Grid grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(20.0)
		});
		grid.RowDefinitions.Add(new RowDefinition());
		Button addButton = PrimaryButton("Добавить конфигурацию", 190.0);
		TourAnchor("servers.import", addButton);
		addButton.Click += async delegate
		{
			await ShowImportDialogAsync();
		};
		Button testAll = SmallButton(state.AutoSelectBestServer ? "Проверить и выбрать лучший" : "Проверить все");
		testAll.IsEnabled = !profileSwitchInProgress && core.Status != CoreStatus.Connecting;
		testAll.Click += async delegate
		{
			testAll.IsEnabled = false;
			testAll.Content = "Проверяем…";
			ShowBusySkeleton("Проверяем серверы", Math.Max(3, Math.Min(7, state.Profiles.Count)));
			await Dispatcher.Yield(DispatcherPriority.Background);
			await HealthCheckService.TestAllAsync(CollectionCandidates());
			StateStore.Save(state);
			VpnProfile best = CollectionBestAvailable();
			if (best != null && state.AutoSelectBestServer) await SelectProfileAsync(best);
			else Toast(best == null ? "Доступные серверы не найдены." : "Лучший сервер: " + best.Name + " · " + best.LastLatency + " мс", best == null ? Warning : Green);
			Navigate("servers");
		};
		Button chooseBest = SmallButton("Выбрать лучший");
		TourAnchor("servers.best", state.AutoSelectBestServer ? testAll : chooseBest);
		chooseBest.IsEnabled = !profileSwitchInProgress && core.Status != CoreStatus.Connecting;
		chooseBest.Click += async delegate
		{
			chooseBest.IsEnabled = false;
			chooseBest.Content = "Выбираем…";
			ShowBusySkeleton("Проверяем HTTPS через профили", Math.Max(3, Math.Min(7, state.Profiles.Count)));
			await Dispatcher.Yield(DispatcherPriority.Background);
			await HealthCheckService.TestAllAsync(CollectionCandidates());
			VpnProfile best = CollectionBestAvailable();
			if (best == null) Toast("Доступные серверы не найдены.", Warning);
			else await SelectProfileAsync(best);
			Navigate("servers");
		};
		Button update = SmallButton("Обновить подписки");
		update.IsEnabled = state.Subscriptions.Count > 0;
		update.Click += async delegate
		{
			update.IsEnabled = false;
			update.Content = "Обновляем…";
			ShowBusySkeleton("Обновляем подписки", 4);
			await Dispatcher.Yield(DispatcherPriority.Background);
			await ReviewSubscriptionsAsync();
			Navigate("servers");
		};
		var actions = new System.Windows.Controls.Primitives.UniformGrid
        {
            Name = "ServerActions", Columns = state.Visual.TextScale > 1.15 ? 2 : 3,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Width = responsiveMode == "Compact" ? 460 : 620
        };
		testAll.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		chooseBest.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		update.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);

		actions.Children.Add(testAll);
		if (!state.AutoSelectBestServer) actions.Children.Add(chooseBest);
		actions.Children.Add(update);
		actions.Children.Add(addButton);
        var importFile = SmallButton("Из файла"); importFile.Margin = new Thickness(0, 6, 8, 0);
        importFile.Click += async delegate { await ImportFileAsync(); }; actions.Children.Add(importFile);
        var qr = SmallButton("QR ▾"); qr.Margin = new Thickness(0, 6, 8, 0);
        var qrMenu = new ContextMenu(); qr.ContextMenu = qrMenu;
        foreach (var option in new[] { new[] { "С экрана", "screen" }, new[] { "Из изображения", "file" }, new[] { "Из буфера обмена", "clipboard" } })
        {
            string source = option[1]; var item = new MenuItem { Header = option[0] };
            item.Click += async delegate { await ImportQrAsync(source); }; qrMenu.Items.Add(item);
        }
        qr.Click += delegate { qrMenu.PlacementTarget = qr; qrMenu.IsOpen = true; }; actions.Children.Add(qr);
        var fallback = SmallButton("Резервные профили"); fallback.Margin = new Thickness(0, 6, 8, 0);
        fallback.Click += delegate { EditFallbackProfiles(); }; actions.Children.Add(fallback);
        var corporate = SmallButton("Корпоративный прокси"); corporate.Margin = new Thickness(0, 6, 8, 0);
        corporate.Click += async delegate { await EditCorporateProxyAsync(); }; actions.Children.Add(corporate);
        var vps = SmallButton("Мой VPS"); vps.Margin = new Thickness(0, 6, 8, 0);
        vps.Click += async delegate { await OpenVpsAsync(); }; actions.Children.Add(vps);
        var services = SmallButton("Доступность сервисов"); services.Margin = new Thickness(0, 6, 8, 0);
        services.Click += async delegate {
            var dialog = new ServiceMatrixWindow(CollectionCandidates(), () => CollectionCandidates(), core.ProbeCorePath,
                System.IO.Path.Combine(StateStore.DirectoryPath, "runtime", "service-matrix"), state.Visual, snapshotMode, mapHealthCancellation.Token) { Owner = this };
            if (dialog.ShowDialog() == true && !snapshotMode) {
                var chosen = CollectionCandidates().FirstOrDefault(p => p.Id == dialog.ChosenProfileId);
                if (chosen != null) await SelectProfileAsync(chosen);
            }
        }; actions.Children.Add(services);
        services.Name = "ServiceAvailability";
        foreach (Button action in actions.Children)
        {
            string label = (string)action.Content;
            action.MinWidth = 0;
            action.Width = double.NaN;
            action.Height = Math.Max(44, 44 * state.Visual.TextScale);
            action.Margin = new Thickness(0, 0, 8, 8);
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
            action.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            action.Padding = new Thickness(10, 4, 10, 4);
            action.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(action, label);
        }
        // Fit the action grid to the actual page width, including DPI and enlarged text.
        grid.SizeChanged += delegate { actions.Width = Math.Min(620, Math.Max(1, grid.ActualWidth - (responsiveMode == "Wide" && state.Visual.TextScale <= 1.15 ? 210 : 0))); };
		grid.Children.Add(PageHeader("Серверы", state.Profiles.Count + " профилей · " + state.Profiles.Count(p => p.IsFavorite) + " избранных", actions, null));
        var stackPanel = new StackPanel();
        Action renderServers = delegate {
            stackPanel.Children.Clear();
            stackPanel.Children.Add(CollectionBar());
            RenderSubscriptionCards(stackPanel);

		if (state.Profiles.Count == 0)
		{
			Border border = EmptyState("servers", "Здесь появятся ваши серверы", "Вставьте одиночный VPN-ключ или ссылку на подписку.");
			border.Margin = new Thickness(0.0, 36.0, 0.0, 0.0);
			stackPanel.Children.Add(border);
		}
		else
		{
			var visible = CollectionCandidates().Where(p => (!serverFavoritesOnly || p.IsFavorite) && (string.IsNullOrWhiteSpace(serverSearch) || (p.Name ?? "").IndexOf(serverSearch, StringComparison.OrdinalIgnoreCase) >= 0 || (p.Endpoint ?? "").IndexOf(serverSearch, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (visible.Count == 0) stackPanel.Children.Add(EmptyState("servers", "Серверы не найдены", "Измените поисковый запрос или фильтр избранного."));
            var groups = visible.GroupBy(ServerGroupingService.CountryLabel).OrderBy(group => group.Key == "Другие" ? 1 : 0).ThenBy(group => group.Key);
			foreach (var group in groups)
			{
				TextBlock groupTitle = SectionTitle(group.Key.ToUpperInvariant() + " · " + group.Count());
				groupTitle.Margin = new Thickness(2.0, stackPanel.Children.Count == 0 ? 0.0 : 12.0, 0.0, 8.0);
				stackPanel.Children.Add(groupTitle);
				IEnumerable<VpnProfile> sorted = serverSortByLatency ? group.OrderBy(p => p.LastLatency > 0 ? p.LastLatency : int.MaxValue) : group.OrderBy(p => p.Name);
                foreach (VpnProfile profile in sorted) stackPanel.Children.Add(ServerCard(profile));
			}
		}
        };
        var search = new TextBox { Text = serverSearch, MinHeight = 34, Padding = new Thickness(10,6,10,6), FontSize = 14, Background = new SolidColorBrush(palette.Input), Foreground = new SolidColorBrush(TextColor), BorderBrush = new SolidColorBrush(BorderColor), ToolTip = "Поиск по названию или адресу", Margin = new Thickness(0,0,12,0) };
        AutomationProperties.SetName(search,"Поиск серверов");
        search.TextChanged += delegate { serverSearch = search.Text; renderServers(); };
        var filterBar = new Grid { Margin = new Thickness(0,0,0,16) };
        filterBar.ColumnDefinitions.Add(new ColumnDefinition()); filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterBar.Children.Add(search);
        var favorites = new CheckBox { Content = "Избранные", IsChecked = serverFavoritesOnly, Foreground = new SolidColorBrush(TextColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
        favorites.Click += delegate { serverFavoritesOnly = favorites.IsChecked == true; renderServers(); };Grid.SetColumn(favorites,1);filterBar.Children.Add(favorites);
        var sort = new ComboBox { ItemsSource = new[] { "По названию", "По задержке" }, SelectedIndex = serverSortByLatency ? 1 : 0, MinWidth = 140, Style = Application.Current.Resources["NovaSelectorStyle"] as Style };
        AutomationProperties.SetName(sort,"Сортировка серверов");
        sort.SelectionChanged += delegate { serverSortByLatency = sort.SelectedIndex == 1; renderServers(); };Grid.SetColumn(sort,2);filterBar.Children.Add(sort);
        renderServers();
		ScrollViewer scrollViewer = TouchScrollViewer(new ScrollViewer());
		scrollViewer.Content = stackPanel;
		scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var results = new Grid();results.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });results.RowDefinitions.Add(new RowDefinition());
        results.Children.Add(filterBar); Grid.SetRow(scrollViewer,1);results.Children.Add(scrollViewer);
        Grid.SetRow(results,2);grid.Children.Add(results);
		return grid;
	}

	private UIElement ServerCard(VpnProfile profile)
	{
		bool selected = state.SelectedProfileId == profile.Id;
		bool compact = state.Visual.ServerViewMode == "Compact";
		Border border = new Border();
		border.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		border.Padding = compact ? new Thickness(14.0, 10.0, 12.0, 10.0) : new Thickness(18.0, 15.0, 14.0, 15.0);
		border.CornerRadius = Radius(16.0);
		border.Background = selected ? new SolidColorBrush(C("#1C1D30")) : SoftSurface(false);
		border.BorderBrush = new SolidColorBrush(selected ? Accent : BorderColor);
		border.BorderThickness = new Thickness(selected ? 1.2 : 1.0);
		border.Cursor = Cursors.Hand;
		Border border2 = border;
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(52.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(0.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(96.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(0.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38.0) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38.0) });
		Border border3 = RegionMark(profile);
		border3.Width = 40.0;
		border3.Height = 40.0;
		grid.Children.Add(border3);
		StackPanel stackPanel = new StackPanel();
		stackPanel.VerticalAlignment = VerticalAlignment.Center;
		StackPanel stackPanel2 = stackPanel;
		StackPanel stackPanel3 = new StackPanel();
		stackPanel3.Orientation = Orientation.Vertical;
		StackPanel stackPanel4 = stackPanel3;
		TextBlock serverName = T(profile.Name, 13.5, TextColor, FontWeights.SemiBold);
		serverName.TextWrapping = TextWrapping.Wrap;
		stackPanel4.Children.Add(serverName);
		if (selected)
		{
			TextBlock textBlock = T(core.Status == CoreStatus.Connected && core.ActiveProfileId == profile.Id ? "ПОДКЛЮЧЁН" : "ВЫБРАН", 8.0, AccentLight, FontWeights.Bold);
			textBlock.Margin = new Thickness(10.0, 4.0, 0.0, 0.0);
			stackPanel4.Children.Add(textBlock);
		}
		stackPanel2.Children.Add(stackPanel4);
		StackPanel endpointLine = new StackPanel { Orientation = Orientation.Vertical };
		TextBlock serverEndpoint = T(profile.Endpoint, 10.5, Muted);
		serverEndpoint.TextWrapping = TextWrapping.Wrap;
		endpointLine.Children.Add(serverEndpoint);
		TextBlock protocolInline = T("  ·  " + profile.ProtocolLabel, 9.0, AccentLight, FontWeights.SemiBold); endpointLine.Children.Add(protocolInline);
		stackPanel2.Children.Add(endpointLine);
		TextBlock provider = T(ServerGroupingService.ProviderLabel(profile), 8.5, C("#6F788A"));
		provider.ToolTip = "Источник профиля";
		if (!compact && !string.Equals(ServerGroupingService.ProviderLabel(profile), profile.Server, StringComparison.OrdinalIgnoreCase)) stackPanel2.Children.Add(provider);
		TextBlock health = T(profile.HealthStatus ?? "Не проверен", 9.0, profile.ConsecutiveFailures > 0 ? Danger : profile.LastLatency > 0 ? Green : Muted);
		health.ToolTip = profile.LastTestMessage;
		if (!compact) { stackPanel2.Children.Add(health); var measurement = T(ProfileHealthEvidence.MeasurementLabel(profile), 8.5, Muted); measurement.TextWrapping = TextWrapping.Wrap; stackPanel2.Children.Add(measurement); }
		Grid.SetColumn(stackPanel2, 1);
		grid.Children.Add(stackPanel2);
		StackPanel stackPanel5 = new StackPanel();
		stackPanel5.VerticalAlignment = VerticalAlignment.Center;
		StackPanel stackPanel6 = stackPanel5;
		stackPanel6.Children.Add(Label("ПРОТОКОЛ"));
		stackPanel6.Children.Add(T(profile.ProtocolLabel, 10.5, TextColor, FontWeights.SemiBold));
		Grid.SetColumn(stackPanel6, 2);
		grid.Children.Add(stackPanel6);
		Button latency = SmallButton((profile.LastLatency < 0) ? "Проверить" : (profile.LastLatency + " мс"));
		latency.Foreground = new SolidColorBrush(LatencyColor(profile.LastLatency));
		latency.Click += async delegate(object sender, RoutedEventArgs e)
		{
			e.Handled = true;
			Viewbox spinnerIcon = IconFactory.Create("speed", 15, AccentLight); latency.Content = spinnerIcon;
			if (MotionAllowed && !state.Visual.ReduceMotion) { RotateTransform spin = new RotateTransform(); spinnerIcon.RenderTransformOrigin = new Point(0.5, 0.5); spinnerIcon.RenderTransform = spin; BeginMotionAnimation(spin, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(AnimMs(700))) { RepeatBehavior = RepeatBehavior.Forever }); }
			int value = await core.TestLatencyAsync(profile);
			profile.LastLatency = value;
			StateStore.Save(state);
			Navigate("servers");
		};
		Grid.SetColumn(latency, 3);
		grid.Children.Add(latency);
		Button edit = VectorIconButton("edit", "Переименовать");
		edit.ToolTip = "Переименовать";
		edit.Click += delegate(object sender, RoutedEventArgs e)
		{
			e.Handled = true;
            RenameProfile(profile);
		};
		Grid.SetColumn(edit, 4);

		Button favorite = VectorIconButton("star", profile.IsFavorite ? "Убрать из избранного" : "Добавить в избранное", profile.IsFavorite ? Warning : Muted, profile.IsFavorite);
		favorite.ToolTip = profile.IsFavorite ? "Убрать из избранного" : "Добавить в избранное";
		favorite.Foreground = new SolidColorBrush(profile.IsFavorite ? Warning : Muted);
		favorite.Click += async delegate(object sender, RoutedEventArgs e)
		{
			e.Handled = true;
            await ToggleProfileFavoriteAsync(profile,favorite);
		};
		Grid.SetColumn(favorite, 5);
		grid.Children.Add(favorite);
		Button button = VectorIconButton("trash", "Удалить", Danger);
		button.ToolTip = "Удалить";
		button.Click += delegate(object sender, RoutedEventArgs e)
		{
			e.Handled = true;
            DeleteProfile(profile);
		};
		Grid.SetColumn(button, 6);
        var more = SmallButton("⋯"); more.MinWidth = 0; more.Width = 34; more.Padding = new Thickness(0); more.FontSize = 22; more.ToolTip = "Действия сервера";
        AutomationProperties.SetName(more,"Действия сервера " + profile.Name);
        var menu = new ContextMenu { Background = new SolidColorBrush(Surface), Foreground = new SolidColorBrush(TextColor), BorderBrush = new SolidColorBrush(BorderColor), FontFamily = VisualDesign.InterfaceFont(state.Visual), FontSize = 13 * state.Visual.TextScale };
        more.ContextMenu = menu;
        var rename = new MenuItem { Header = "Переименовать" }; rename.Click += delegate { edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        var remove = new MenuItem { Header = "Удалить профиль", Foreground = new SolidColorBrush(Danger) }; remove.Click += delegate { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        AddVpnRoleMenuItems(menu, profile);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(rename);menu.Items.Add(new Separator());menu.Items.Add(remove);
        more.Click += delegate(object sender,RoutedEventArgs e) { e.Handled=true; menu.PlacementTarget=more;menu.IsOpen=true; };
        Grid.SetColumn(more,6);grid.Children.Add(more);
		border2.Child = grid;
        border2.Focusable = true;
        AutomationProperties.SetName(border2, "Выбрать сервер " + profile.Name);
        border2.KeyDown += async delegate(object sender, KeyEventArgs e) {
            if (e.OriginalSource == border2 && (e.Key == Key.Enter || e.Key == Key.Space)) {
                e.Handled = true;
                await ChooseProfileRoleAsync(profile);
            }
        };
        border2.MouseLeftButtonUp += async delegate(object sender, MouseButtonEventArgs e) {
            if (e.Handled) return;
            DependencyObject source = e.OriginalSource as DependencyObject;
            while (source != null && source != border2) {
                if (source is System.Windows.Controls.Primitives.ButtonBase) return;
                source = VisualTreeHelper.GetParent(source);
            }
            e.Handled = true;
            await ChooseProfileRoleAsync(profile);
        };
		EnableCardMotion(border2);
		return border2;
	}

	private UIElement ServerGridCard(VpnProfile profile)
	{
		bool selected = state.SelectedProfileId == profile.Id;
		Border card = new Border { Width = 398, MinHeight = 174, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(17), CornerRadius = Radius(16), Background = new SolidColorBrush(selected ? C("#1C1D30") : Surface), BorderBrush = new SolidColorBrush(selected ? Accent : BorderColor), BorderThickness = new Thickness(selected ? 1.4 : 1), Cursor = Cursors.Hand };
		Grid grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		top.Children.Add(RegionMark(profile)); StackPanel copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; copy.Children.Add(T(profile.Name, 13.5, TextColor, FontWeights.SemiBold)); copy.Children.Add(T(ServerGroupingService.CountryLabel(profile) + " · " + profile.ProtocolLabel, 9.5, Muted)); Grid.SetColumn(copy, 1); top.Children.Add(copy);
		Button favorite = VectorIconButton("star", "Избранное", profile.IsFavorite ? Warning : Muted, profile.IsFavorite); favorite.Click += async delegate(object sender, RoutedEventArgs e) { e.Handled = true; await ToggleProfileFavoriteAsync(profile,favorite); }; Grid.SetColumn(favorite, 2); top.Children.Add(favorite); grid.Children.Add(top);
		Grid metrics = new Grid { Margin = new Thickness(0, 14, 0, 0) }; metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition());
		metrics.Children.Add(Metric("ЗАДЕРЖКА", profile.LastLatency > 0 ? profile.LastLatency + " мс" : "—", LatencyColor(profile.LastLatency), 0)); metrics.Children.Add(Metric("ПРОВЕРКА", profile.HealthMeasurement == "https-profile" ? profile.LastCheckedUtc.ToLocalTime().ToString("t") : "—", Muted, 1)); metrics.Children.Add(Metric("СТАТУС", profile.HealthStatus ?? "Не проверен", profile.ConsecutiveFailures > 0 ? Danger : profile.LastLatency > 0 ? Green : Muted, 2)); Grid.SetRow(metrics, 1); grid.Children.Add(metrics); card.Child = grid;
        if (state.Routing?.MultiServer != null)
        {
            var roles = SmallButton("Роль VPN");
            var menu = new ContextMenu();
            AddVpnRoleMenuItems(menu, profile);
            roles.ContextMenu = menu;
            roles.Click += (sender, args) => { args.Handled = true; menu.PlacementTarget = roles; menu.IsOpen = true; };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(roles, 2);
            grid.Children.Add(roles);
        }
        VisualDesign.ApplyGlassSurface(card, state.Visual);
        card.MouseLeftButtonUp += async delegate(object sender, MouseButtonEventArgs args)
        {
            if (args.Handled) return;
            for (var source = args.OriginalSource as DependencyObject; source != null && source != card;
                source = VisualTreeHelper.GetParent(source))
                if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            await ChooseProfileRoleAsync(profile);
        }; EnableCardMotion(card); return card;
	}

	private UIElement BuildRoutingPage()
	{
        if (state.Routing.MultiServer != null) return BuildMultiServerRoutingPage();
		Grid grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(18.0)
		});
		grid.RowDefinitions.Add(new RowDefinition());
		Button importRules = SmallButton("Импорт списка");
		importRules.IsEnabled = state.Zapret?.RoutingChangesApplied != true;
		if (!importRules.IsEnabled) importRules.ToolTip = "Отключите Zapret, прежде чем менять списки маршрутизации.";
		importRules.Click += delegate
		{
			OpenFileDialog file = new OpenFileDialog { Filter = "Списки доменов (*.txt;*.list)|*.txt;*.list|Все файлы (*.*)|*.*" };
			if (file.ShowDialog(this) != true) return;
			TextPromptWindow target = new TextPromptWindow("Куда импортировать", "Введите proxy (через VPN), direct (напрямую) или block (блокировать)", "proxy", state.Visual) { Owner = this };
			if (target.ShowDialog() != true) return;
			List<string> destination = target.Value.Equals("direct", StringComparison.OrdinalIgnoreCase) ? state.Routing.DirectDomains : target.Value.Equals("block", StringComparison.OrdinalIgnoreCase) ? state.Routing.BlockDomains : target.Value.Equals("proxy", StringComparison.OrdinalIgnoreCase) ? state.Routing.ProxyDomains : null;
			if (destination == null)
			{
				Toast("Укажите proxy, direct или block.", Danger);
				return;
			}
			List<string> imported = RoutingListImporter.Parse(File.ReadAllLines(file.FileName));
			foreach (string domain in imported)
			{
				if (!destination.Contains(domain, StringComparer.OrdinalIgnoreCase)) destination.Add(domain);
			}
			StateStore.Save(state);
			Toast("Импортировано доменов: " + imported.Count, Green);
			Navigate("routing");
		};
		var routingTransfers = new WrapPanel { Orientation = Orientation.Horizontal };
        routingTransfers.Children.Add(SettingsImportButton("routing"));
        routingTransfers.Children.Add(SettingsExportButton());
        var assignments=SmallButton("Серверы для приложений и сайтов");assignments.Name="EditMultiServerRouting";
        assignments.IsEnabled=CanEditServerAssignments();
        assignments.ToolTip="Настройка конкретных серверов с проверкой и переподключением.";
        assignments.Click+=async delegate{await EditMultiServerRoutingAsync();};routingTransfers.Children.Add(assignments);
        if(state.MultiServerRoutingHistory!=null) {
            var resume=SmallButton("Включить сохранённые назначения");resume.Name="ResumeMultiServerRouting";resume.IsEnabled=CanEditServerAssignments();
            resume.Click+=async delegate{await EditMultiServerRoutingAsync(true);};routingTransfers.Children.Add(resume);
            var forget=SmallButton("Удалить сохранённые назначения");forget.Name="ForgetMultiServerRouting";forget.IsEnabled=CanEditServerAssignments();
            forget.Click+=delegate{try{MultiServerRoutingCommitPlan.PrepareForgetHistory(state).Apply(()=>CanEditServerAssignments(),StateStore.Save);RefreshCurrentPage();}
                catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}};routingTransfers.Children.Add(forget);
        }
        importRules.Margin = new Thickness(8, 0, 0, 0);
        routingTransfers.Children.Add(importRules);
        grid.Children.Add(PageHeader("Маршрутизация", "Что направлять через VPN", routingTransfers, null));
		StackPanel stackPanel = new StackPanel();
        if(MultiServerRoutingHistory.MissingRestoredProfile(state)) {
            var missing=T("Прежний сервер недоступен. Правила восстановлены; выберите сервер вручную перед подключением.",12,Warning);
            missing.Name="RestoredServerUnavailable";missing.TextWrapping=TextWrapping.Wrap;stackPanel.Children.Add(missing);
        }
        bool migratedRouting = state.Routing.MigratedPolicy != null;
        if (migratedRouting || state.RoutingMigrationBackup != null)
        {
            var migration = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var info = T(migratedRouting ? "Применяются перенесённые правила в исходном порядке. Обычные списки ниже временно не используются." : "Доступна предыдущая маршрутизация для отмены последнего переноса.", 12, palette.Text);
            info.Name = "MigratedRoutingNotice"; info.TextWrapping = TextWrapping.Wrap; migration.Children.Add(info);
            var undo = SmallButton(state.RoutingMigrationBackup != null ? "Отменить перенос правил" : "Вернуться к обычным правилам"); undo.Name = "UndoRoutingMigration"; undo.IsEnabled = !snapshotMode;
            undo.Click += delegate {
                try {
                    string expected = RoutingMigrationService.Fingerprint(state.Routing);
                    if (state.RoutingMigrationBackup != null) RoutingMigrationService.Undo(state, expected, CanChangeMigratedRouting, StateStore.Save);
                    else RoutingMigrationService.UseLocalRules(state, expected, CanChangeMigratedRouting, StateStore.Save);
                    Navigate("routing"); Toast("Маршрутизация восстановлена. Подключение не запускалось.", Green);
                } catch (Exception error) { Toast(LogSanitizer.Sanitize(error.Message), Warning); }
            }; migration.Children.Add(undo); stackPanel.Children.Add(migration);
        }
        if (migratedRouting) importRules.IsEnabled = false;
		stackPanel.Children.Add(SectionTitle("РЕЖИМ"));
		Grid grid2 = new Grid();
		grid2.Margin = new Thickness(0.0, 10.0, 0.0, 22.0);
		Grid grid3 = grid2;
		grid3.ColumnDefinitions.Add(new ColumnDefinition());
		grid3.ColumnDefinitions.Add(new ColumnDefinition());
		grid3.ColumnDefinitions.Add(new ColumnDefinition());
		grid3.Children.Add(ModeCard("Global", "Весь трафик", "Всё через VPN", "globe", 0));
		grid3.Children.Add(ModeCard("Smart", "По правилам", "Гибкое разделение", "routing", 1));
		grid3.Children.Add(ModeCard("Direct", "Напрямую", "VPN только для списка", "home", 2));
		stackPanel.Children.Add(grid3);
		StackPanel presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0.0, 0.0, 0.0, 18.0) };
		presets.Children.Add(PresetButton("Мессенджеры через VPN", "messengers"));
		presets.Children.Add(PresetButton("Приватность", "privacy"));
		presets.Children.Add(PresetButton("Совместимость", "compatibility"));
		stackPanel.Children.Add(presets);
		TextBlock priority = T("Приоритет: локальная сеть → блокировка → TCP/UDP → путь приложения → имя приложения → домен → по умолчанию. Zapret перемещает свои маршруты в список «Напрямую».", 9.5, Muted);
		priority.TextWrapping = TextWrapping.Wrap;
		priority.Margin = new Thickness(2.0, 0.0, 0.0, 14.0);
		stackPanel.Children.Add(priority);
		Grid grid4 = new Grid();
		grid4.ColumnDefinitions.Add(new ColumnDefinition());
		grid4.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(14.0)
		});
		grid4.ColumnDefinitions.Add(new ColumnDefinition());
		StackPanel stackPanel2 = new StackPanel();
		stackPanel2.Children.Add(RoutingToggleCard("Обходить локальную сеть", "Принтеры, NAS и роутер останутся доступны", "lan"));
		stackPanel2.Children.Add(RoutingToggleCard("Блокировать рекламу", "Базовый список рекламных доменов", "ads"));
		stackPanel2.Children.Add(RoutingToggleCard("IPv6", "Использовать IPv6 внутри туннеля", "ipv6"));
		stackPanel2.Children.Add(RoutingToggleCard("Строгий маршрут", "Защищает от обхода туннеля и утечек DNS", "strict"));
		stackPanel2.Children.Add(RoutingToggleCard("Определять протоколы", "Надёжнее сопоставлять домены с правилами", "sniff"));
		if (string.Equals(state.Routing.Mode, "Smart", StringComparison.OrdinalIgnoreCase))
		{
			stackPanel2.Children.Add(ChoiceCard("По умолчанию через VPN", "Если ни одно правило не совпало", state.Routing.DefaultRoute == "Proxy", delegate
			{
				state.Routing.DefaultRoute = "Proxy";
				StateStore.Save(state);
				Navigate("routing");
			}));
			stackPanel2.Children.Add(ChoiceCard("По умолчанию напрямую", "Если ни одно правило не совпало", state.Routing.DefaultRoute == "Direct", delegate
			{
				state.Routing.DefaultRoute = "Direct";
				StateStore.Save(state);
				Navigate("routing");
			}));
		}
		grid4.Children.Add(stackPanel2);
		StackPanel stackPanel3 = new StackPanel();
		stackPanel3.Children.Add(RouteEditorCard("Через VPN", "Домены, которые всегда идут через VPN", state.Routing.ProxyDomains, Accent, "proxy-domains"));
		stackPanel3.Children.Add(RouteEditorCard("Напрямую", "Строго без VPN: сайт, поддомены и DNS. Отключите защищённый DNS браузера; сторонние CDN добавляйте отдельно.", state.Routing.DirectDomains, Teal, "direct-domains"));
		stackPanel3.Children.Add(RouteEditorCard("Блокировать", "Домены без доступа к сети", state.Routing.BlockDomains, Danger, "block-domains"));
		Grid.SetColumn(stackPanel3, 2);
		grid4.Children.Add(stackPanel3);
		if (responsiveMode != "Wide" || state.Visual.TextScale > 1.15)
		{
			grid4.ColumnDefinitions.Clear();
			grid4.ColumnDefinitions.Add(new ColumnDefinition());
			grid4.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid4.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetColumn(stackPanel3, 0);
			Grid.SetRow(stackPanel3, 1);
			stackPanel3.Margin = new Thickness(0, 14, 0, 0);
		}
		stackPanel.Children.Add(grid4);
		TourAnchor("routing.domains", stackPanel3);
		stackPanel.Children.Add(SectionTitle("ДОПОЛНИТЕЛЬНЫЕ ПРАВИЛА ДЛЯ ПРИЛОЖЕНИЙ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		Grid grid5 = new Grid();
		grid5.ColumnDefinitions.Add(new ColumnDefinition());
		grid5.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(14.0)
		});
		grid5.ColumnDefinitions.Add(new ColumnDefinition());
		grid5.Children.Add(RouteEditorCard("Приложения через VPN", "Например: discord.exe", state.Routing.ProxyProcesses, Accent, "proxy-apps"));
		Border element = RouteEditorCard("Приложения напрямую", "Например: steam.exe", state.Routing.DirectProcesses, Teal, "direct-apps");
		Grid.SetColumn(element, 2);
		grid5.Children.Add(element);
		if (responsiveMode != "Wide" || state.Visual.TextScale > 1.15)
		{
			grid5.ColumnDefinitions.Clear();
			grid5.ColumnDefinitions.Add(new ColumnDefinition());
			grid5.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetColumn(element, 0);
			Grid.SetRow(element, 1);
			element.Margin = new Thickness(0, 0, 0, 10);
		}
		stackPanel.Children.Add(grid5);
		stackPanel.Children.Add(SectionTitle("ПОЛНЫЕ ПУТИ ПРИЛОЖЕНИЙ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		Grid pathGrid = new Grid();
		pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
		pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14.0) });
		pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
		pathGrid.Children.Add(RouteEditorCard("Пути через VPN", "Например: C:\\Apps\\Browser\\browser.exe", state.Routing.ProxyProcessPaths, Accent, "proxy-paths"));
		Border directPaths = RouteEditorCard("Пути напрямую", "Поддерживаются переменные %LOCALAPPDATA%", state.Routing.DirectProcessPaths, Teal, "direct-paths");
		Grid.SetColumn(directPaths, 2);
		pathGrid.Children.Add(directPaths);
		if (responsiveMode != "Wide" || state.Visual.TextScale > 1.15)
		{
			pathGrid.ColumnDefinitions.Clear();
			pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
			pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetColumn(directPaths, 0);
			Grid.SetRow(directPaths, 1);
			directPaths.Margin = new Thickness(0, 0, 0, 10);
		}
		stackPanel.Children.Add(pathGrid);
		stackPanel.Children.Add(SectionTitle("TCP / UDP И ПОРТЫ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel.Children.Add(RouteEditorCard("Расширенные правила", "Формат: proxy|tcp|443 или block|udp|443,1000:2000", state.Routing.AdvancedRules, Warning, "advanced"));
		List<string> routeWarnings = RoutingInspector.Validate(state.Routing);
		if (routeWarnings.Count > 0)
		{
			TextBlock warning = T("⚠ " + routeWarnings[0] + " Откройте «Диагностика» для подробностей.", 10.0, Warning);
			warning.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
			warning.TextWrapping = TextWrapping.Wrap;
			stackPanel.Children.Add(warning);
		}
		var entryHint = T("Подсказка: домен *.example.com включает все его поддомены. Для каждого правила — отдельная ячейка; новые ячейки добавляются кнопкой «Добавить».", 10.5, Muted);
		entryHint.TextWrapping = TextWrapping.Wrap;
		stackPanel.Children.Add(entryHint);
        if (migratedRouting) foreach (UIElement child in stackPanel.Children.Cast<UIElement>().Skip(1)) child.IsEnabled = false;
		ScrollViewer scrollViewer = TouchScrollViewer(new ScrollViewer());
		scrollViewer.Content = stackPanel;
		scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
		ScrollViewer element2 = scrollViewer;
		Grid.SetRow(element2, 2);
		grid.Children.Add(element2);
		return grid;
	}

	private UIElement BuildApplicationRoutingSection()
	{
		Border card = new Border
		{
			Padding = new Thickness(12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0, 0, 28, 0), Background = new SolidColorBrush(Surface),
			BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1),
			CornerRadius = Radius(14)
		};
		StackPanel content = new StackPanel();
		var routingHint = T("Перетащите переключатель: слева — напрямую, по центру — по умолчанию, справа — VPN.", 12, TextColor);
        routingHint.TextWrapping = TextWrapping.Wrap; routingHint.TextTrimming = TextTrimming.None; content.Children.Add(routingHint);
		TextBlock explanation = T("Правило задаётся по полному пути к .exe. Пока Zapret включён, домены Zapret и приложения из VPN-маршрутов перемещаются в «Напрямую»; при выключении Zapret VPN-маршруты восстанавливаются.", 9.5, Muted);
		explanation.TextWrapping = TextWrapping.Wrap;
		explanation.Margin = new Thickness(0, 4, 0, 12);
		content.Children.Add(explanation);
		WrapPanel toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
		TextBox search = new TextBox
		{
			Width = 230, Height = 29, Margin = new Thickness(0, 0, 8, 6),
			Padding = new Thickness(8, 4, 8, 4), Background = new SolidColorBrush(Surface2),
			Foreground = new SolidColorBrush(TextColor), BorderBrush = new SolidColorBrush(BorderColor),
			ToolTip = "Поиск по названию и пути приложения"
		};
		toolbar.Children.Add(search);
		string[] filterOptions = { "Все", "VPN", "Напрямую", "По умолчанию" };
		int filterIndex = 0;
		Button filter = SmallButton("Все");
		filter.Width = 116;
		filter.Margin = new Thickness(0, 0, 8, 6);
		filter.ToolTip = "Фильтр списка: все, через VPN, напрямую или по умолчанию";
		toolbar.Children.Add(filter);
		Button add = SmallButton("Добавить .exe");
		add.Margin = new Thickness(0, 0, 8, 6);
		toolbar.Children.Add(add);
		Button refresh = SmallButton("Обновить список");
		refresh.Margin = new Thickness(0, 0, 8, 6);
		toolbar.Children.Add(refresh);
		content.Children.Add(toolbar);
		TextBlock status = T("", 9.5, Muted);
		status.Margin = new Thickness(0, 0, 0, 8);
		content.Children.Add(status);
		ListBox results = new ListBox
		{
			Height = 250, Background = new SolidColorBrush(Surface2),
			BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1),
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
		VirtualizingStackPanel.SetIsVirtualizing(results, true);
		ScrollViewer.SetCanContentScroll(results, true);
		ScrollViewer.SetPanningMode(results, PanningMode.VerticalOnly);
		results.PreviewMouseWheel += (s, e) => SmoothScrollBehavior.ForwardToPage(results, e);
		content.Children.Add(results);
		var listHint = T("Колесо — вся страница; Shift + колесо — список программ. Маршрут меняется перетаскиванием.", 9.5, Muted);
		listHint.TextWrapping = TextWrapping.Wrap;
		content.Children.Add(listHint);
		Button apply = SmallButton("Применить маршруты");
		apply.Margin = new Thickness(0, 10, 0, 0);
		apply.Visibility = applicationRoutingPending && core.Status == CoreStatus.Connected ? Visibility.Visible : Visibility.Collapsed;
		apply.Click += async delegate { await ApplyApplicationRoutingAsync(); };
		content.Children.Add(apply);
		card.Child = content;

		int renderRevision = 0;
		Action render = null;
		render = async delegate
		{
			int revision = ++renderRevision;
			results.Items.Clear();
			if (applicationCatalog == null)
			{
				status.Text = "Поиск установленных и запущенных программ…";
				return;
			}
			string query = search.Text.Trim();
			string selectedFilter = filterOptions[filterIndex];
			IEnumerable<ApplicationEntry> matches = applicationCatalog.Where(item =>
				(query.Length == 0 || item.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
					|| item.ExecutablePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
				&& (selectedFilter == "Все" || RouteLabel(ApplicationRoutingService.GetRoute(state.Routing, item.ExecutablePath)) == selectedFilter));
			int total = matches.Count();
			int added = 0;
			foreach (ApplicationEntry item in matches.Take(150).ToArray())
			{
				if (revision != renderRevision || currentPage != "routing") return;
				results.Items.Add(BuildApplicationRoutingRow(item, render));
				if (++added % 8 == 0) await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
			}
			status.Text = total == 0 ? "Программы не найдены. Можно выбрать .exe вручную."
				: "Показано " + Math.Min(total, 150) + " из " + total + " программ" + (applicationRoutingPending && core.Status == CoreStatus.Connected ? " · изменения ожидают применения" : "");
			apply.Visibility = applicationRoutingPending && core.Status == CoreStatus.Connected ? Visibility.Visible : Visibility.Collapsed;
		};
		applicationListRefresh = render;
		search.TextChanged += delegate { render(); };
		filter.Click += delegate { filterIndex = (filterIndex + 1) % filterOptions.Length; filter.Content = filterOptions[filterIndex]; render(); };
		add.Click += delegate
		{
			OpenFileDialog picker = new OpenFileDialog { Filter = "Приложения (*.exe)|*.exe", CheckFileExists = true };
			if (picker.ShowDialog(this) != true) return;
			try
			{
				ApplicationEntry entry = ApplicationCatalog.FromFile(picker.FileName);
				if (applicationCatalog == null) applicationCatalog = new List<ApplicationEntry>();
				applicationCatalog.RemoveAll(item => ApplicationRoutingService.SamePath(item.ExecutablePath, entry.ExecutablePath));
				applicationCatalog.Add(entry);
				applicationCatalog = applicationCatalog.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
				search.Text = entry.Name;
				filterIndex = 0;
				filter.Content = filterOptions[filterIndex];
				render();
			}
			catch (Exception ex) { Toast("Не удалось добавить приложение: " + LogSanitizer.Sanitize(ex.Message), Warning); }
		};
		refresh.Click += delegate { applicationCatalog = null; render(); _ = LoadApplicationCatalogAsync(); };
		render();
		if (applicationCatalog == null) _ = LoadApplicationCatalogAsync();
		return card;
	}

	private Action applicationListRefresh;
	private bool applicationCatalogLoading;

	private async Task LoadApplicationCatalogAsync()
	{
		if (applicationCatalogLoading) return;
		applicationCatalogLoading = true;
		try
		{
			string[] saved = (state.Routing.DirectProcessPaths ?? new List<string>())
				.Concat(state.Routing.ProxyProcessPaths ?? new List<string>()).ToArray();
			List<ApplicationEntry> discovered = await Task.Run(() => ApplicationCatalog.Discover(saved));
			// Shell icon extraction may block on disk/shell extensions; never run it on the UI thread.
			var icons = await Task.Run(() => discovered.Take(150).ToDictionary(item => item.ExecutablePath, item => ExtractApplicationIcon(item.ExecutablePath), StringComparer.OrdinalIgnoreCase));
			foreach (var icon in icons) applicationIconCache[icon.Key] = icon.Value;
			if (applicationCatalog != null)
			foreach (ApplicationEntry item in applicationCatalog)
			{
				if (!discovered.Any(found => ApplicationRoutingService.SamePath(found.ExecutablePath, item.ExecutablePath))) discovered.Add(item);
			}
			applicationCatalog = discovered.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
		catch (Exception ex)
		{
			applicationCatalog = new List<ApplicationEntry>();
			Toast("Не удалось загрузить список программ: " + LogSanitizer.Sanitize(ex.Message), Warning);
		}
		finally
		{
			applicationCatalogLoading = false;
			if (currentPage == "routing") applicationListRefresh?.Invoke();
		}
	}

	private static string RouteLabel(string route)
	{
		return route == ApplicationRoutingService.Proxy ? "VPN" : route == ApplicationRoutingService.Direct ? "Напрямую" : "По умолчанию";
	}

	private ListBoxItem BuildApplicationRoutingRow(ApplicationEntry entry, Action refresh)
	{
		string route = ApplicationRoutingService.GetRoute(state.Routing, entry.ExecutablePath);
		Grid row = new Grid { MinHeight = 60, Margin = new Thickness(8, 7, 8, 7), HorizontalAlignment = HorizontalAlignment.Stretch };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
		row.ColumnDefinitions.Add(new ColumnDefinition());
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 72 });
		ImageSource icon = GetApplicationIcon(entry.ExecutablePath);
		if (icon != null) row.Children.Add(new Image { Source = icon, Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
		else row.Children.Add(IconFactory.Create("servers", 23, Muted));
		StackPanel description = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		TextBlock title = T(entry.Name, 12.5, TextColor, FontWeights.SemiBold);
		title.TextTrimming = TextTrimming.CharacterEllipsis;
		description.Children.Add(title);
		TextBlock path = T(entry.ExecutablePath, 10.5, Muted);
		path.TextTrimming = TextTrimming.CharacterEllipsis;
		path.ToolTip = entry.ExecutablePath;
		description.Children.Add(path);
		Grid.SetColumn(description, 1);
		row.Children.Add(description);
		StackPanel control = new StackPanel { MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
		TextBlock label = T(RouteLabel(route), 9.0, route == ApplicationRoutingService.Proxy ? AccentLight : route == ApplicationRoutingService.Direct ? Teal : Muted, FontWeights.SemiBold);
		label.HorizontalAlignment = HorizontalAlignment.Center;
		control.Children.Add(label);
		Color routeColor = route == ApplicationRoutingService.Proxy ? Accent : route == ApplicationRoutingService.Direct ? Teal : Surface2;
		Border track = CreatePillSwitch(routeColor, route == ApplicationRoutingService.Automatic ? BorderColor : routeColor,
			route == ApplicationRoutingService.Proxy ? HorizontalAlignment.Right : route == ApplicationRoutingService.Direct ? HorizontalAlignment.Left : HorizontalAlignment.Center, 50, 26);
		Button toggle = new DragRouteSwitch(track, route, next =>
		{
			ApplicationRoutingService.SetRoute(state.Routing, entry.ExecutablePath, next);
			StateStore.Save(state);
			applicationRoutingPending = ApplicationRoutingNeedsApply();
			Dispatcher.BeginInvoke(refresh);
		})
		{
			Content = track, Width = 64, Height = 48, BorderThickness = new Thickness(0),
			Background = Brushes.Transparent, Cursor = Cursors.SizeWE, Padding = new Thickness(0),
			ToolTip = state.Zapret?.RoutingChangesApplied == true
				? "Отключите Zapret, чтобы менять маршруты приложений. Текущие VPN-маршруты временно направлены напрямую."
				: "Перетащите: слева — напрямую, центр — по умолчанию, справа — VPN. Клавиатура: ← →, Home, End.",
			IsEnabled = state.Zapret?.RoutingChangesApplied != true
		};
		// A bare presenter avoids the square native Button chrome around the switch.
		var switchRoot = new FrameworkElementFactory(typeof(Border)); switchRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		var switchPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
		switchPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		switchPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); switchRoot.AppendChild(switchPresenter);
		toggle.Template = new ControlTemplate(typeof(Button)) { VisualTree = switchRoot };
		toggle.FocusVisualStyle = null;
		toggle.GotKeyboardFocus += (s, e) => track.BorderBrush = new SolidColorBrush(Accent);
		toggle.LostKeyboardFocus += (s, e) => track.BorderBrush = new SolidColorBrush(BorderColor);
		System.Windows.Automation.AutomationProperties.SetName(toggle, entry.Name + " · " + RouteLabel(route));
		toggle.MouseEnter += (s, e) => track.Opacity = 0.85;
		toggle.MouseLeave += (s, e) => track.Opacity = 1;
		control.Children.Add(toggle);
		Grid.SetColumn(control, 2);
		row.Children.Add(control);
		return new ListBoxItem
		{
			Content = row,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			ToolTip = entry.Source + " · " + entry.ExecutablePath,
			BorderThickness = new Thickness(0),
			Background = Brushes.Transparent
		};
	}

	private ImageSource GetApplicationIcon(string path)
	{
		if (applicationIconCache.TryGetValue(path, out ImageSource cached)) return cached;
		return null;
	}

	private static ImageSource ExtractApplicationIcon(string path)
	{
		ImageSource source = null;
		try
		{
			if (File.Exists(path))
			using (Drawing.Icon icon = Drawing.Icon.ExtractAssociatedIcon(path))
			{
				if (icon != null)
				{
					BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
					bitmap.Freeze();
					source = bitmap;
				}
			}
		}
		catch (Exception) { }
		return source;
	}

    private UIElement BuildMultiServerRoutingPage()
    {
        var layout=new DockPanel();var actions=new WrapPanel();
        actions.Children.Add(SettingsImportButton("routing"));actions.Children.Add(SettingsExportButton());
        var edit=SmallButton("Назначения серверов");edit.Name="EditMultiServerRouting";edit.IsEnabled=CanEditServerAssignments();
        edit.ToolTip="Применение проверит конфигурацию и переподключит VPN.";edit.Click+=async delegate{await EditMultiServerRoutingAsync();};actions.Children.Add(edit);
        var disable=SmallButton(state.MultiServerRoutingHistory?.SingleServerRouting!=null?"Вернуть прежнюю маршрутизацию":"Отключить назначения");
        disable.Name="DisableMultiServerRouting";disable.IsEnabled=CanEditServerAssignments();
        disable.ToolTip="Назначения сохранятся для повторного включения. Активное подключение будет переподключено после проверки.";
        disable.Click+=async delegate{try{await ApplyServerAssignmentsAsync(state,null,true);}catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}};actions.Children.Add(disable);
        var inspect=SmallButton("Проверить маршрут");inspect.Click+=delegate{Navigate("diagnostics");};actions.Children.Add(inspect);
        var header = new StackPanel();
        header.Children.Add(PageHeader("Маршрутизация", "Сайты → приложения → основной сервер", null, null));
        actions.Margin = new Thickness(0, 10, 0, 0);
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var content = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var routing = state.Routing;
        var settings = routing.MultiServer;
        var primary = state.Profiles.FirstOrDefault(profile => profile.Id == settings.DefaultProfileId);
        var secondary = state.Profiles.FirstOrDefault(profile => profile.Id == settings.SecondaryProfileId);
        if (settings.RequiresRebinding)
        {
            var warning = T("Импортированные назначения требуют привязки к серверам этого компьютера.", 12, Warning);
            warning.TextWrapping = TextWrapping.Wrap;
            warning.Margin = new Thickness(0, 0, 0, 10);
            content.Children.Add(warning);
        }

        bool GoesThroughPrimary(MultiServerRuleRecord rule) => rule.Target == "Default" ||
            (rule.Target == "Profile" && rule.ProfileId == settings.DefaultProfileId);
        bool GoesThroughSecondary(MultiServerRuleRecord rule) => settings.SecondaryProfileId != null &&
            rule.Target == "Profile" && rule.ProfileId == settings.SecondaryProfileId;
        IEnumerable<string> Sites(Func<MultiServerRuleRecord, bool> filter) =>
            settings.Rules.Where(rule => rule.Domain != null && filter(rule)).Select(rule => rule.DomainLabel);
        IEnumerable<string> Apps(Func<MultiServerRuleRecord, bool> filter) =>
            settings.Rules.Where(rule => rule.ApplicationPath != null && filter(rule)).Select(rule => rule.ApplicationPath);
        string ServerCaption(VpnProfile profile, string unavailable) =>
            profile == null ? unavailable : profile.Name + (profile.RetiredFromSubscription ? " · недоступен" : "");

        var sitesDirect = RoutingViewCard("Сайты напрямую", "Действующие правила без VPN", Sites(rule => rule.Target == "Direct"), Teal, "RoutingSitesDirect");
        content.Children.Add(TourAnchor("routing.domains", sitesDirect));
        content.Children.Add(RoutingViewCard("Сайты через основной VPN", ServerCaption(primary, "Основной сервер недоступен"),
            Sites(GoesThroughPrimary), Accent, "RoutingSitesPrimary"));
        content.Children.Add(RoutingViewCard("Сайты через VPN-2", ServerCaption(secondary, settings.SecondaryProfileId == null ? "VPN-2 не выбран" : "VPN-2 недоступен"),
            Sites(GoesThroughSecondary), Accent, "RoutingSitesSecondary"));
        content.Children.Add(RoutingViewCard("Приложения напрямую", "Правила по пути и имени .exe",
            Apps(rule => rule.Target == "Direct").Concat(routing.DirectProcesses), Teal, "RoutingAppsDirect"));
        content.Children.Add(RoutingViewCard("Приложения через основной VPN", ServerCaption(primary, "Основной сервер недоступен"),
            Apps(GoesThroughPrimary).Concat(routing.ProxyProcesses), Accent, "RoutingAppsPrimary"));
        content.Children.Add(RoutingViewCard("Приложения через VPN-2", ServerCaption(secondary, settings.SecondaryProfileId == null ? "VPN-2 не выбран" : "VPN-2 недоступен"),
            Apps(GoesThroughSecondary), Accent, "RoutingAppsSecondary"));

        var blocked = Sites(rule => rule.Target == "Block").Concat(routing.BlockDomains).Concat(Apps(rule => rule.Target == "Block")).ToArray();
        if (blocked.Length != 0)
            content.Children.Add(RoutingViewCard("Блокировать", "Заблокированные сайты и приложения", blocked, Danger, "RoutingBlocked"));
        var other = settings.Rules.Where(rule => rule.Target == "Profile" && !GoesThroughPrimary(rule) && !GoesThroughSecondary(rule))
            .Select(rule => (rule.Domain == null ? rule.ApplicationPath : rule.DomainLabel) + " → " +
                (state.Profiles.FirstOrDefault(profile => profile.Id == rule.ProfileId)?.Name ?? "Сервер недоступен")).ToArray();
        if (other.Length != 0)
            content.Children.Add(RoutingViewCard("Другие назначения", "Правила для отдельных серверов", other, Accent, "RoutingOther"));
        if (routing.ZapretDirectDomains.Count != 0)
            content.Children.Add(RoutingViewCard("Сайты Zapret", "Напрямую, если нет явного назначения или блокировки",
                routing.ZapretDirectDomains, Teal, "RoutingZapret"));
        if (routing.AdvancedRules.Count != 0)
            content.Children.Add(RouteEditorCard("Расширенные правила", "Правила TCP и UDP", routing.AdvancedRules, Warning, "advanced"));

        multiExitHealthPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        content.Children.Add(multiExitHealthPanel);
        UpdateMultiExitHealthPanel();
        layout.Children.Add(TouchScrollViewer(new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }));
        return layout;
    }

    private Border RoutingViewCard(string title, string caption, IEnumerable<string> values, Color accent, string name)
    {
        var rows = new StackPanel();
        foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var row = T(value, 12, TextColor);
            row.TextWrapping = TextWrapping.Wrap;
            rows.Children.Add(new Border { Child = row, Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 0, 0, 6), Background = new SolidColorBrush(palette.Input),
                BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), ToolTip = value });
        }
        if (rows.Children.Count == 0)
            rows.Children.Add(T("Нет правил.", 11, Muted));
        var scroll = new ScrollViewer { Content = rows, MaxHeight = 220 * state.Visual.TextScale,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.PreviewMouseWheel += (sender, args) => SmoothScrollBehavior.ForwardToPage(scroll, args);
        var card = RouteCard(title, caption, accent, scroll);
        card.Name = name;
        return card;
    }
    private StackPanel multiExitHealthPanel;
    private void UpdateMultiExitHealthPanel()
    {
        if(multiExitHealthPanel==null)return;
        multiExitHealthPanel.Children.Clear();
        if(core.Status!=CoreStatus.Connected||core.LastConnectedRouting?.MultiServer==null)return;
        multiExitHealthPanel.Children.Add(T("Проверка активного подключения",14,TextColor));
        if (core.HasMultiServerCatalogChanges(state.Profiles))
        {
            var notice = T("Параметры назначенных серверов изменились. VPN использует прежний снимок до переподключения.", 12, Warning);
            notice.Name = "MultiServerCatalogChanged";
            notice.TextWrapping = TextWrapping.Wrap;
            multiExitHealthPanel.Children.Add(notice);
            var apply = SmallButton("Применить изменения серверов");
            apply.Name = "ApplyMultiServerCatalog";
            apply.IsEnabled = CanEditServerAssignments() && state.Routing?.MultiServer != null;
            apply.Click += async delegate
            {
                if (!CanEditServerAssignments() || state.Routing?.MultiServer == null || !core.HasMultiServerCatalogChanges(state.Profiles)) return;
                try { await ApplyServerAssignmentsAsync(state, state.Routing); }
                catch (Exception error) { Toast(LogSanitizer.Sanitize(error.Message), Warning); }
            };
            multiExitHealthPanel.Children.Add(apply);
        }
        var group=core.Verification.MultiExits;
        if(group==null){multiExitHealthPanel.Children.Add(T("Проверяем каждый назначенный сервер…",12,Muted));return;}
        foreach(var exit in group.Results) {
            string status=exit.Label(DateTime.UtcNow);
            var current=state.Profiles.FirstOrDefault(p=>p.Id==exit.Exit.ProfileId);
            string currentFingerprint = current == null || current.RetiredFromSubscription ? null : ExitProbeTarget.Fingerprint(current);
            bool changed = currentFingerprint != exit.Exit.ProfileFingerprint;
            var row=T(exit.Exit.Name+" → "+status+(changed?" · снимок до изменения конфига":""),12,!changed&&exit.HasRecentSuccess(DateTime.UtcNow)?Green:Warning);
            row.TextWrapping=TextWrapping.Wrap;row.Margin=new Thickness(0,6,0,0);multiExitHealthPanel.Children.Add(row);
        }
    }
    private bool multiServerApplyBusy;
    private bool CanEditServerAssignments(bool owner=false) => !snapshotMode&&!closing&&(owner||!profileAliasEditorBusy)&&(owner||!multiServerApplyBusy)&&!subscriptionReviewBusy&&!SubscriptionManager.IsUpdating&&!configuredFallbackBusy&&!profileSwitchInProgress&&!startupConnecting&&!startupSettingsBusy&&gamingSession==null&&gamingCancellation==null&&
        (core.CanSelectEngine||core.Status==CoreStatus.Connected)&&core.Engine==VpnEngine.SingBox;
    private async Task ApplyServerAssignmentsAsync(AppState opened,RoutingSettings accepted,bool restorePrevious=false)
    {
        await trafficModeGate.WaitAsync();
        bool previousSwitch=profileSwitchInProgress;
        try {
            if(!CanEditServerAssignments(true)||!ReferenceEquals(opened,state))throw new InvalidOperationException("Подключение изменилось. Откройте редактор заново.");
            multiServerApplyBusy=true;
            var catalog=MultiServerConfigBuilder.CaptureProfiles(opened.Profiles);
            string routingBaseline = RoutingMigrationService.Fingerprint(opened.Routing);
            string historyBaseline = MultiServerRoutingHistory.Fingerprint(opened.MultiServerRoutingHistory);
            string collectionBaseline = opened.ActiveProfileCollectionId;
            var restore=restorePrevious?MultiServerRoutingHistory.PreviewDisable(opened):null;
            string zapretMode = opened.Zapret?.Mode;
            string[] zapretDomains = opened.Zapret?.StandardDomains?.ToArray() ?? Array.Empty<string>();
            bool ZapretUnchanged() => opened.Zapret?.Mode == zapretMode &&
                zapretDomains.SequenceEqual(opened.Zapret?.StandardDomains ?? Enumerable.Empty<string>());
            var candidate=restore==null ? ZapretRoutingTransitionService.PrepareAssignedRouting(opened,accepted).Routing
                : ZapretRoutingTransitionService.PrepareOrdinaryRouting(opened,restore.SingleServerRouting).Routing;
            string selectedId=restorePrevious?restore.SelectedProfileId:candidate.MultiServer?.DefaultProfileId;
            var primary=catalog.FirstOrDefault(p=>p.Id==selectedId&&(!restorePrevious||!p.RetiredFromSubscription));
            var savedAssignments = opened.Routing?.MultiServer;
            if (primary == null && !restorePrevious && savedAssignments != null && !savedAssignments.RequiresRebinding &&
                savedAssignments.DefaultProfileId == selectedId)
                primary = MultiServerStartupSelection.ResolveCatalogIdentity(savedAssignments, catalog);
            if(primary==null&&(!restorePrevious||core.Status==CoreStatus.Connected))throw new InvalidOperationException(restorePrevious?
                "Прежний сервер недоступен. Текущее подключение сохранено; восстановите сервер или отключите VPN и верните правила без подключения.":"Нет доступного основного сервера.");
            if(primary!=null&&!primary.RetiredFromSubscription){string exclusion=ProfileCollectionService.Exclusion(opened,opened.Profiles.First(p=>p.Id==primary.Id));if(exclusion!=null)throw new InvalidOperationException(exclusion);}
            bool InputsUnchanged() => ReferenceEquals(opened, state) && ZapretUnchanged() &&
                opened.ActiveProfileCollectionId == collectionBaseline &&
                RoutingMigrationService.Fingerprint(opened.Routing) == routingBaseline &&
                MultiServerRoutingHistory.Fingerprint(opened.MultiServerRoutingHistory) == historyBaseline;
            void RequireCandidateCatalog()
            {
                if (candidate.MultiServer != null)
                    MultiServerConfigBuilder.RequireCurrentCatalog(candidate.MultiServer, catalog, opened.Profiles);
            }
            if(core.Status!=CoreStatus.Connected){
                if(primary!=null){string validation=await core.ValidateAsync(primary,candidate,catalog);if(!string.IsNullOrEmpty(validation))throw new InvalidOperationException(validation);}
                if(!InputsUnchanged())throw new InvalidOperationException("Настройки маршрутизации изменились. Повторите применение.");
                RequireCandidateCatalog();
                var commit=restorePrevious?MultiServerRoutingCommitPlan.PrepareDisable(opened):MultiServerRoutingCommitPlan.Prepare(opened,candidate);
                commit.Apply(()=>ReferenceEquals(opened,state)&&CanEditServerAssignments(true)&&core.CanSelectEngine,StateStore.Save);
                Toast(restorePrevious?(primary==null?"Прежние правила восстановлены. Выберите доступный сервер перед подключением.":"Прежняя маршрутизация восстановлена. Назначения сохранены."):"Назначения проверены и сохранены. Подключите VPN.",Green);return;
            }
            var previousRouting=RoutingOverlayService.Clone(core.LastConnectedRouting??opened.Routing);
            var previousCatalog=core.CaptureActiveProfiles();
            var previousProfile = previousRouting.MultiServer == null
                ? previousCatalog.SingleOrDefault(p => p.Id == core.ActiveProfileId)
                : MultiServerStartupSelection.ResolveCatalogIdentity(previousRouting.MultiServer, previousCatalog);
            if (previousProfile == null || previousProfile.Id != core.ActiveProfileId)
                throw new InvalidOperationException("Нет снимка предыдущего подключения.");
            bool Current()=>!closing&&!snapshotMode&&!manualDisconnect&&connectionRequested&&InputsUnchanged();
            profileSwitchInProgress=true;multiServerApplyBusy=true;reconnectTimer.Stop();manualDisconnect=false;connectionRequested=true;
            async Task Restart(VpnProfile selected,RoutingSettings routes,VpnProfile[] profiles){
                if(!Current())throw new OperationCanceledException();
                manualDisconnect=true;connectionRequested=false;
                try{core.Stop();}finally{manualDisconnect=false;connectionRequested=true;}
                await core.StartAsync(selected,routes,profiles);
                if(!Current())throw new OperationCanceledException();
                if(core.Status!=CoreStatus.Connected||core.ActiveProfileId!=selected.Id)throw new InvalidOperationException(string.IsNullOrEmpty(core.LastError)?"Не удалось запустить подключение.":core.LastError);
            }
            var result=await RoutingApplyTransaction.ApplyAsync(opened,Current,async()=>{
                string validation=await core.ValidateAsync(primary,candidate,catalog);if(!string.IsNullOrEmpty(validation))throw new InvalidOperationException(validation);
                RequireCandidateCatalog();
            },async()=>{
                await Restart(primary,candidate,catalog);
                // A changed catalog rejects the candidate without revoking ownership
                // of the previous running snapshot needed by the rollback.
                RequireCandidateCatalog();
            },()=>{
                var commit=restorePrevious?MultiServerRoutingCommitPlan.PrepareDisable(opened,core.ActiveProfileId):MultiServerRoutingCommitPlan.PrepareConnected(opened,candidate,core.ActiveProfileId);
                commit.Apply(Current,StateStore.Save);
            },StateStore.Save,()=>Restart(previousProfile,previousRouting,previousCatalog));
            if(result.Applied)Toast(restorePrevious?"Прежняя маршрутизация восстановлена. Назначения сохранены.":"Назначения применены. VPN переподключён.",Green);
            else if(result.Cancelled)Toast("Применение отменено. Новые настройки не подтверждены.",Warning);
            else if(result.PreviousRestored&&result.PersistenceRollbackError==null)Toast("Изменение не применено. Предыдущее подключение восстановлено.",Warning);
            else Toast("Не удалось применить назначения: "+LogSanitizer.Sanitize((result.RestoreError??result.PersistenceRollbackError??result.Error)?.Message),Danger);
        }finally{multiServerApplyBusy=false;profileSwitchInProgress=previousSwitch;trafficModeGate.Release();if(!closing&&currentPage=="routing")RefreshCurrentPage();}
    }
    private async Task EditMultiServerRoutingAsync(bool resume=false)
    {
        if(!CanEditServerAssignments())return;
        var opened=state;string baseline=FullStateRestorePlan.Fingerprint(opened);profileAliasEditorBusy=true;
        try {
            var editorRoutes=resume?MultiServerRoutingHistory.PreviewResume(opened):MultiServerRoutingHistory.AssignmentEditorSource(opened);
            var paths=(editorRoutes.MultiServer?.Rules??new List<MultiServerRuleRecord>()).Where(r=>r.ApplicationPath!=null).Select(r=>r.ApplicationPath).Concat(editorRoutes.DirectProcessPaths).Concat(editorRoutes.ProxyProcessPaths).ToArray();
            var entries=await Task.Run(()=>ApplicationCatalog.Discover(paths));
            if(!CanEditServerAssignments(true)||!ReferenceEquals(opened,state)||FullStateRestorePlan.Fingerprint(opened)!=baseline)throw new InvalidOperationException("Настройки изменились. Откройте редактор заново.");
            var editor=new MultiServerRoutingWindow(editorRoutes,opened.Profiles,entries,opened.SelectedProfileId,opened.Visual,
                RoutingOverlayService.IsZapretMode(opened.Zapret?.Mode),this);
            if(editor.ShowDialog()!=true)return;
            if(FullStateRestorePlan.Fingerprint(opened)!=baseline)throw new InvalidOperationException("Настройки изменились. Повторите изменение.");
            await ApplyServerAssignmentsAsync(opened,editor.Accepted);
        }catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}
        finally{profileAliasEditorBusy=false;ScheduleRetiredProfileCleanup();}
    }
	private bool ApplicationRoutingNeedsApply()
	{
		if (core.Status != CoreStatus.Connected || core.LastConnectedRouting == null) return false;
		return !SameApplicationPaths(state.Routing.DirectProcessPaths, core.LastConnectedRouting.DirectProcessPaths)
			|| !SameApplicationPaths(state.Routing.ProxyProcessPaths, core.LastConnectedRouting.ProxyProcessPaths);
	}

	private static bool SameApplicationPaths(IEnumerable<string> first, IEnumerable<string> second)
	{
		IEnumerable<string> Normalize(IEnumerable<string> paths) => (paths ?? Enumerable.Empty<string>())
			.Select(ApplicationRoutingService.NormalizePath).Where(path => path.Length > 0);
		return new HashSet<string>(Normalize(first), StringComparer.OrdinalIgnoreCase).SetEquals(Normalize(second));
	}

	private async Task ApplyApplicationRoutingAsync()
	{
        if (configuredFallbackBusy) { Toast("Дождитесь резервного подключения перед применением маршрутов.", Warning); return; }
        if (gamingSession != null) { Toast("Обычные правила сохранены и применятся при завершении игровой сессии.", Warning); return; }
		if (!applicationRoutingPending || core.Status != CoreStatus.Connected) return;
		await trafficModeGate.WaitAsync();
		bool stopped = false;
		VpnProfile profile = SelectedProfile();
		RoutingSettings previous = core.LastConnectedRouting;
		try
		{
			if (profile == null) throw new InvalidOperationException("Сначала выберите VPN-конфиг.");
			RoutingSettings updated = EffectiveRouting();
			string error = await core.ValidateAsync(profile, updated, state.Profiles);
			if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
			manualDisconnect = true;
			connectionRequested = false;
			core.Stop();
			stopped = true;
			manualDisconnect = false;
			connectionRequested = true;
			await StartAllowedProfileAsync(profile, updated);
			applicationRoutingPending = false;
			Toast("Маршруты приложений применены.", Green);
		}
		catch (Exception ex)
		{
			if (stopped && previous != null)
			{
				try { core.Stop(); manualDisconnect = false; connectionRequested = true; await StartAllowedProfileAsync(profile, previous); }
				catch { manualDisconnect = true; connectionRequested = false; }
			}
			Toast("Не удалось применить маршруты: " + LogSanitizer.Sanitize(ex.Message), Danger);
		}
		finally
		{
			trafficModeGate.Release();
			if (currentPage == "routing") RefreshCurrentPage();
		}
	}

	private Button PresetButton(string title, string key)
	{
		Button button = SmallButton(title);
		button.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		button.IsEnabled = state.Zapret?.RoutingChangesApplied != true;
		if (!button.IsEnabled) button.ToolTip = "Отключите Zapret, чтобы изменить набор VPN-маршрутов.";
		button.Click += delegate
		{
			RoutingPresetService.Apply(state.Routing, key);
			StateStore.Save(state);
			Toast("Набор правил применён: " + title + ".", Green);
			Navigate("routing");
		};
		return button;
	}

	private Border ModeCard(string key, string title, string caption, string glyph, int column)
	{
		bool flag = string.Equals(state.Routing.Mode, key, StringComparison.OrdinalIgnoreCase);
		Border border = new Border();
		border.Margin = new Thickness((column != 0) ? 6 : 0, 0.0, (column != 2) ? 6 : 0, 0.0);
		border.Padding = new Thickness(17.0, 15.0, 17.0, 15.0);
		border.CornerRadius = Radius(15.0);
		border.Background = new SolidColorBrush(flag ? C("#211E37") : Surface);
		border.BorderBrush = new SolidColorBrush(flag ? Accent : BorderColor);
		border.BorderThickness = new Thickness(1.0);
		border.Cursor = Cursors.Hand;
		Border border2 = border;
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(39.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		Viewbox textBlock = IconFactory.Create(glyph, 19.0, flag ? AccentLight : Muted);
		textBlock.VerticalAlignment = VerticalAlignment.Center;
		grid.Children.Add(textBlock);
		StackPanel stackPanel = new StackPanel();
		TextBlock modeTitle = T(title, 12.5, TextColor, FontWeights.SemiBold);
        TextBlock modeCaption = T(caption, 9.5, Muted);
        modeTitle.TextWrapping = modeCaption.TextWrapping = TextWrapping.Wrap;
        modeTitle.TextTrimming = modeCaption.TextTrimming = TextTrimming.None;
        stackPanel.Children.Add(modeTitle); stackPanel.Children.Add(modeCaption);
		Grid.SetColumn(stackPanel, 1);
		grid.Children.Add(stackPanel);
		border2.Child = grid;
		border2.MouseLeftButtonUp += delegate
		{
			if (core.Status == CoreStatus.Connected)
			{
				Toast("Маршрут применится после переподключения.", Warning);
			}
			state.Routing.Mode = key;
			StateStore.Save(state);
			Navigate("routing");
		};
		Grid.SetColumn(border2, column);
		VisualDesign.ApplyGlassSurface(border2, state.Visual);
		return border2;
	}

	private Border RoutingToggleCard(string title, string caption, string key)
	{
		bool flag = key == "lan" ? state.Routing.BypassLan : key == "ads" ? state.Routing.BlockAds : key == "ipv6" ? state.Routing.EnableIpv6 : key == "strict" ? state.Routing.StrictRoute : state.Routing.EnableSniffing;
		Border border = new Border();
		border.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		border.Padding = new Thickness(17.0, 14.0, 15.0, 14.0);
		border.CornerRadius = Radius(14.0);
		border.Background = new SolidColorBrush(Surface);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		Border border2 = border;
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(74.0)
		});
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(T(title, 12.0, TextColor, FontWeights.SemiBold));
		stackPanel.Children.Add(T(caption, 9.5, Muted));
		grid.Children.Add(stackPanel);
		Button button = ToggleButton(flag);
		button.Click += delegate
		{
			if (key == "lan")
			{
				state.Routing.BypassLan = !state.Routing.BypassLan;
			}
			else if (key == "ads")
			{
				state.Routing.BlockAds = !state.Routing.BlockAds;
			}
			else if (key == "ipv6")
			{
				state.Routing.EnableIpv6 = !state.Routing.EnableIpv6;
			}
			else if (key == "strict")
			{
				state.Routing.StrictRoute = !state.Routing.StrictRoute;
			}
			else
			{
				state.Routing.EnableSniffing = !state.Routing.EnableSniffing;
			}
			StateStore.Save(state);
			Navigate("routing");
		};
		Grid.SetColumn(button, 1);
		grid.Children.Add(button);
		border2.Child = grid;
		VisualDesign.ApplyGlassSurface(border2, state.Visual);
		EnableCardMotion(border2);
		return border2;
	}

	private Border RouteEditorCard(string title, string caption, List<string> values, Color accent, string key)
	{
		bool zapretTransitionActive = state.Zapret?.RoutingChangesApplied == true;
		bool routingListLocked = zapretTransitionActive && (key == "proxy-domains" || key == "direct-domains"
			|| key == "proxy-apps" || key == "direct-apps" || key == "proxy-paths" || key == "direct-paths");
		string itemLabel = key.EndsWith("domains", StringComparison.Ordinal) ? "домен"
			: key.EndsWith("apps", StringComparison.Ordinal) ? "приложение"
			: key.EndsWith("paths", StringComparison.Ordinal) ? "путь" : "правило";
		var editor = new RoutingListEditor(values, () =>
		{
			if (!snapshotMode) StateStore.Save(state);
		}, () => new TextBox
		{
			Background = new SolidColorBrush(palette.Input),
			Foreground = new SolidColorBrush(TextColor),
			CaretBrush = new SolidColorBrush(AccentLight),
			BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 13 * state.Visual.TextScale,
			MinHeight = Math.Max(36, 36 * state.Visual.TextScale),
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(8, 6, 8, 6),
			ToolTip = caption
		}, text =>
		{
			Button button = SmallButton(text);
			button.Foreground = new SolidColorBrush(TextColor);
			return button;
		}, itemLabel, routingListLocked);
		if (routingListLocked)
			editor.ToolTip = "Выключите Zapret, чтобы редактировать списки маршрутов. Исходные VPN-маршруты временно сохранены и вернутся автоматически.";
		AutomationProperties.SetName(editor, title);
        return RouteCard(title, caption, accent, editor);
    }

    private Border RouteCard(string title, string caption, Color accent, UIElement editor)
    {
		Border border = new Border();
		border.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		border.Padding = new Thickness(17.0, 14.0, 14.0, 14.0);
		border.CornerRadius = Radius(14.0);
		border.Background = new SolidColorBrush(Surface);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		Border border2 = border;
		Grid grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(9.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		Grid grid2 = new Grid();
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(10.0)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.Children.Add(new Border
		{
			Width = 4.0,
			Height = 28.0,
			CornerRadius = new CornerRadius(2.0),
			Background = new SolidColorBrush(accent),
			HorizontalAlignment = HorizontalAlignment.Left
		});
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(T(title, 11.5, TextColor, FontWeights.SemiBold));
		var captionText = T(caption, 9.0, Muted);
		captionText.TextWrapping = TextWrapping.Wrap;
		stackPanel.Children.Add(captionText);
		Grid.SetColumn(stackPanel, 1);
		grid2.Children.Add(stackPanel);
		grid.Children.Add(grid2);
		Grid.SetRow(editor, 2);
		grid.Children.Add(editor);
		border2.Child = grid;
		VisualDesign.ApplyGlassSurface(border2, state.Visual);
		EnableCardMotion(border2);
		return border2;
	}

    private UIElement BuildConnectionJournal()
    {
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0,0,0,12) };
        var copy = SmallButton("Копировать журнал");
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(T("Журнал подключения",16,TextColor,FontWeights.SemiBold));
        panel.Children.Add(header);
        var logs = EditorBox(string.IsNullOrWhiteSpace(core.Logs)
            ? "Журнал пока пуст. Подключитесь к серверу, чтобы увидеть подробности."
            : LogSanitizer.Sanitize(core.Logs), true);
        logs.Height = 220;
        logs.FontFamily = new FontFamily("Consolas");
        logs.FontSize = 13 * state.Visual.TextScale;
        copy.IsEnabled = !string.IsNullOrWhiteSpace(core.Logs);
        copy.Click += delegate {
            Clipboard.SetText(LogSanitizer.Sanitize(core.Logs));
            Toast("Журнал скопирован без секретов.",Green);
        };
        panel.Children.Add(logs);
        return new Border { Child = panel, Padding = new Thickness(16),
            Margin = new Thickness(0,0,0,22), Background = new SolidColorBrush(Surface),
            BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1),
            CornerRadius = Radius(8) };
    }

	private UIElement BuildDiagnosticsPage()
	{
		Grid page = new Grid();
		page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18.0) });
		page.RowDefinitions.Add(new RowDefinition());
		Button run = PrimaryButton("Запустить проверку", 160.0);
		TourAnchor("diagnostics.run", run);
		Button speed = SmallButton("Тест скорости");
		speed.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		speed.Click += async delegate
		{
			speed.IsEnabled = false;
			speed.Content = "Измеряем…";
			NetworkSpeedResult result = await NetworkSpeedService.MeasureAsync();
			Toast(result.Success ? "Примерная скорость: " + result.MegabitsPerSecond.ToString("0.0") + " Мбит/с. " + result.Message : "Тест скорости не выполнен: " + result.Message, result.Success ? Green : Warning);
			speed.Content = "Тест скорости";
			speed.IsEnabled = true;
		};
		StackPanel diagnosticActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		diagnosticActions.Children.Add(speed);
		diagnosticActions.Children.Add(run);
		page.Children.Add(PageHeader("Диагностика", "Проверка ядра, сервера, DNS и правил без раскрытия ключей", diagnosticActions, null));

		StackPanel content = new StackPanel();
		content.Children.Add(SectionTitle("ПОЛНАЯ ПРОВЕРКА"));
		Border reportCard = new Border
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 22.0),
			Padding = new Thickness(16.0),
			Background = new SolidColorBrush(Surface),
			BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1.0),
			CornerRadius = Radius(15.0)
		};
		Grid reportGrid = new Grid();
		reportGrid.RowDefinitions.Add(new RowDefinition());
		reportGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42.0) });
		StackPanel reportContent = new StackPanel();
		ProgressBar diagnosticProgress = new ProgressBar { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Accent), Background = new SolidColorBrush(Surface2), Margin = new Thickness(0, 0, 0, 12) };
		reportContent.Children.Add(diagnosticProgress);
		StackPanel visualResults = new StackPanel(); reportContent.Children.Add(visualResults);
		TextBox reportBox = EditorBox("Нажмите «Запустить проверку». NOVA проверит настройки и скроет секреты в отчёте.", true);
		reportBox.MinHeight = 180.0;
		reportContent.Children.Add(reportBox);
		reportGrid.Children.Add(reportContent);
		Button copy = SmallButton("Копировать отчёт");
		copy.HorizontalAlignment = HorizontalAlignment.Right;
		copy.VerticalAlignment = VerticalAlignment.Bottom;
		copy.Click += delegate
		{
			Clipboard.SetText(LogSanitizer.Sanitize(reportBox.Text));
			Toast("Диагностический отчёт скопирован без секретов.", Green);
		};
		Grid.SetRow(copy, 1);
		reportGrid.Children.Add(copy);
		reportCard.Child = reportGrid;
		content.Children.Add(reportCard);

		run.Click += async delegate
		{
			run.IsEnabled = false;
			run.Content = "Проверяем…";
			diagnosticProgress.Visibility = Visibility.Visible;
			reportBox.Visibility = Visibility.Collapsed;
			visualResults.Children.Clear();
			try
			{
				DiagnosticReport report = await DiagnosticService.RunAsync(core, SelectedProfile(), EffectiveRouting(), state.Profiles);
				reportBox.Text = report.ToString();
				foreach (DiagnosticItem item in report.Items) visualResults.Children.Add(DiagnosticResultRow(item));
				StateStore.Save(state);
				Toast(report.Success ? "Все основные проверки пройдены." : "Обнаружены проблемы — смотрите отчёт.", report.Success ? Green : Warning);
			}
			finally
			{
				diagnosticProgress.Visibility = Visibility.Collapsed;
				run.Content = "Запустить проверку";
				run.IsEnabled = true;
			}
		};

		content.Children.Add(BuildConnectionJournal());
		content.Children.Add(SectionTitle("ПОЧЕМУ ТАКОЙ МАРШРУТ?"));
		Border routeCard = new Border
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
			Padding = new Thickness(16.0),
			Background = new SolidColorBrush(Surface),
			BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1.0),
			CornerRadius = Radius(15.0)
		};
		Grid routeGrid = new Grid();
		routeGrid.ColumnDefinitions.Add(new ColumnDefinition());
		routeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14.0) });
		routeGrid.ColumnDefinitions.Add(new ColumnDefinition());
		routeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130.0) });
		TextBox destination = EditorBox("example.com", false);
		destination.Height = 38.0;
		destination.ToolTip = "Домен, IP или URL";
		routeGrid.Children.Add(destination);
		TextBox process = EditorBox("discord.exe", false);
		process.Height = 38.0;
		process.ToolTip = "Имя или полный путь приложения (необязательно)";
		Grid.SetColumn(process, 2);
		routeGrid.Children.Add(process);
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        routeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var networkChoice = new ComboBox { ItemsSource = new[] { "Транспорт не указан", "TCP", "UDP" }, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 0), Height = 34 };
        networkChoice.ToolTip = "TCP или UDP конкретного соединения; HTTPS также может использовать UDP/QUIC";
        Grid.SetRow(networkChoice, 1); routeGrid.Children.Add(networkChoice);
        var destinationPort = EditorBox("", false); destinationPort.Height = 34; destinationPort.Margin = new Thickness(0, 8, 0, 0);
        destinationPort.ToolTip = "Порт назначения; для URL берётся порт из адреса";
        Grid.SetRow(destinationPort, 1); Grid.SetColumn(destinationPort, 2); routeGrid.Children.Add(destinationPort);
		Button explain = SmallButton("Объяснить");
		explain.Height = 38.0;
		explain.Click += delegate
		{
            int? port = null;
            if (!string.IsNullOrWhiteSpace(destinationPort.Text))
            {
                if (!int.TryParse(destinationPort.Text, out int parsedPort) || parsedPort < 1 || parsedPort > 65535) { Toast("Порт должен быть от 1 до 65535.", Warning); return; }
                port = parsedPort;
            }
            string network = networkChoice.SelectedIndex == 1 ? "tcp" : networkChoice.SelectedIndex == 2 ? "udp" : "";
            try
            {
                var decision = ExplainCurrentRouting(destination.Text,process.Text,network,port);
                ShowRouteDecision(routeCard, decision, destination.Text, process.Text);
            }
            catch (Exception) { Toast("Для выбранного ядра или полного конфига этот набор правил не поддерживается. Проверьте совместимость настроек.", Warning); }
		};
		Grid.SetColumn(explain, 3);
		routeGrid.Children.Add(explain);
		routeCard.Child = routeGrid;
		content.Children.Add(routeCard);

		List<string> warnings = RoutingInspector.Validate(state.Routing);
		TextBlock validation = T(warnings.Count == 0 ? "✓ Конфликтов между правилами не найдено." : string.Join("\n", warnings.Take(5)), 10.0, warnings.Count == 0 ? Green : Warning);
		validation.Margin = new Thickness(2.0, 10.0, 0.0, 0.0);
		validation.TextWrapping = TextWrapping.Wrap;
		content.Children.Add(validation);
		content.Children.Add(SectionTitle("ПОСЛЕДНИЕ ПОДКЛЮЧЕНИЯ", new Thickness(0.0, 22.0, 0.0, 10.0)));
		Border historyCard = new Border { Padding = new Thickness(16.0), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1.0), CornerRadius = Radius(15.0) };
		StackPanel history = new StackPanel();
		ConnectionHistoryItem[] recent = state.ConnectionHistory.OrderByDescending(item => item.TimestampUtc).Take(8).ToArray();
		if (recent.Length == 0)
		{
			history.Children.Add(T("История пока пуста.", 10.0, Muted));
		}
		else
		{
			foreach (ConnectionHistoryItem item in recent)
			{
				string duration = item.DurationSeconds > 0 ? " · " + TimeSpan.FromSeconds(item.DurationSeconds).ToString(@"hh\:mm\:ss") : "";
				TextBlock row = T(item.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm") + " · " + item.Status + " · " + item.ProfileName + duration, 10.0, item.Status == "Обрыв" ? Danger : item.Status.Contains("Подключ") ? Green : Muted);
				row.ToolTip = item.Details;
				row.Margin = new Thickness(0.0, 2.0, 0.0, 2.0);
				history.Children.Add(row);
			}
		}
		historyCard.Child = history;
		content.Children.Add(historyCard);

		ScrollViewer scroll = TouchScrollViewer(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
		Grid.SetRow(scroll, 2);
		page.Children.Add(scroll);
		return page;
	}

	private UIElement DiagnosticResultRow(DiagnosticItem item)
	{
		bool severe = !item.Success && ((item.Details ?? "").IndexOf("ошиб", StringComparison.OrdinalIgnoreCase) >= 0 || (item.Details ?? "").IndexOf("не найден", StringComparison.OrdinalIgnoreCase) >= 0);
		Color color = item.Success ? Green : severe ? Danger : Warning;
		Border row = new Border { Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 7), Background = new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)), BorderThickness = new Thickness(1), CornerRadius = Radius(10) };
		Grid grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
		Viewbox icon = IconFactory.Create(item.Success ? "check" : "diagnostics", 16, color); icon.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(icon);
		StackPanel copy = new StackPanel(); copy.Children.Add(T(item.Name, 10.5, TextColor, FontWeights.SemiBold)); TextBlock detail = T(item.Details, 9, Muted); detail.TextWrapping = TextWrapping.Wrap; copy.Children.Add(detail); Grid.SetColumn(copy, 1); grid.Children.Add(copy); row.Child = grid; return row;
	}

	private void ShowRouteDecision(Border routeCard, RoutingDecision decision, string destination, string process)
	{
		Color color = !decision.IsCertain ? Warning : decision.Route == "proxy" ? AccentLight : decision.Route == "direct" ? Teal : decision.Route == "block" ? Danger : Warning;
		StackPanel flow = new StackPanel();
		Grid nodes = new Grid(); nodes.ColumnDefinitions.Add(new ColumnDefinition()); nodes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) }); nodes.ColumnDefinitions.Add(new ColumnDefinition()); nodes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) }); nodes.ColumnDefinitions.Add(new ColumnDefinition());
		nodes.Children.Add(RouteNode("globe", string.IsNullOrWhiteSpace(destination) ? "Назначение" : destination, Muted, 0)); TextBlock arrow1 = T("→", 18, Muted); arrow1.HorizontalAlignment = HorizontalAlignment.Center; arrow1.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow1, 1); nodes.Children.Add(arrow1);
		nodes.Children.Add(RouteNode("routing", string.IsNullOrWhiteSpace(process) ? "Правила" : process, AccentLight, 2)); TextBlock arrow2 = T("→", 18, color); arrow2.HorizontalAlignment = HorizontalAlignment.Center; arrow2.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow2, 3); nodes.Children.Add(arrow2);
		nodes.Children.Add(RouteNode(decision.Route == "block" ? "diagnostics" : decision.Route == "direct" ? "home" : "shield", decision.DisplayRoute.ToUpperInvariant(), color, 4));
        bool? compactLayout = null;
        Action<bool> arrangeNodes = compact => {
            if (compactLayout == compact) return; compactLayout = compact;
            nodes.ColumnDefinitions.Clear(); nodes.RowDefinitions.Clear();
            if (compact) {
                nodes.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < nodes.Children.Count; i++) { nodes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetColumn(nodes.Children[i], 0); Grid.SetRow(nodes.Children[i], i); }
            } else {
                for (int i = 0; i < 5; i++) nodes.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(45) : new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < nodes.Children.Count; i++) { Grid.SetColumn(nodes.Children[i], i); Grid.SetRow(nodes.Children[i], 0); }
            }
            arrow1.Text = arrow2.Text = compact ? "↓" : "→";
        };
        arrangeNodes(!double.IsNaN(routeCard.Width) && routeCard.Width < 540);
        nodes.SizeChanged += (sender, args) => arrangeNodes(args.NewSize.Width < 540);
        flow.Children.Add(nodes); TextBlock reason = T(decision.Reason, 9.5, Muted); reason.TextWrapping = TextWrapping.Wrap; reason.Margin = new Thickness(0, 12, 0, 0); flow.Children.Add(reason); routeCard.Child = flow;
	}

	private UIElement RouteNode(string icon, string label, Color color, int column)
	{
		Border node = new Border { Padding = new Thickness(10), Background = new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)), BorderThickness = new Thickness(1), CornerRadius = Radius(10) };
		Grid content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); var symbol = IconFactory.Create(icon, 16, color); symbol.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(symbol); TextBlock copy = T(label, 9.5, color, FontWeights.SemiBold); copy.Margin = new Thickness(7, 0, 0, 0); copy.TextWrapping = TextWrapping.Wrap; copy.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(copy, 1); content.Children.Add(copy); node.Child = content; Grid.SetColumn(node, column); return node;
	}

	private TextBox EditorBox(string text, bool readOnly)
	{
		return new TextBox
		{
			Text = text,
			Background = new SolidColorBrush(C("#0E1117")),
			Foreground = new SolidColorBrush(C("#CBD1DC")),
			CaretBrush = new SolidColorBrush(AccentLight),
			BorderBrush = new SolidColorBrush(C("#242A37")),
			BorderThickness = new Thickness(1.0),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 13 * state.Visual.TextScale,
			IsReadOnly = readOnly,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = true,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0)
		};
	}

    private readonly System.Collections.ObjectModel.ObservableCollection<CoreUsageRecord> coreUsageRecords = new System.Collections.ObjectModel.ObservableCollection<CoreUsageRecord>();
    private UIElement BuildEnginePage()
    {
        var body = new StackPanel();
        if (gamingSession != null || gamingCancellation != null) return PageHeader("Игровой режим", "Сначала выключите игровой режим или отмените подбор.", null, null);
        var resources = PrimaryButton("Нагрузка и EcoQoS", 190);
        resources.Click += delegate {
            var dialog = new CoreResourceWindow(core, coreUsageRecords, () => VpnEngines.DisplayName(core.Engine) + " · " + (state.Profiles.FirstOrDefault(p => p.Id == core.ActiveProfileId)?.Name ?? "профиль"),
                enabled => { state.CoreEcoMode = enabled; if (!snapshotMode) StateStore.Save(state); }, state.Visual, snapshotMode, mapHealthCancellation.Token) { Owner = this };
            dialog.ShowDialog();
        };
        var compare = PrimaryButton("Сравнить ядра", 170);
        compare.Click += delegate {
            var selected = SelectedProfile();
            string exclusion = ProfileCollectionService.Exclusion(state, selected);
            if (!snapshotMode && exclusion != null) { Toast(exclusion, Warning); return; }
            string profileId = selected?.Id;
            var dialog = new CoreBenchmarkWindow(() => {
                var current = state.Profiles.FirstOrDefault(p => p.Id == profileId);
                return ProfileCollectionService.Exclusion(state, current) == null ? current : null;
            },
                () => core.Status == CoreStatus.Disconnected && !connectionRequested && !collectionEditorBusy && gamingSession == null && gamingCancellation == null,
                VpnEngines.ExecutablePath(VpnEngine.SingBox), VpnEngines.ExecutablePath(VpnEngine.Reality), System.IO.Path.Combine(StateStore.DirectoryPath, "runtime", "core-benchmark"),
                state.Visual, snapshotMode, mapHealthCancellation.Token) { Owner = this };
            dialog.ShowDialog();
        };
        var actions = new WrapPanel(); actions.Children.Add(resources); actions.Children.Add(compare);
        body.Children.Add(PageHeader("VPN-ядро", "Выберите движок подключения", actions, null));
        var hint = T("Выбор сохраняется для следующих подключений. Менять ядро можно после отключения VPN.",14,Muted);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0,18,0,20); body.Children.Add(hint);
        foreach (VpnEngine engine in new[] { VpnEngine.SingBox, VpnEngine.Reality })
        {
            bool installed = File.Exists(VpnEngines.ExecutablePath(engine));
            var copy = new StackPanel();
            copy.Children.Add(T(VpnEngines.DisplayName(engine),17,TextColor,FontWeights.SemiBold));
            var description = T(engine == VpnEngine.SingBox
                ? "Основное ядро NOVA. Полный набор профилей и настроек клиента."
                : "Альтернативное ядро для VLESS и Trojan. Неподдерживаемые параметры проверяются до подключения.",14,Muted);
            description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(0,8,0,8); copy.Children.Add(description);
            copy.Children.Add(T(installed ? "Установлено" : "Файл ядра отсутствует в сборке",12,installed ? Green : Warning));
            var choice = new RadioButton { GroupName = "VpnEngine", Content = copy, IsChecked = core.Engine == engine,
                IsEnabled = installed && core.Status != CoreStatus.Connected && core.Status != CoreStatus.Connecting,
                Foreground = new SolidColorBrush(TextColor), Padding = new Thickness(12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice,VpnEngines.DisplayName(engine));
            choice.Checked += delegate {
                if (core.Engine == engine) return;
                try {
                    core.SelectEngine(engine); state.EngineSelection = (int)engine;
                    if (!snapshotMode) StateStore.Save(state);
                    Navigate("engine",false);
                } catch (Exception ex) { Toast(LogSanitizer.Sanitize(ex.Message),Warning); Navigate("engine",false); }
            };
            body.Children.Add(new Border { Child = choice, Padding = new Thickness(12), Margin = new Thickness(0,0,0,12),
                Background = new SolidColorBrush(core.Engine == engine ? palette.SoftSelection : Surface),
                BorderBrush = new SolidColorBrush(core.Engine == engine ? Accent : BorderColor),
                BorderThickness = new Thickness(1), CornerRadius = Radius(8) });
        }
        var details = T("Reality Core: нужен защищённый DNS и отключённый обход DNS локальных имён. Для правил сайтов и блокировки рекламы нужен sing-box. Правила приложений и другие несовместимые параметры проверяются перед подключением.\n\nЛокальные SOCKS/VLESS-проверки пройдены. Работа Windows TUN и реального провайдера в этой сборке ещё не подтверждена.",14,Muted);
        details.TextWrapping = TextWrapping.Wrap; details.Margin = new Thickness(0,12,0,0); body.Children.Add(details);
        return TouchScrollViewer(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

	private UIElement BuildSettingsPage()
	{
		Grid grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(18.0)
		});
		grid.RowDefinitions.Add(new RowDefinition());
		Button appearance = SmallButton("Интерфейс и доступность");
		TourAnchor("settings.appearance", appearance);
		appearance.Click += delegate
		{
			AppearanceWindow dialog = new AppearanceWindow(state.Visual, state.ShowAnimations) { Owner = this };
			if (dialog.ShowDialog() == true)
			{
				bool profileChanged = !string.Equals(state.Visual.ThemeName, dialog.Result.ThemeName, StringComparison.OrdinalIgnoreCase);
				state.Visual.CopyFrom(dialog.Result);
				state.Visual.StoreActiveThemeSettings();
				state.Theme = state.Visual.ThemeName;
				StateStore.Save(state);
				BuildShell();
				if (profileChanged && contentHost.Children.Count > 0) AnimatePage(contentHost.Children[0]);
				Toast("Оформление сохранено.", Green);
			}
		};
		var identityButton = SmallButton("Идентификация и перенос");
        identityButton.Click += delegate {
            try{
                var dialog=new IdentityWindow(state.SubscriptionIdentities,state.Visual,state,()=>CanRestoreFullState()){Owner=this};
                if(dialog.ShowDialog()!=true)return;
                if(dialog.FullRestore!=null){RestoreFullState(dialog.FullRestore);BuildShell();}
                else{RestoreIdentitiesOnly(dialog.Result);}
            }catch(Exception error){Toast("Не удалось восстановить данные: "+LogSanitizer.Sanitize(error.Message),Danger);}
        };
        var settingsActions=new WrapPanel();settingsActions.Children.Add(appearance);identityButton.Margin=new Thickness(8,0,0,0);settingsActions.Children.Add(identityButton);
        grid.Children.Add(PageHeader("Настройки", "Поведение клиента и оформление", settingsActions, null));
		Grid grid2 = new Grid();
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(18.0)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(SectionTitle("ОФОРМЛЕНИЕ"));
		stackPanel.Children.Add(T("Windows 11 · " + (state.Visual.DarkMode ? "Dark" : "Light") + " · Тема переключается в левой панели", 12, Muted));
        Button tour = SmallButton("Начать обучение"); tour.Click += delegate { StartGuidedTour(); }; stackPanel.Children.Add(tour);
		stackPanel.Children.Add(SectionTitle("ЗАПУСК", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel.Children.Add(TourAnchor("settings.startup", SettingCard("Запускать с Windows", "Открыть приложение после входа в Windows, без автоподключения", state.StartWithWindows, async delegate { await SetWindowsStartupAsync(false); })));
		stackPanel.Children.Add(SettingCard("Запускать и подключать с Windows", "Без окна: последний использованный профиль, при отказе — лучший по HTTPS-проверке", state.StartConnectedWithWindows, async delegate { await SetWindowsStartupAsync(true); }));
		stackPanel.Children.Add(SettingCard("Автоподключение", "Подключаться при открытии NOVA вручную", state.AutoConnect, delegate
		{
			state.AutoConnect = !state.AutoConnect;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Автопереподключение", "Восстанавливать туннель после обрыва или смены сети", state.AutoReconnect, delegate
		{
			state.AutoReconnect = !state.AutoReconnect;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Лучший сервер автоматически", "После общей проверки выбирать самый быстрый доступный", state.AutoSelectBestServer, delegate
		{
			state.AutoSelectBestServer = !state.AutoSelectBestServer;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Строгий режим TUN", "Строгие маршруты Windows и переподключение при обрыве. Работает, пока запущено VPN-ядро.", state.KillSwitch, delegate
		{
            if (gamingSession != null || gamingCancellation != null) { Toast("Сначала выключите игровой режим или отмените подбор.", Warning); return; }
			state.KillSwitch = !state.KillSwitch;
			if (state.KillSwitch)
			{
				state.AutoReconnect = true;
				state.Routing.StrictRoute = true;
			}
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Анимации", "Плавные переходы, цель до 120 кадров/с с учётом экрана", state.ShowAnimations, delegate
		{
			state.ShowAnimations = !state.ShowAnimations;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Сворачивать в трей", "Не занимать место на панели задач", state.MinimizeToTray, delegate
		{
			state.MinimizeToTray = !state.MinimizeToTray;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Уведомления Windows", "Сообщать о подключении и важных сбоях", state.ShowNotifications, delegate
		{
			state.ShowNotifications = !state.ShowNotifications;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Подсказки обучения", "Показывать пояснения рядом с важными функциями", state.ShowTrainingHints, delegate
		{
			state.ShowTrainingHints = !state.ShowTrainingHints;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SectionTitle("DNS", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel.Children.Add(ChoiceCard("Защищённый DNS", "DNS-over-HTTPS через Cloudflare", state.Routing.DnsMode == "Secure", delegate
		{
			state.Routing.DnsMode = "Secure";
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Системный DNS", "Использовать настройки Windows", state.Routing.DnsMode == "System", delegate
		{
			state.Routing.DnsMode = "System";
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Cloudflare", "1.1.1.1 · DNS-over-HTTPS", state.Routing.DnsMode == "Secure" && state.Routing.DnsProvider == "Cloudflare", delegate
		{
			state.Routing.DnsMode = "Secure";
			state.Routing.DnsProvider = "Cloudflare";
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Google DNS", "8.8.8.8 · DNS-over-HTTPS", state.Routing.DnsMode == "Secure" && state.Routing.DnsProvider == "Google", delegate
		{
			state.Routing.DnsMode = "Secure";
			state.Routing.DnsProvider = "Google";
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Quad9", "9.9.9.9 · DNS-over-HTTPS", state.Routing.DnsMode == "Secure" && state.Routing.DnsProvider == "Quad9", delegate
		{
			state.Routing.DnsMode = "Secure";
			state.Routing.DnsProvider = "Quad9";
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Свой DoH-сервер", string.IsNullOrWhiteSpace(state.Routing.CustomDnsEndpoint) ? "Указать HTTPS-адрес DNS-over-HTTPS" : state.Routing.CustomDnsEndpoint, state.Routing.DnsMode == "Secure" && state.Routing.DnsProvider == "Custom", delegate
		{
			TextPromptWindow dialog = new TextPromptWindow("Свой DNS-over-HTTPS", "Введите полный HTTPS-адрес, например https://dns.example.com/dns-query", state.Routing.CustomDnsEndpoint, state.Visual) { Owner = this };
			if (dialog.ShowDialog() == true)
			{
				Uri endpoint;
				if (!Uri.TryCreate(dialog.Value, UriKind.Absolute, out endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
				{
					Toast("DoH-адрес должен начинаться с https://", Danger);
					return;
				}
				state.Routing.CustomDnsEndpoint = dialog.Value;
				state.Routing.DnsMode = "Secure";
				state.Routing.DnsProvider = "Custom";
				StateStore.Save(state);
				Navigate("settings");
			}
		}));
		stackPanel.Children.Add(SectionTitle("ПОДПИСКИ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel.Children.Add(ChoiceCard("Каждые 6 часов", "Чаще проверять изменения серверов", state.SubscriptionUpdateIntervalHours == 6, delegate
		{
			state.SubscriptionUpdateIntervalHours = 6;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Раз в сутки", "Рекомендуемый интервал обновления", state.SubscriptionUpdateIntervalHours == 24, delegate
		{
			state.SubscriptionUpdateIntervalHours = 24;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(ChoiceCard("Раз в 3 дня", "Меньше запросов к подписке", state.SubscriptionUpdateIntervalHours == 72, delegate
		{
			state.SubscriptionUpdateIntervalHours = 72;
			StateStore.Save(state);
			Navigate("settings");
		}));
		GroupSettingsCards(stackPanel);
		grid2.Children.Add(stackPanel);
		StackPanel stackPanel2 = new StackPanel();
		stackPanel2.Children.Add(SectionTitle("О ПРИЛОЖЕНИИ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		Border border3 = new Border();
		border3.Padding = new Thickness(16.0);
		border3.Background = new SolidColorBrush(Surface);
		border3.BorderBrush = new SolidColorBrush(BorderColor);
		border3.BorderThickness = new Thickness(1.0);
		border3.CornerRadius = Radius(15.0);
		Border border4 = border3;
		StackPanel stackPanel4 = new StackPanel();
		stackPanel4.Children.Add(T("NOVA VPN  " + ReleaseNotesService.CurrentVersionText, 12.0, TextColor, FontWeights.SemiBold));
		stackPanel4.Children.Add(T(core.IsCoreInstalled ? "VPN-ядро установлено · Windows TUN" : "VPN-ядро не найдено", 9.5, core.IsCoreInstalled ? Green : Danger));
		stackPanel4.Children.Add(T("Профили хранятся зашифрованно в вашей учётной записи Windows.", 9.5, Muted));
		stackPanel4.Children.Add(T("Горячая клавиша подключения: Ctrl + Alt + V", 9.5, Muted));
		border4.Child = stackPanel4;
		stackPanel2.Children.Add(border4);
		stackPanel2.Children.Add(SectionTitle("ДРУГИЕ ПРОДУКТЫ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel2.Children.Add(ProductLinkCard("NOVA VPN для Android", "Клиент для телефона · исходники и APK на GitHub", "https://github.com/leoraijin/NOVA-VPN-Android"));
		stackPanel2.Children.Add(ProductLinkCard("NOVA VPN для Wear OS", "Клиент для часов · исходники и APK на GitHub", "https://github.com/leoraijin/NOVA-VPN-WearOS"));
		stackPanel2.Children.Add(SectionTitle("ОБНОВЛЕНИЕ ПРИЛОЖЕНИЯ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel2.Children.Add(SettingCard("Предлагать обновления при запуске", "Проверка GitHub выполняется всегда; предложение можно скрыть", !state.SuppressUpdatePrompts, delegate { state.SuppressUpdatePrompts = !state.SuppressUpdatePrompts; StateStore.Save(state); Navigate("settings"); }));
		Border updateCard = new Border
		{
			Padding = new Thickness(16.0),
			Background = new SolidColorBrush(Surface),
			BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1.0),
			CornerRadius = Radius(15.0)
		};
		Grid updateLayout = new Grid();
		updateLayout.ColumnDefinitions.Add(new ColumnDefinition());
		updateLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel updateCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		updateCopy.Children.Add(T("Версия " + ReleaseNotesService.CurrentVersionText, 11.0, TextColor, FontWeights.SemiBold));
		TextBlock updateHint = T("GitHub проверяется при запуске. Кнопка «Обновить» скачивает последний установщик. Конфиги и VPN-ключи не меняются.", 9.5, Muted);
		updateHint.TextWrapping = TextWrapping.Wrap;
		updateHint.Margin = new Thickness(0.0, 3.0, 12.0, 0.0);
		updateCopy.Children.Add(updateHint);
		updateLayout.Children.Add(updateCopy);
		Button appUpdateButton = PrimaryButton("Обновить", 112.0);
		TourAnchor("settings.update", appUpdateButton);
		appUpdateButton.VerticalAlignment = VerticalAlignment.Center;
		appUpdateButton.Click += async delegate { await UpdateApplicationAsync(appUpdateButton); };
		Grid.SetColumn(appUpdateButton, 1);
		updateLayout.Children.Add(appUpdateButton);
		updateCard.Child = updateLayout;
		stackPanel2.Children.Add(updateCard);
		stackPanel2.Children.Add(SectionTitle("РЕЗЕРВНАЯ КОПИЯ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		Border backupCard = new Border
		{
			Padding = new Thickness(16.0),
			Background = new SolidColorBrush(Surface),
			BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1.0),
			CornerRadius = Radius(15.0)
		};
		StackPanel backupContent = new StackPanel();
		Grid backupHeader = new Grid(); backupHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); backupHeader.ColumnDefinitions.Add(new ColumnDefinition());
		try { Image backupIcon = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/nova-backup.png", UriKind.Absolute)), Width = 40, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }; backupHeader.Children.Add(backupIcon); } catch { backupHeader.Children.Add(IconFactory.Create("shield", 26, AccentLight)); }
		StackPanel backupCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; backupCopy.Children.Add(T("Зашифрованный локальный бэкап", 12.0, TextColor, FontWeights.SemiBold)); TextBlock backupHint = T("Открывается только текущей учётной записью Windows.", 9.5, Muted); backupHint.Margin = new Thickness(0.0, 2.0, 0.0, 0.0); backupCopy.Children.Add(backupHint); Grid.SetColumn(backupCopy, 1); backupHeader.Children.Add(backupCopy); backupContent.Children.Add(backupHeader);
		TextBlock portableHint = T("Полный переносимый конфиг включает VPN-ключи. Для переноса оформления и маршрутов без VPN-профилей используйте отдельные настройки ниже.", 9.5, Warning);
		portableHint.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		portableHint.TextWrapping = TextWrapping.Wrap;
		backupContent.Children.Add(portableHint);
		WrapPanel backupButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
		backupButtons.Margin = new Thickness(0, 12, 0, 0);
		Button export = SmallButton("Экспортировать");
		export.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		export.Click += delegate
		{
			SaveFileDialog dialog = new SaveFileDialog { Filter = "NOVA Backup (*.nova-backup)|*.nova-backup", FileName = "NOVA-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".nova-backup" };
			if (dialog.ShowDialog(this) == true)
			{
				StateStore.ExportEncrypted(state, dialog.FileName);
				Toast("Зашифрованная резервная копия сохранена.", Green);
			}
		};
		Button import = SmallButton("Восстановить");
		import.Click += delegate
		{
			if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting)
			{
				Toast("Сначала отключите VPN.", Warning);
				return;
			}
			OpenFileDialog dialog = new OpenFileDialog { Filter = "NOVA Backup (*.nova-backup)|*.nova-backup" };
			if (dialog.ShowDialog(this) == true && MessageBox.Show(this, "Заменить текущие профили и настройки данными из резервной копии?", "NOVA VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				try
				{
					RestoreFullState(StateStore.ImportEncrypted(dialog.FileName));
                    BuildShell();
					Toast("Резервная копия восстановлена.", Green);
					Navigate("settings");
				}
				catch (Exception ex)
				{
					Toast("Не удалось открыть копию: " + LogSanitizer.Sanitize(ex.Message), Danger);
				}
			}
		};


		Button portableImport = SmallButton("Импорт конфига");
		portableImport.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		portableImport.Click += delegate
		{
			if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting) { Toast("Сначала отключите VPN.", Warning); return; }
			OpenFileDialog dialog = new OpenFileDialog { Filter = "Переносимый конфиг NOVA (*.nova-config)|*.nova-config" };
			if (dialog.ShowDialog(this) == true && MessageBox.Show(this, "Заменить текущие профили, маршруты и настройки данными из переносимого конфига?", "NOVA VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				try
				{
                    RestoreFullState(PortableConfigService.Import(dialog.FileName));
                    BuildShell();
                    Toast("Переносимый конфиг импортирован.", Green);
					Navigate("settings");
				}
				catch (Exception ex) { Toast("Не удалось импортировать конфиг: " + LogSanitizer.Sanitize(ex.Message), Danger); }
			}
		};


        Button settingsExport = SettingsExportButton();
        Button settingsImport = SettingsImportButton("settings");

        var exportMenu = SmallButton("Экспорт ▾");
        var importMenu = SmallButton("Восстановление ▾");
        exportMenu.Margin = new Thickness(0,0,8,0);
        AttachActionMenu(exportMenu, new[] { "Локальная зашифрованная копия", "Настройки без VPN-ключей" },
            new[] { export, settingsExport });
        AttachActionMenu(importMenu, new[] { "Из локальной копии", "Из старого конфига NOVA", "Только настройки без VPN-ключей" },
            new[] { import, portableImport, settingsImport });
        backupButtons.Children.Add(exportMenu);
        backupButtons.Children.Add(importMenu);
		backupContent.Children.Add(backupButtons);
		backupCard.Child = backupContent;
		stackPanel2.Children.Add(backupCard);
		GroupSettingsCards(stackPanel2);
		Grid.SetColumn(stackPanel2, 2);
		grid2.Children.Add(stackPanel2);
		if (responsiveMode != "Wide" || state.Visual.TextScale > 1.15)
		{
			grid2.ColumnDefinitions.Clear(); grid2.ColumnDefinitions.Add(new ColumnDefinition());
			grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetColumn(stackPanel2, 0); Grid.SetRow(stackPanel2, 1); stackPanel2.Margin = new Thickness(0,20,0,0);
		}
		ScrollViewer scrollViewer = TouchScrollViewer(new ScrollViewer());
		scrollViewer.Content = grid2;
		scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		ScrollViewer element2 = scrollViewer;
		Grid.SetRow(element2, 2);
		grid.Children.Add(element2);
		return grid;
	}

    private Button SettingsExportButton()
    {
		Button settingsExport = SmallButton("Экспорт настроек");
		settingsExport.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		settingsExport.Click += delegate
		{
			SaveFileDialog dialog = new SaveFileDialog { Filter = "NOVA Settings (*.nova-settings)|*.nova-settings", FileName = "NOVA-Settings-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".nova-settings" };
			if (dialog.ShowDialog(this) == true)
			{
				try { PortableConfigService.ExportSettingsOnly(state, dialog.FileName); Toast("Настройки сохранены без VPN-профилей и ключей.", Green); }
				catch (Exception ex) { Toast("Не удалось сохранить настройки: " + LogSanitizer.Sanitize(ex.Message), Danger); }
			}
		};

        return settingsExport;
    }

    private Button SettingsImportButton(string destination)
    {
		Button settingsImport = SmallButton("Импорт настроек");
		settingsImport.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		settingsImport.Click += delegate
		{
			if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting) { Toast("Сначала отключите VPN.", Warning); return; }
			OpenFileDialog dialog = new OpenFileDialog { Filter = "NOVA Settings (*.nova-settings)|*.nova-settings" };
			if (dialog.ShowDialog(this) == true && MessageBox.Show(this, "Заменить маршрутизацию и оформление настройками из файла? VPN-профили не переносятся.", "NOVA VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				try
				{
					RestoreSettingsOnly(PortableConfigService.ImportSettingsOnly(dialog.FileName));
                    BuildShell();
					Toast("Настройки импортированы без VPN-профилей.", Green);
					Navigate(destination);
				}
				catch (Exception ex) { Toast("Не удалось импортировать настройки: " + LogSanitizer.Sanitize(ex.Message), Danger); }
			}
		};



        return settingsImport;
    }

    private void AttachActionMenu(Button owner, string[] labels, Button[] actions)
    {
        var menu = new ContextMenu { Background = new SolidColorBrush(Surface),
            Foreground = new SolidColorBrush(TextColor), FontFamily = VisualDesign.InterfaceFont(state.Visual), FontSize = 13 * state.Visual.TextScale };
        owner.ContextMenu = menu;
        for (int i=0; i<labels.Length; i++) {
            Button action = actions[i];
            var item = new MenuItem { Header = labels[i] };
            item.Click += delegate { action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            menu.Items.Add(item);
        }
        owner.Click += delegate {
            menu.PlacementTarget = owner;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
    }

	private int MergeImportedProfiles(ImportResult importResult)
	{
		if (importResult == null || importResult.Profiles == null) return 0;
		if (importResult.IsSubscription)
		{
			SubscriptionManager.MergeImported(state, importResult);
			return importResult.Profiles.Count;
		}
		int added = 0;
		foreach (VpnProfile profile in importResult.Profiles)
		{
			if (profile == null) continue;
			VpnProfile existing = state.Profiles.FirstOrDefault(x => x != null && !string.IsNullOrWhiteSpace(x.Source) && string.Equals(x.Source, profile.Source, StringComparison.Ordinal));
			if (existing == null)
			{
				state.Profiles.Add(profile);
				added++;
			}
		}
		if (string.IsNullOrWhiteSpace(state.SelectedProfileId) && state.Profiles.Count > 0) state.SelectedProfileId = state.Profiles[0].Id;
		return added;
	}

	private void GroupSettingsCards(StackPanel panel)
	{
		if (palette.Name != ThemePalette.OneUi) return;
		UIElement[] children = panel.Children.Cast<UIElement>().ToArray();
		panel.Children.Clear();
		for (int i = 0; i < children.Length;)
		{
			int end = i;
			while (end < children.Length && children[end] is Border) end++;
			if (end - i < 2) { panel.Children.Add(children[i++]); continue; }
			StackPanel rows = new StackPanel();
			for (int row = i; row < end; row++)
			{
				Border card = (Border)children[row];
				card.Margin = new Thickness(0); card.Effect = null;
				card.BorderThickness = new Thickness(0, 0, 0, row < end - 1 ? 1 : 0);
				card.CornerRadius = new CornerRadius(row == i ? 16 : 0, row == i ? 16 : 0, row == end - 1 ? 16 : 0, row == end - 1 ? 16 : 0);
				rows.Children.Add(card);
			}
			panel.Children.Add(new Border { Child = rows, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Surface), Margin = new Thickness(0, 0, 0, 9) });
			i = end;
		}
	}

	private Border ProductLinkCard(string title, string caption, string url)
	{
		Border card = new Border { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 9),
			Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor),
			BorderThickness = new Thickness(1), CornerRadius = Radius(14) };
		StackPanel content = new StackPanel();
		TextBlock heading = T(title, 12, TextColor, FontWeights.SemiBold); heading.TextWrapping = TextWrapping.Wrap;
		TextBlock description = T(caption, 9.5, Muted); description.TextWrapping = TextWrapping.Wrap;
		content.Children.Add(heading); content.Children.Add(description);
		Button open = SmallButton("Открыть на GitHub ↗");
		open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 10, 0, 0);
		open.ToolTip = url; AutomationProperties.SetName(open, title + " · открыть на GitHub");
		open.Click += delegate {
			if (snapshotMode) return;
			try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
			catch { Toast("Не удалось открыть GitHub. Проверьте браузер по умолчанию.", Warning); }
		};
		content.Children.Add(open); card.Child = content;
		VisualDesign.ApplyGlassSurface(card, state.Visual);
		return card;
	}

	private Border SettingCard(string title, string caption, bool value, Action action)
	{
		Border border = new Border();
		border.Margin = new Thickness(0.0, 0.0, 0.0, 9.0);
		border.Padding = new Thickness(16 * VisualDesign.Density(state.Visual), 14 * VisualDesign.Density(state.Visual), 14, 14 * VisualDesign.Density(state.Visual));
		border.Background = new SolidColorBrush(Surface);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		border.CornerRadius = Radius(14.0);
		Border border2 = border;
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(74.0)
		});
		StackPanel stackPanel = new StackPanel();
		TextBlock settingTitle = T(title, 11.5, TextColor, FontWeights.SemiBold); settingTitle.TextWrapping = TextWrapping.Wrap;
		TextBlock settingCaption = T(caption, 9.5, Muted); settingCaption.TextWrapping = TextWrapping.Wrap;
		stackPanel.Children.Add(settingTitle); stackPanel.Children.Add(settingCaption);
		grid.Children.Add(stackPanel);
		Button button = ToggleButton(value);
		button.Click += delegate
		{
			action();
		};
		Grid.SetColumn(button, 1);
		grid.Children.Add(button);
		border2.Child = grid;
		VisualDesign.ApplyGlassSurface(border2, state.Visual);
		EnableCardMotion(border2);
		return border2;
	}

	private Border ChoiceCard(string title, string caption, bool active, Action action)
	{
		Border border = SettingCard(title, caption, active, action);
		if (active)
		{
			border.Background = new SolidColorBrush(C("#1C1D30"));
			border.BorderBrush = new SolidColorBrush(Accent);
		}
		return border;
	}

	private async Task ShowImportDialogAsync()
    {
        var dialog = new ImportWindow(state.Visual) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { Mouse.OverrideCursor = Cursors.Wait; await ReviewAndApplyImportAsync(dialog.ImportText); }
        catch (Exception) { Toast("Не удалось импортировать данные. Проверьте формат и совместимость параметров.", Warning); }
        finally { Mouse.OverrideCursor = null; }
    }

    private async Task ToggleConnectionAsync()
	{
        if (collectionEditorBusy) { Toast("Закройте редактор коллекции перед подключением.", Warning); return; }
        if (configuredFallbackBusy)
        {
            manualDisconnect = true; connectionRequested = false; reconnectTimer.Stop();
            configuredFallbackCancellation?.Cancel(); core.Stop(); RefreshCurrentPage(); return;
        }
        if (gamingSession != null) { await StopGamingAsync("Игровое подключение отключено."); return; }
        if (gamingCancellation != null) { Toast("Отмените игровую проверку перед подключением.", Warning); return; }
		startupCancellation.Cancel();
		if (profileSwitchInProgress)
		{
            manualDisconnect = true; connectionRequested = false; reconnectTimer.Stop();
            configuredFallbackCancellation?.Cancel(); core.Stop(); RefreshCurrentPage(); return;
		}
		if (zapret.LifecycleState == ZapretLifecycleState.Starting)
		{
			zapret.StopAndVerify();
			ZapretRoutingTransitionService.SetActive(state, false);
			if (state.Zapret != null) state.Zapret.Mode = "VPN";
			StateStore.Save(state);
			RefreshCurrentPage();
			return;
		}
		if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting)
		{
			manualDisconnect = true;
			connectionRequested = false;
			reconnectTimer.Stop();
			RecordHistory("Отключено", "Отключено пользователем.");
			core.Stop();
			if (zapret.IsRunning || string.Equals(state.Zapret?.Mode, "VPN+Zapret", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					zapret.StopAndVerify();
					ZapretRoutingTransitionService.SetActive(state, false);
					if (state.Zapret != null) state.Zapret.Mode = "VPN";
					StateStore.Save(state);
				}
				catch (Exception ex)
				{
					Toast("VPN отключён, но Zapret не удалось полностью остановить: " + LogSanitizer.Sanitize(ex.Message), Danger);
				}
			}
			RefreshCurrentPage();
			return;
		}
		if (!HasConnectionSelection)
		{
			await ShowImportDialogAsync();
			return;
		}
		try
		{
			state.PauseUntilUtc = default(DateTime);
            StateStore.Save(state);
            manualDisconnect = false;
			connectionRequested = true;
			await StartWithConfiguredFallbackAsync();
            var profile = SelectedProfile();
			currentSessionStartedUtc = DateTime.UtcNow;
			RecordHistory("Подключено", "Туннель запущен: " + VpnEngines.DisplayName(core.Engine) + ".");
            if(state.Routing?.MultiServer==null){
                profile.ConsecutiveFailures = 0;
                profile.HealthStatus = "Подключён";
                profile.LastTestMessage = "Туннель запущен: " + VpnEngines.DisplayName(core.Engine) + ".";
            }
			StateStore.Save(state);
			reconnectAttempt = 0;
		}
		catch (Exception ex)
		{
			Toast(ex.Message, Danger);
		}
		RefreshCurrentPage();
	}

	private void StopZapretForPause()
	{
		if (zapret == null) return;
		if (!zapret.IsRunning && !string.Equals(state.Zapret?.Mode, "VPN+Zapret", StringComparison.OrdinalIgnoreCase)) return;
		zapret.StopAndVerify();
		ZapretRoutingTransitionService.SetActive(state, false);
		if (state.Zapret != null) state.Zapret.Mode = "VPN";
	}

	private async Task UpdateSubscriptionsIfDueAsync(bool notify)
	{
		if (!CanReviewSubscriptions() || collectionEditorBusy)
		{
			return;
		}
        NotifySubscriptionQuota();
		bool hasSubscriptions = state.Subscriptions.Count > 0;
		bool updateDue = SubscriptionManager.NextDueUtc(state) <= DateTime.UtcNow;
		if (!hasSubscriptions || !updateDue)
		{
			return;
		}
        var opened=state;string active=core.ActiveProfileId;int generation=core.RuntimeGeneration;var owners=RetiredProfileOwners().ToArray();
		try
		{
			SubscriptionUpdateResult result = await SubscriptionManager.UpdateAllAsync(opened, true, active, mapHealthCancellation.Token,()=>CanCommitBackgroundSubscriptions(opened,active,generation)&&RetiredOwnersUnchanged(owners),owners);
            await ReconcileWorkloadsAsync();
            if(!CanCommitBackgroundSubscriptions(opened,active,generation))return;
            if (currentPage == "routing") UpdateMultiExitHealthPanel();
            NotifySubscriptionQuota();
			if (notify)
			{
				Toast(result.Success ? "Подписки обновлены автоматически." : result.Errors.Count>0 ? string.Join(" ", result.Errors.Take(2)) : "Не все записи подписки распознаны. Прежние серверы сохранены.", result.Success ? Green : Warning);
			}
		}
		catch (Exception ex)
		{
			if (notify&&!closing&&Object.ReferenceEquals(opened,state)) Toast("Не удалось обновить подписки: " + ex.Message, Warning);
		}
	}

	private void SyncSubscriptionTimer()
	{
		if (snapshotMode || closing || subscriptionTimer == null) return;
		bool hasSubscriptions = state.Subscriptions.Count > 0;
		bool connectionBusy = core.Status == CoreStatus.Connecting;
		if (!hasSubscriptions || connectionBusy)
		{
			subscriptionTimer.Stop();
			return;
		}

		DateTime dueUtc = SubscriptionManager.NextDueUtc(state) ?? DateTime.UtcNow;
		TimeSpan untilDue = dueUtc - DateTime.UtcNow;
		if (untilDue < TimeSpan.FromMinutes(1)) untilDue = TimeSpan.FromMinutes(1);
		if (untilDue > TimeSpan.FromDays(1)) untilDue = TimeSpan.FromDays(1);
        if (state.Subscriptions.Any(s => s.Quota != null) && untilDue > TimeSpan.FromHours(1)) untilDue = TimeSpan.FromHours(1);
		subscriptionTimer.Interval = untilDue;
		if (!subscriptionTimer.IsEnabled) subscriptionTimer.Start();
	}

	private void SyncVisualTimerState()
	{
		if (visualTimer == null) return;
		bool shouldRun = IsVisualSamplingAllowed(snapshotMode, closing, currentPage, IsVisible, WindowState == WindowState.Minimized);
		if (!shouldRun)
		{
			visualTimer.Stop();
			lastReceivedBytes = 0;
			lastSentBytes = 0;
			lastTrafficSampleUtc = default(DateTime);
			return;
		}

		visualTimer.Interval = core.Status == CoreStatus.Connected ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10);
		if (!visualTimer.IsEnabled)
		{
			lastReceivedBytes = 0;
			lastSentBytes = 0;
			lastTrafficSampleUtc = default(DateTime);
			visualTimer.Start();
		}
	}

	private static bool IsVisualSamplingAllowed(bool isSnapshot, bool isClosing, string page, bool isVisible, bool isMinimized)
	{
		return !isSnapshot && !isClosing && String.Equals(page, "home", StringComparison.Ordinal) && isVisible && !isMinimized;
	}

	private static bool ShouldCollectNetworkCounters(CoreStatus status)
	{
		return status == CoreStatus.Connected;
	}

	private void ScheduleReconnect()
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
            if (gamingSession != null && GetGamePreferences(gamingSession.GameId).EndOnDisconnect) { _ = StopGamingAsync("Игровое соединение прервано."); return; }
			if (!closing && !profileSwitchInProgress && !configuredFallbackBusy && !startupConnecting && !manualDisconnect && connectionRequested && state.AutoReconnect && reconnectAttempt < 5 && core.Status != CoreStatus.Connected && core.Status != CoreStatus.Connecting)
			{
                RecordHistory("Обрыв", core.LastError);
                if(state.Routing?.MultiServer==null){
				VpnProfile failed = SelectedProfile();
				if (failed != null)
				{
					failed.ConsecutiveFailures++;
					failed.HealthStatus = "Сбой";
					failed.LastTestMessage = core.LastError;
				}
                var fallbackPlan = ConfiguredFallbackService.Build(state.Profiles, failed?.Id, state.FallbackRootProfileId);
                if (fallbackPlan.IsConfigured) fallbackFailedProfileId = failed?.Id;
				if (!fallbackPlan.IsConfigured && state.AutoSelectBestServer && gamingSession == null)
				{
					VpnProfile alternative = CollectionBestAvailable(failed?.Id);
					if (alternative != null)
					{
						state.SelectedProfileId = alternative.Id;
					}
				}
                }
				StateStore.Save(state);
				reconnectTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, reconnectAttempt * 3));
				reconnectTimer.Stop();
				reconnectTimer.Start();
				Toast("Соединение оборвалось. NOVA попробует подключиться снова.", Warning);
			}
		});
	}

	private void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
	{
		base.Dispatcher.BeginInvoke((Action)(() => HandleRecoveryNetworkAvailability(e.IsAvailable)));
	}

    private void CoreStatusChanged(CoreStatus newStatus,string message,int generation)
    {
        if(closing||generation!=core.RuntimeGeneration||newStatus!=core.Status)return;
        long revision=System.Threading.Interlocked.Increment(ref coreStatusNotificationRevision);
		base.Dispatcher.BeginInvoke((Action)delegate
		{
            if(!IsCurrentCoreStatusNotification(newStatus,generation,revision))return;
            ScheduleRetiredProfileCleanup();
            if (!closing && gamingSession != null && !gamingTransitionBusy && newStatus == core.Status &&
                (newStatus == CoreStatus.Disconnected && manualDisconnect || newStatus == CoreStatus.Error && GetGamePreferences(gamingSession.GameId).EndOnDisconnect))
                _ = StopGamingAsync("Игровое подключение завершено.");
			SyncSubscriptionTimer();
			if (newStatus == CoreStatus.Connected) applicationRoutingPending = ApplicationRoutingNeedsApply();
			if (currentPage == "routing") applicationListRefresh?.Invoke();
            UpdateSidebarConnectionStatus();
			if (currentPage == "home")
			{
				UpdateHomeConnectionStatus();
				if (newStatus == CoreStatus.Error) AnimateConnectionError();
			}

			if (newStatus == CoreStatus.Connected)
			{
				if (!snapshotMode && !multiServerApplyBusy && core.Status == CoreStatus.Connected && core.ActiveProfileId != null) { state.LastConnectedProfileId = core.ActiveProfileId; StateStore.Save(state); }
				ShowNotification(gamingSession != null ? "Игровой маршрут включён: " + gamingSession.Game : "VPN подключён: " + (SelectedProfile()?.Name ?? "сервер"));
			}
			else if (newStatus == CoreStatus.Error)
			{
				ShowNotification("Ошибка VPN: " + message);
			}
		});
		_ = RefreshExternalIpForStatusAsync(newStatus,generation,revision);
	}

	private void InitializeTray()
	{
		try
		{
			trayIcon = new Forms.NotifyIcon
			{
				Text = "NOVA VPN",
				Icon = Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
				Visible = true
			};
			Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
			Forms.ToolStripMenuItem open = new Forms.ToolStripMenuItem("Открыть NOVA");
			open.Click += delegate { RestoreFromTray(); };
			Forms.ToolStripMenuItem exit = new Forms.ToolStripMenuItem("Выход");
			exit.Click += delegate { base.Dispatcher.BeginInvoke((Action)Close); };
			menu.Items.Add(open);
			menu.Items.Add(new Forms.ToolStripSeparator());
			menu.Items.Add(exit);
			trayIcon.ContextMenuStrip = menu;
			trayIcon.DoubleClick += delegate { RestoreFromTray(); };
		}
		catch
		{
			trayIcon = null;
		}
	}

	private void RegisterGlobalHotkey()
	{
		try
		{
			WindowInteropHelper helper = new WindowInteropHelper(this);
			hotkeySource = HwndSource.FromHwnd(helper.Handle);
			hotkeySource?.AddHook(HotkeyHook);
			NativeMethods.RegisterHotKey(helper.Handle, HotkeyId, 0x0001u | 0x0002u, (uint)KeyInterop.VirtualKeyFromKey(Key.V));
		}
		catch
		{
		}
	}

	private void UnregisterGlobalHotkey()
	{
		try
		{
			IntPtr handle = new WindowInteropHelper(this).Handle;
			NativeMethods.UnregisterHotKey(handle, HotkeyId);
			hotkeySource?.RemoveHook(HotkeyHook);
		}
		catch
		{
		}
	}

	private IntPtr HotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
		{
			handled = true;
			base.Dispatcher.BeginInvoke((Action)(async delegate { await ToggleConnectionAsync(); }));
		}
		return IntPtr.Zero;
	}

	private static class NativeMethods
	{
		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool UnregisterHotKey(IntPtr window, int id);
	}

	private void RestoreFromTray()
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			base.ShowInTaskbar = true;
			Show();
			base.WindowState = WindowState.Normal;
			Activate();
		});
	}

    private bool IsCurrentCoreStatusNotification(CoreStatus status,int generation,long revision) => !closing&&status==core.Status&&generation==core.RuntimeGeneration&&revision==System.Threading.Volatile.Read(ref coreStatusNotificationRevision);
    private async Task RefreshExternalIpForStatusAsync(CoreStatus status,int generation,long revision)
    {
        if(snapshotMode||!IsCurrentCoreStatusNotification(status,generation,revision)||(status!=CoreStatus.Connected&&status!=CoreStatus.Disconnected))return;
        await ExternalIpService.RefreshAsync(ExternalIpService.GetAsync,
            ()=>IsCurrentCoreStatusNotification(status,generation,revision), async value=>{
        await Dispatcher.InvokeAsync(delegate{
            if(!IsCurrentCoreStatusNotification(status,generation,revision))return;
            if(status==CoreStatus.Connected)tunnelExternalIp=value;else directExternalIp=value;
            if(currentPage=="home"&&liveExternalIpText!=null)liveExternalIpText.Text=core.Status==CoreStatus.Connected?tunnelExternalIp:directExternalIp;
        });
        });
    }
    private RoutingDecision ExplainCurrentRouting(string destination,string process,string network,int? port)
    {
        if(core.Status==CoreStatus.Connecting)return new RoutingDecision{Route="unknown",Reason="Подключение запускается; активные правила ещё не подтверждены."};
        if(core.Status!=CoreStatus.Connected&&!core.CanSelectEngine)return new RoutingDecision{Route="unknown",Reason="Отключение VPN ещё не подтверждено; действующие правила недоступны."};
        if(core.Status==CoreStatus.Connected){
            int generation=core.RuntimeGeneration;var active=core.ActiveRoutingPlan;
            if(active==null)return new RoutingDecision{Route="unknown",Reason="Не удалось подтвердить конфигурацию действующего подключения."};
            var result=active.Explain(destination,process,network,port);
            if(generation!=core.RuntimeGeneration||!Object.ReferenceEquals(active,core.ActiveRoutingPlan))return new RoutingDecision{Route="unknown",Reason="Подключение изменилось во время расчёта. Повторите проверку."};
            result.Reason="По конфигурации действующего подключения. "+result.Reason;return result;
        }
        var preview=CompiledRoutingPlan.CreateFor(SelectedProfile(),EffectiveRouting(),core.Engine,state.Profiles).Explain(destination,process,network,port);
        preview.Reason="Предварительный расчёт текущих настроек. "+preview.Reason;return preview;
    }
    private void ShowNotification(string message) { TryShowNotification(message); }
    private bool TryShowNotification(string message)
    {
        if(!state.ShowNotifications||trayIcon==null||string.IsNullOrWhiteSpace(message))return false;
        try{
            trayIcon.BalloonTipTitle="NOVA VPN";
            trayIcon.BalloonTipText=message.Length>180?message.Substring(0,180):message;
            trayIcon.ShowBalloonTip(2500);return true;
        }catch{return false;}
    }
	private void RefreshCurrentPage()
	{
		string page = currentPage;
		Navigate(page, false);
	}

    private bool HasVerifiedConnection(ConnectionVerification proof)
    {
        if(core.Status != CoreStatus.Connected || !proof.Completed) return false;
        if(proof.MultiExits!=null)return proof.MultiExits.IsFreshFor(state.Profiles,ProfileHealthEvidence.NetworkKey(),ProfileHealthEvidence.CoreKey(core.CorePath),DateTime.UtcNow);
        var profile = state.Profiles.FirstOrDefault(p => p.Id == core.ActiveProfileId);
        return proof.IsVerifiedFor(profile, ProfileHealthEvidence.NetworkKey(), ProfileHealthEvidence.CoreKey(core.CorePath), DateTime.UtcNow);
    }

    private void UpdateSidebarConnectionStatus()
    {
        if(currentPage=="routing")UpdateMultiExitHealthPanel();
        var status = core.Status;
        var proof = core.Verification;
        bool confirmed = HasVerifiedConnection(proof);
        bool failed = proof.Completed && (!proof.InternetOk || proof.ProfileProbe?.Supported == true && !proof.ProfileProbe.Success);
        string text = "Не подключено";
        Color color = Muted;
        if(status == CoreStatus.Connected)
        {
            if(gamingSession != null) text = "Игровой режим";
            else if(confirmed) text = "Защищено";
            else if(!proof.Completed) text = "Проверка…";
            else if(proof.MultiExits!=null) text = "Проверьте выходы";
            else if(!proof.InternetOk) text = "Нет интернета";
            else text = "Туннель включён";
            color = confirmed ? Green : failed ? Danger : Warning;
        }
        else if(status == CoreStatus.Connecting) { text = "Подключение…"; color = Warning; }
        else if(status == CoreStatus.Error) { text = "Ошибка"; color = Danger; }
        if(statusText != null) { statusText.Text = text; statusText.Foreground = new SolidColorBrush(color); }
        if(statusDot != null) statusDot.Background = new SolidColorBrush(color);
    }
	private void UpdateVerificationDisplay()
	{
        UpdateSidebarConnectionStatus();
		if (protectionIndicators == null) return;
		bool tunnelOk = core.Status == CoreStatus.Connected;
		if (connectHeadline != null) connectHeadline.Text = StatusHeadline();
		if (connectCaption != null) connectCaption.Text = StatusCaption();
		protectionIndicators.Children.Clear();
        if(core.LastConnectedRouting?.MultiServer!=null&&tunnelOk) {
            protectionIndicators.Children.Add(StatusPill("TUN","активен",Green));
            if(core.Verification.MultiExits==null)protectionIndicators.Children.Add(StatusPill("ВЫХОДЫ","проверяются",Warning));
            else foreach(var exit in core.Verification.MultiExits.Results) {
                var pill=StatusPill(exit.Exit.Name,exit.Label(DateTime.UtcNow),exit.HasRecentSuccess(DateTime.UtcNow)?Green:Warning);
                pill.ToolTip="Проверка активного снимка подключения. Изменённые конфиги требуют повторного подключения.";
                protectionIndicators.Children.Add(pill);
            }
            return;
        }

		protectionIndicators.Children.Add(StatusPill("TUN", tunnelOk ? "активен" : core.Status == CoreStatus.Connecting ? "запускается" : "выключен", tunnelOk ? Green : core.Status == CoreStatus.Connecting ? Warning : Muted));
		protectionIndicators.Children.Add(StatusPill("DNS", core.Verification.DnsOk ? "проверен" : tunnelOk ? "проверяется" : "не проверен", core.Verification.DnsOk ? Green : tunnelOk ? Warning : Muted));
		protectionIndicators.Children.Add(StatusPill("ИНТЕРНЕТ", core.Verification.InternetOk ? core.Verification.InternetLatencyMs + " мс" : tunnelOk ? core.Verification.Completed ? "проверка не прошла" : "проверяется" : "не проверен", core.Verification.InternetOk ? Green : tunnelOk ? core.Verification.Completed ? Danger : Warning : Muted));
	}

    private void UpdatePausedConnectionLabel()
    {
        if (connectionControl != null && core.Status != CoreStatus.Connected && core.Status != CoreStatus.Connecting
            && state.PauseUntilUtc > DateTime.UtcNow)
            connectionControl.Label.Text = "Возобновить";
    }

	private void UpdateHomeConnectionStatus()
	{
		if (trafficModeStatusText != null) trafficModeStatusText.Text = ZapretStatusCaption();
		connectionControl?.Apply(state.Visual, core.Status, MotionAllowed);
        if (gamingSession != null && connectionControl != null && core.Status == CoreStatus.Connected) connectionControl.Label.Text = "Завершить режим";
        UpdatePausedConnectionLabel();
		UpdateVerificationDisplay();
		if (homePauseButton != null)
		{
			homePauseButton.Content = "Пауза 15 минут";
			homePauseButton.Visibility = core.Status == CoreStatus.Connected && gamingSession == null ? Visibility.Visible : Visibility.Collapsed;
		}
		if (homeBestButton != null) homeBestButton.IsEnabled = state.Profiles.Count > 0 && core.Status != CoreStatus.Connecting && !profileSwitchInProgress;
		if (liveSessionText != null) liveSessionText.Text = SessionLabel();
		if (liveExternalIpText != null) liveExternalIpText.Text = core.Status == CoreStatus.Connected ? tunnelExternalIp : directExternalIp;
		SyncVisualTimerState();
	}

    private bool HasConnectionSelection => state.Routing?.MultiServer != null || SelectedProfile() != null;

	private VpnProfile SelectedProfile()
	{
        if (state.Routing?.MultiServer is MultiServerSettings assignments)
            return state.Profiles.FirstOrDefault(p => p.Id == assignments.DefaultProfileId);
        if(MultiServerRoutingHistory.MissingRestoredProfile(state))return null;
		return state.Profiles.FirstOrDefault((VpnProfile p) => p.Id == state.SelectedProfileId) ?? state.Profiles.FirstOrDefault();
	}

    private bool CanRestoreFullState() => !snapshotMode&&!closing&&!subscriptionReviewBusy&&!SubscriptionManager.IsUpdating&&!startupSettingsBusy&&!startupConnecting&&core.CanSelectEngine&&workloadProxies?.HasEntries!=true&&!collectionEditorBusy&&!configuredFallbackBusy&&!profileSwitchInProgress&&gamingSession==null&&gamingCancellation==null&&!zapret.IsRunning&&!ZapretRoutingTransitionService.HasActiveRouting(state);
    private void RestoreIdentitiesOnly(IdentityState source)
    {
        if(!CanRestoreFullState())throw new InvalidOperationException("Завершите подключения, обновление подписок и редакторы перед переносом идентификации.");
        var plan=FullStateRestorePlan.PrepareIdentitiesOnly(state,source);plan.Apply(state,CanRestoreFullState,StateStore.Save);
    }
    private void RestoreSettingsOnly(AppState source)
    {
        if(!CanRestoreFullState())throw new InvalidOperationException("Завершите VPN, локальные прокси и редакторы перед импортом настроек.");
        var plan=FullStateRestorePlan.PrepareSettingsOnly(state,source);plan.Apply(state,CanRestoreFullState,StateStore.Save);
    }
    private void RestoreFullState(AppState source)
    {
        if(!CanRestoreFullState())throw new InvalidOperationException("Завершите VPN, локальные прокси и редакторы перед восстановлением.");
        var plan=FullStateRestorePlan.Prepare(state,source);plan.Apply(state,CanRestoreFullState,StateStore.Save);
        gamingLibraryLoaded=false;gamingSelected=null;
        try{core.SelectEngine(state.EngineSelection==1?VpnEngine.Reality:VpnEngine.SingBox);core.ConfigureEcoModeForNextStart(state.CoreEcoMode);}
        catch(Exception error){throw new InvalidOperationException("Копия сохранена. Не удалось обновить параметры ядра; перезапустите NOVA перед подключением.",error);}
    }

	private Border AppearanceSummaryCard()
	{
		Border card = new Border { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 9), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(14) };
		Grid grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel copy = new StackPanel(); copy.Children.Add(T(state.Visual.ThemeName, 12.0, TextColor, FontWeights.SemiBold)); copy.Children.Add(T(VisualSummary(), 9.5, Muted)); grid.Children.Add(copy);
		StackPanel swatches = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		Border sample = new Border { Width = 28, Height = 28, Margin = new Thickness(5, 0, 0, 0), Background = new LinearGradientBrush(Surface2, Surface, 35), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), ToolTip = state.Visual.ThemeName };
		swatches.Children.Add(sample);
		Grid.SetColumn(swatches, 1); grid.Children.Add(swatches); card.Child = grid; return card;
	}

	private string VisualSummary()
	{
		return (state.Visual.Density == "Compact" ? "компактно" : state.Visual.Density == "Comfortable" ? "просторно" : "стандартно") + " · текст " + Math.Round(state.Visual.TextScale * 100) + "% · " + (state.Visual.AnimationFrameRate == 0 ? "авто" : state.Visual.AnimationFrameRate + " кадр./с");
	}

	private void RecordHistory(string status, string details)
	{
		VpnProfile profile = SelectedProfile();
		int duration = currentSessionStartedUtc == default(DateTime) ? 0 : (int)Math.Max(0.0, (DateTime.UtcNow - currentSessionStartedUtc).TotalSeconds);
		state.ConnectionHistory.Add(new ConnectionHistoryItem
		{
			TimestampUtc = DateTime.UtcNow,
			Status = status,
			ProfileName = profile?.Name ?? "—",
			DurationSeconds = duration,
			Details = LogSanitizer.Sanitize(details ?? "")
		});
		if (state.ConnectionHistory.Count > 50)
		{
			state.ConnectionHistory.RemoveRange(0, state.ConnectionHistory.Count - 50);
		}
		if (status == "Отключено" || status == "Обрыв" || status == "Пауза")
		{
			currentSessionStartedUtc = default(DateTime);
		}
		StateStore.Save(state);
	}

	private string Greeting()
	{
		int hour = DateTime.Now.Hour;
		if (hour < 6)
		{
			return "Доброй ночи";
		}
		if (hour < 12)
		{
			return "Доброе утро";
		}
		if (hour < 18)
		{
			return "Добрый день";
		}
		return "Добрый вечер";
	}

	private string StatusHeadline()
	{
        if (gamingSession != null && core.Status == CoreStatus.Connected) return "Игровой режим · " + gamingSession.Game;
		if (core.Status == CoreStatus.Connected)
		{
            if(core.LastConnectedRouting?.MultiServer!=null)return !core.Verification.Completed?"Проверяем назначенные серверы":
                HasVerifiedConnection(core.Verification)?"Все выходы проверены":"Проверка выходов не подтверждена";
			return !core.Verification.Completed ? "Проверяем соединение" : !core.Verification.InternetOk ? "Нет доступа в интернет" :
				core.Verification.ProfileProbe?.Supported == true && !core.Verification.ProfileProbe.Success ? "Профиль не прошёл проверку" :
				HasVerifiedConnection(core.Verification) ? "Вы защищены" : "Туннель включён";
		}
		if (core.Status == CoreStatus.Connecting)
		{
			return "Подключаемся";
		}
		if (core.Status == CoreStatus.Error)
		{
			return "Нужна проверка";
		}
		return "Готово к работе";
	}

	private string StatusCaption()
	{
        if (gamingSession != null && core.Status == CoreStatus.Connected) return gamingSession.GameOnly ? "Отдельный конфиг используется только для игровых EXE. Остальные приложения выходят напрямую." : "Игра использует отдельный выход. Остальные приложения работают по обычным правилам NOVA.";
		if (core.Status == CoreStatus.Connected)
		{
            if(core.LastConnectedRouting?.MultiServer!=null)return core.Verification.MultiExits?.Summary??"Отдельный HTTPS-запрос через каждый назначенный сервер. Назначения сохраняются при ошибке проверки.";
			return !core.Verification.Completed ? "Туннель включён. Проверяем доступ в интернет через выбранный профиль." :
				!core.Verification.InternetOk ? "Контрольный HTTPS-запрос не прошёл. Выберите зелёную точку на карте." :
				core.Verification.ProfileProbe?.Supported == true && !core.Verification.ProfileProbe.Success ? "Интернет доступен, но HTTPS через выбранный VPN-профиль не прошёл. Выберите зелёную точку на карте." :
				HasVerifiedConnection(core.Verification) ? "HTTPS через выбранный VPN-профиль подтверждён недавней проверкой." :
				"Интернет доступен, но прохождение HTTPS через этот профиль не подтверждено.";
		}
		if (core.Status == CoreStatus.Connecting)
		{
			return "Создаём защищённый туннель и применяем маршруты.";
		}
		if (core.Status == CoreStatus.Error)
		{
			return "Откройте журнал в диагностике или проверьте ключ.";
		}
		if (HasConnectionSelection)
		{
			return "Нажмите кнопку, чтобы создать защищённый туннель.";
		}
		return "Добавьте ключ — всё остальное NOVA настроит сама.";
	}

	private string RouteLabel()
	{
		if (state.Routing.Mode == "Global")
		{
			return "Весь трафик";
		}
		if (state.Routing.Mode == "Direct")
		{
			return "Только список";
		}
		return "По правилам";
	}

	private string SessionLabel()
	{
		if (core.Status == CoreStatus.Connected && core.ConnectedAtUtc != default(DateTime))
		{
			TimeSpan elapsed = DateTime.UtcNow - core.ConnectedAtUtc;
			return elapsed.TotalHours >= 1.0 ? ((int)elapsed.TotalHours) + " ч " + elapsed.Minutes + " мин" : Math.Max(1, elapsed.Minutes) + " мин";
		}
		if (state.PauseUntilUtc > DateTime.UtcNow)
		{
			return "Пауза до " + state.PauseUntilUtc.ToLocalTime().ToString("HH:mm");
		}
		return "—";
	}

	private void SetStartup(bool enabled)
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (enabled)
			{
				registryKey.SetValue("NovaVPN", "\"" + Assembly.GetExecutingAssembly().Location + "\" --autostart");
			}
			else
			{
				registryKey.DeleteValue("NovaVPN", throwOnMissingValue: false);
			}
		}
		catch (Exception ex)
		{
			Toast("Не удалось изменить автозапуск: " + ex.Message, Danger);
		}
	}

	private void ToggleMaximize()
	{
		base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
	}

	private Grid PageHeader(string title, string subtitle, UIElement action, UIElement secondAction)
	{
		Grid grid = new Grid();
		bool stacked = responsiveMode != "Wide" || state.Visual.TextScale > 1.15;
		if (stacked) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		if (action != null)
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
		}
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(T(title, responsiveMode == "Compact" ? 28 : palette.Name == ThemePalette.OneUi ? 40 : palette.Name == ThemePalette.MaterialYou ? 38 : palette.IsGlass ? 32 : 28, TextColor, palette.Name == ThemePalette.OneUi ? FontWeights.Bold : FontWeights.SemiBold));
		stackPanel.Margin = new Thickness(0, palette.Name == ThemePalette.OneUi && responsiveMode == "Wide" ? 38 : 0, 12, palette.Name == ThemePalette.OneUi && responsiveMode == "Wide" ? 26 : 0);
		TextBlock textBlock = T(subtitle, 10.5, Muted);
		textBlock.Margin = new Thickness(1.0, 4.0, 0.0, 0.0);
		stackPanel.Children.Add(textBlock);
		grid.Children.Add(stackPanel);
		if (action != null)
		{
			Grid.SetColumn(action, stacked ? 0 : 1);
			if (stacked) { Grid.SetRow(action, 1); ((FrameworkElement)action).Margin = new Thickness(0,12,0,0); }
			if (action is FrameworkElement frameworkElement)
			{
				frameworkElement.VerticalAlignment = VerticalAlignment.Center;
			}
			grid.Children.Add(action);
		}
		return grid;
	}

	private UIElement InfoCard(string label, string value, string hint, int column)
	{
		int visibleCards = (state.Visual.HomeCardOrder ?? new List<string>()).Count(key => !state.Visual.HiddenHomeCards.Contains(key));
		bool compact = visibleCards > 3;
		Border border = new Border();
		border.Margin = new Thickness((column != 0) ? 6 : 0, 0.0, (column != 2) ? 6 : 0, 0.0);
		border.Padding = new Thickness(17.0, 14.0, 17.0, 14.0);
		border.Background = new SolidColorBrush(Surface);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		border.CornerRadius = Radius(15.0);
		Border border2 = border;
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 0.0 : 90.0) });
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(Label(label));
		TextBlock textBlock = T(value, 15.0, TextColor, FontWeights.SemiBold);
		if (label == "СЕАНС") liveSessionText = textBlock;
		if (label == "ВНЕШНИЙ IP") liveExternalIpText = textBlock;
		textBlock.Margin = new Thickness(0.0, 5.0, 0.0, 0.0);
		stackPanel.Children.Add(textBlock);
		grid.Children.Add(stackPanel);
		TextBlock textBlock2 = T(hint, 9.5, Muted);
		textBlock2.VerticalAlignment = VerticalAlignment.Center;
		textBlock2.TextAlignment = TextAlignment.Right;
		textBlock2.TextWrapping = TextWrapping.Wrap;
		Grid.SetColumn(textBlock2, 1);
		if (!compact) grid.Children.Add(textBlock2);
		border2.Child = grid;
		Grid.SetColumn(border2, column);
		return border2;
	}

	private UIElement StatusMini(string label, string value)
	{
		StackPanel stackPanel = new StackPanel();
		stackPanel.Width = 108.0;
		StackPanel stackPanel2 = stackPanel;
		stackPanel2.Children.Add(Label(label));
		TextBlock textBlock = T(value, 10.5, TextColor, FontWeights.SemiBold);
		textBlock.Margin = new Thickness(0.0, 3.0, 0.0, 0.0);
		stackPanel2.Children.Add(textBlock);
		return stackPanel2;
	}

	private Border StatusPill(string label, string value, Color color)
	{
		Border pill = new Border
		{
			Margin = new Thickness(0.0, 0.0, 6.0, 5.0),
			Padding = new Thickness(10.0, 5.0, 10.0, 5.0),
			CornerRadius = Radius(14.0),
			Background = new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		pill.Child = T("●  " + label + " · " + value, 9.5, color, FontWeights.SemiBold);
		return pill;
	}

	private Border ProtocolMark(string text)
	{
		string text2 = text switch
		{
			"SING-BOX" => "SB",
			"HYSTERIA2" => "HY",
			"SHADOWSOCKS" => "SS",
			_ => (text.Length > 2) ? text.Substring(0, 2) : text,
		};
		Border border = new Border();
		border.Width = 38.0;
		border.Height = 38.0;
		border.CornerRadius = Radius(12.0);
		border.Background = new LinearGradientBrush(C("#2D2850"), C("#1F3044"), 45.0);
		border.BorderBrush = new SolidColorBrush(C("#514A80"));
		border.BorderThickness = new Thickness(1.0);
		Border border2 = border;
		border2.Child = new TextBlock
		{
			Text = text2,
			FontSize = 9.5,
			FontWeight = FontWeights.Bold,
			Foreground = new SolidColorBrush(AccentLight),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		return border2;
	}

	private Border RegionMark(VpnProfile profile)
	{
		string code = ServerGroupingService.CountryCode(profile);
		Border mark = new Border { Width = 40, Height = 40, CornerRadius = Radius(12), Background = new LinearGradientBrush(Color.FromArgb(220, Accent.R, Accent.G, Accent.B), Color.FromArgb(220, Teal.R, Teal.G, Teal.B), 35), BorderBrush = new SolidColorBrush(Color.FromArgb(100, TextColor.R, TextColor.G, TextColor.B)), BorderThickness = new Thickness(1), ToolTip = ServerGroupingService.CountryLabel(profile) };
		mark.Child = T(code, 10.0, Colors.White, FontWeights.Bold);
		((TextBlock)mark.Child).HorizontalAlignment = HorizontalAlignment.Center; ((TextBlock)mark.Child).VerticalAlignment = VerticalAlignment.Center;
		return mark;
	}

	private UIElement Metric(string label, string value, Color color, int column)
	{
		StackPanel panel = new StackPanel(); panel.Children.Add(Label(label)); TextBlock number = T(value, 11, color, FontWeights.SemiBold); number.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(number); Grid.SetColumn(panel, column); return panel;
	}

	private Color LatencyColor(int latency)
	{
		return latency < 0 ? Muted : latency <= 120 ? Green : latency <= 260 ? Warning : Danger;
	}

	private Border EmptyState(string glyph, string title, string caption)
	{
		Border border = new Border();
		border.Padding = new Thickness(30.0, 48.0, 30.0, 48.0);
		border.Background = new SolidColorBrush(Surface);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		border.CornerRadius = Radius(18.0);
		Border border2 = border;
		StackPanel stackPanel = new StackPanel();
		stackPanel.HorizontalAlignment = HorizontalAlignment.Center;
		StackPanel stackPanel2 = stackPanel;
		Viewbox textBlock = IconFactory.Create(glyph, 34.0, AccentLight);
		textBlock.HorizontalAlignment = HorizontalAlignment.Center;
		stackPanel2.Children.Add(textBlock);
		TextBlock textBlock2 = T(title, 15.0, TextColor, FontWeights.SemiBold);
		textBlock2.Margin = new Thickness(0.0, 14.0, 0.0, 5.0);
		textBlock2.HorizontalAlignment = HorizontalAlignment.Center;
		stackPanel2.Children.Add(textBlock2);
		stackPanel2.Children.Add(T(caption, 10.5, Muted));
		border2.Child = stackPanel2;
		return border2;
	}

	private TextBlock SectionTitle(string text)
	{
		return SectionTitle(text, new Thickness(0.0, 0.0, 0.0, 0.0));
	}

	private TextBlock SectionTitle(string text, Thickness margin)
	{
		TextBlock textBlock = Label(text);
		textBlock.Margin = margin;
		return textBlock;
	}

	private TextBlock Label(string text)
	{
		return T(text, 8.5, C("#6F788A"), FontWeights.Bold);
	}

	private Button PrimaryButton(string text, double width)
	{
		Button button = new Button();
		button.Content = text;
		button.MinWidth = width;
		button.Padding = new Thickness(14, 0, 14, 0);
		button.Height = Math.Max(34, 32 * state.Visual.TextScale);
		button.Background = new SolidColorBrush(Accent);
		button.Foreground = new SolidColorBrush(palette.AccentForeground);
		button.BorderThickness = new Thickness(0.0);
		button.FontSize = 12.0 * state.Visual.TextScale;
		button.FontWeight = FontWeights.SemiBold;
		button.Cursor = Cursors.Hand;
		button.Template = ButtonTemplate(11.0);
		EnableButtonMotion(button);
		return button;
	}

	private Button SmallButton(string text)
	{
		Button button = new Button();
		button.Content = text;
		button.Height = Math.Max(34, 32 * state.Visual.TextScale);
		button.MinWidth = 76.0;
		button.Padding = new Thickness(13.0, 0.0, 13.0, 0.0);
		button.Background = new SolidColorBrush(Surface2);
		button.Foreground = new SolidColorBrush(C("#BBC2CF"));
		button.BorderBrush = new SolidColorBrush(BorderColor);
		button.BorderThickness = new Thickness(1.0);
		button.FontSize = 13 * state.Visual.TextScale;
		button.Cursor = Cursors.Hand;
		button.Template = ButtonTemplate(12.0);
		EnableButtonMotion(button);
		return button;
	}

	private Button IconButton(string text)
	{
		Button button = new Button();
		button.Content = text;
		button.Width = 48.0;
		button.Height = 48.0;
		button.Background = Brushes.Transparent;
		button.Foreground = new SolidColorBrush(Muted);
		button.BorderThickness = new Thickness(0.0);
		button.FontSize = 18.0;
		button.Cursor = Cursors.Hand;
		button.Template = ButtonTemplate(9.0);
		EnableButtonMotion(button);
		return button;
	}

	private Button ToggleButton(bool on)
	{
		Button button = new Button();
		button.Width = 64.0;
		button.Height = 48.0;
		button.Background = Brushes.Transparent;
		button.BorderThickness = new Thickness(0.0);
		button.Cursor = Cursors.Hand;
		button.Template = VisualDesign.ButtonTemplate(state.Visual, 14, false);
		button.HorizontalAlignment = HorizontalAlignment.Right;
		button.VerticalAlignment = VerticalAlignment.Center;
		button.ToolTip = on ? "Включено" : "Выключено";
		button.Content = CreatePillSwitch(on ? Accent : palette.Name == ThemePalette.OneUi ? palette.SoftSelection : Surface2,
			on ? AccentLight : BorderColor, on ? HorizontalAlignment.Right : HorizontalAlignment.Left, 50, 27);
		EnableButtonMotion(button);
		return button;
	}

	private static Border CreatePillSwitch(Color fill, Color outline, HorizontalAlignment thumbAlignment, double width, double height)
	{
		Border track = new Border
		{
			Width = width,
			Height = height,
			CornerRadius = new CornerRadius(height / 2),
			Background = new SolidColorBrush(fill),
			BorderBrush = new SolidColorBrush(outline),
			BorderThickness = new Thickness(1)
		};
		Grid inner = new Grid();
		inner.Children.Add(new Ellipse
		{
			Width = height - 9,
			Height = height - 9,
			Fill = Brushes.White,
			Margin = new Thickness(4),
			Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.2 },
			HorizontalAlignment = thumbAlignment,
			VerticalAlignment = VerticalAlignment.Center
		});
		track.Child = inner;
		return track;
	}

	private Button VectorIconButton(string icon, string tooltip, Color? color = null, bool filled = false)
	{
		Button button = new Button { Content = IconFactory.Create(icon, 19, color ?? Muted, filled), Width = 48, Height = 48, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = tooltip, Template = ButtonTemplate(12) };
		EnableButtonMotion(button); return button;
	}

	private void ShowBusySkeleton(string title, int count)
	{
		Grid page = new Grid(); page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) }); page.RowDefinitions.Add(new RowDefinition()); page.Children.Add(PageHeader(title, "Подготавливаем свежие данные…", null, null));
		StackPanel bars = new StackPanel();
		for (int i = 0; i < count; i++)
		{
			Border row = new Border { Height = state.Visual.Density == "Compact" ? 58 : 72, Margin = new Thickness(0, 0, 0, 10), CornerRadius = Radius(15), Background = new LinearGradientBrush(Surface, Surface2, 0), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), Opacity = 0.55 };
			if (MotionAllowed && !state.Visual.ReduceMotion) BeginMotionAnimation(row, OpacityProperty, new DoubleAnimation(0.45, 0.9, TimeSpan.FromMilliseconds(AnimMs(650 + i * 55))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
			bars.Children.Add(row);
		}
		ScrollViewer scroll = TouchScrollViewer(new ScrollViewer { Content = bars, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Grid.SetRow(scroll, 2); page.Children.Add(scroll); contentHost.Children.Clear(); contentHost.Children.Add(page);
	}

	private void UpdateLiveVisuals()
	{
		if (!IsVisualSamplingAllowed(snapshotMode, closing, currentPage, IsVisible, WindowState == WindowState.Minimized)) return;
		try
		{
			long received = 0, sent = 0;
			bool connected = ShouldCollectNetworkCounters(core.Status);
			if (connected) foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
				try { IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics(); received += stats.BytesReceived; sent += stats.BytesSent; } catch { }
			}
			DateTime now = DateTime.UtcNow;
			double seconds = lastTrafficSampleUtc == default(DateTime) ? 0 : Math.Max(0.1, (now - lastTrafficSampleUtc).TotalSeconds);
			double down = connected && lastReceivedBytes > 0 && received >= lastReceivedBytes ? (received - lastReceivedBytes) / seconds : 0;
			double up = connected && lastSentBytes > 0 && sent >= lastSentBytes ? (sent - lastSentBytes) / seconds : 0;
			lastReceivedBytes = connected ? received : 0;
			lastSentBytes = connected ? sent : 0;
			lastTrafficSampleUtc = connected ? now : default(DateTime);
			AppendSample(downloadSamples, down); AppendSample(uploadSamples, up);
			if (liveDownloadText != null) liveDownloadText.Text = "↓  " + FormatRate(down);
			if (liveUploadText != null) liveUploadText.Text = "↑  " + FormatRate(up);
			if (liveSessionText != null) liveSessionText.Text = SessionLabel();
			RedrawTrafficGraph();
		}
		catch { }
	}

	private static void AppendSample(List<double> samples, double value)
	{
		samples.Add(Math.Max(0, value)); if (samples.Count > 60) samples.RemoveAt(0);
	}

	private string CurrentSpeedLabel()
	{
		return FormatRate(downloadSamples.LastOrDefault()) + " ↓";
	}

	private static string FormatRate(double bytesPerSecond)
	{
		if (bytesPerSecond >= 1024 * 1024) return (bytesPerSecond / 1024 / 1024).ToString("0.0") + " МБ/с";
		if (bytesPerSecond >= 1024) return (bytesPerSecond / 1024).ToString("0") + " КБ/с";
		return Math.Round(bytesPerSecond).ToString("0") + " Б/с";
	}

	private void RedrawTrafficGraph()
	{
		if (trafficCanvas == null || downloadLine == null || uploadLine == null || trafficCanvas.ActualWidth < 2) return;
		double width = trafficCanvas.ActualWidth, height = Math.Max(20, trafficCanvas.ActualHeight);
		double max = Math.Max(1, downloadSamples.Concat(uploadSamples).DefaultIfEmpty(1).Max());
		downloadLine.Points = ToPoints(downloadSamples, width, height, max); uploadLine.Points = ToPoints(uploadSamples, width, height, max);
	}

	private static PointCollection ToPoints(IList<double> samples, double width, double height, double max)
	{
		PointCollection points = new PointCollection(); if (samples.Count == 0) return points;
		for (int i = 0; i < samples.Count; i++) points.Add(new Point(samples.Count == 1 ? width : i * width / (samples.Count - 1), height - 3 - Math.Min(1, samples[i] / max) * (height - 7)));
		return points;
	}

	private void EnableButtonMotion(Button button)
	{
		if (button == null) return;
		button.Focusable = true;
		Brush normalBorder = button.BorderBrush;
		Thickness normalThickness = button.BorderThickness;
		button.GotKeyboardFocus += delegate { button.BorderBrush = new SolidColorBrush(AccentLight); button.BorderThickness = new Thickness(Math.Max(1.5, normalThickness.Left)); };
		button.LostKeyboardFocus += delegate { button.BorderBrush = normalBorder; button.BorderThickness = normalThickness; };
		if (!MotionAllowed || state.Visual.ReduceMotion)
		{
			return;
		}
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		button.RenderTransformOrigin = new Point(0.5, 0.5);
		button.RenderTransform = scale;
		button.MouseEnter += delegate
		{
			BeginMotionAnimation(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1.008, TimeSpan.FromMilliseconds(AnimMs(180.0))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
			BeginMotionAnimation(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1.008, TimeSpan.FromMilliseconds(AnimMs(180.0))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
		};
		button.MouseLeave += delegate
		{
			BeginMotionAnimation(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1.0, TimeSpan.FromMilliseconds(AnimMs(120.0))));
			BeginMotionAnimation(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1.0, TimeSpan.FromMilliseconds(AnimMs(120.0))));
		};
		button.PreviewMouseLeftButtonDown += delegate { BeginMotionAnimation(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 0.99, TimeSpan.FromMilliseconds(AnimMs(110)))); BeginMotionAnimation(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 0.99, TimeSpan.FromMilliseconds(AnimMs(110)))); };
		button.PreviewMouseLeftButtonUp += delegate { BeginMotionAnimation(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1.0, TimeSpan.FromMilliseconds(AnimMs(100)))); BeginMotionAnimation(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1.0, TimeSpan.FromMilliseconds(AnimMs(100)))); };
	}

	private void EnableCardMotion(Border card)
	{
		if (card == null || !MotionAllowed || state.Visual.ReduceMotion) return;
		TranslateTransform movement = new TranslateTransform(); card.RenderTransform = movement;
		card.MouseEnter += delegate
		{
			BeginMotionAnimation(movement, TranslateTransform.YProperty, new DoubleAnimation(movement.Y, -1, TimeSpan.FromMilliseconds(AnimMs(200))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
			// Avoid allocating a blur effect as cards move under the pointer while scrolling.
		};
		card.MouseLeave += delegate
		{
			BeginMotionAnimation(movement, TranslateTransform.YProperty, new DoubleAnimation(movement.Y, 0, TimeSpan.FromMilliseconds(AnimMs(160))));
			card.Effect = null;
		};
	}

	private async Task AnimateIconPopAsync(Button button)
	{
		if (!MotionAllowed || state.Visual.ReduceMotion || !(button?.Content is FrameworkElement icon)) return;
		ScaleTransform scale = new ScaleTransform(1, 1); icon.RenderTransformOrigin = new Point(0.5, 0.5); icon.RenderTransform = scale;
		BeginMotionAnimation(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.35, TimeSpan.FromMilliseconds(AnimMs(90))) { AutoReverse = true });
		BeginMotionAnimation(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.35, TimeSpan.FromMilliseconds(AnimMs(90))) { AutoReverse = true });
		await Task.Delay((int)AnimMs(170));
	}

	private void Toast(string message, Color color)
	{
		Border toast = new Border
		{
			Background = new SolidColorBrush(C("#212632")),
			BorderBrush = new SolidColorBrush(color),
			BorderThickness = new Thickness(1.0),
			CornerRadius = Radius(12.0),
			Padding = new Thickness(16.0, 11.0, 16.0, 11.0),
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			MaxWidth = 470.0,
			Opacity = 0.0
		};
		toast.Child = T(message, 10.5, TextColor, FontWeights.SemiBold);
		TranslateTransform toastSlide = new TranslateTransform(18, 0); toast.RenderTransform = toastSlide;
		contentHost.Children.Add(toast);
		Panel.SetZIndex(toast, 100);
		if (MotionAllowed && !state.Visual.ReduceMotion)
		{
			BeginMotionAnimation(toast, UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160.0)));
			BeginMotionAnimation(toastSlide, TranslateTransform.XProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(AnimMs(220))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
		}
		else toast.Opacity = 1.0;
		DispatcherTimer timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(3.2)
		};
		timer.Tick += delegate
		{
			timer.Stop();
			DoubleAnimation doubleAnimation = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(220.0));
			doubleAnimation.Completed += delegate
			{
				contentHost.Children.Remove(toast);
			};
			if (MotionAllowed && !state.Visual.ReduceMotion) BeginMotionAnimation(toast, UIElement.OpacityProperty, doubleAnimation);
			else contentHost.Children.Remove(toast);
		};
		timer.Start();
	}

	private void AnimatePage(UIElement element)
	{
		if (snapshotMode || !MotionAllowed || state.Visual.ReduceMotion || element == null)
		{
			return;
		}
		FrameworkElement view = element as FrameworkElement;
		if (view == null)
		{
			return;
		}
		TranslateTransform transform = new TranslateTransform(0.0, 10.0);
		view.RenderTransform = transform;
		view.Opacity = 0.0;
		BeginMotionAnimation(view, UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(AnimMs(180.0))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
		BeginMotionAnimation(transform, TranslateTransform.YProperty, new DoubleAnimation(10.0, 0.0, TimeSpan.FromMilliseconds(AnimMs(220.0))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
	}


	private void AnimateConnectionError()
	{
		if (!MotionAllowed || state.Visual.ReduceMotion || connectButton == null) return;
		TranslateTransform shake = new TranslateTransform(); connectionControl.RenderTransform = shake;
		DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(AnimMs(360)) };
		animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
		animation.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromPercent(0.2)));
		animation.KeyFrames.Add(new EasingDoubleKeyFrame(7, KeyTime.FromPercent(0.4)));
		animation.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(0.65)));
		animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
		BeginMotionAnimation(shake, TranslateTransform.XProperty, animation);
	}

	private TextBlock T(string text, double size, Color color)
	{
		return T(text, size, color, FontWeights.Normal);
	}

	private TextBlock T(string text, double size, Color color, FontWeight weight)
	{
		TextBlock textBlock = new TextBlock();
		textBlock.Text = text;
		textBlock.FontSize = Math.Max(12.0, size) * (state?.Visual?.TextScale ?? 1.0);
		textBlock.FontWeight = size < 12 && weight == FontWeights.SemiBold ? FontWeights.Medium : weight;
		TextOptions.SetTextRenderingMode(textBlock, TextRenderingMode.Grayscale);
		TextOptions.SetTextFormattingMode(textBlock, TextFormattingMode.Display);
		textBlock.Foreground = new SolidColorBrush(color);
		textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
		return textBlock;
	}


	// Surface tint is frozen; glass samples the decorative backdrop in a separate layer.
	private Brush SoftSurface(bool prominent) => VisualDesign.Surface(state.Visual, prominent);

	private ControlTemplate ButtonTemplate(double radius) => VisualDesign.ButtonTemplate(state.Visual, radius);

	private Color C(string hex)
	{
		return palette == null ? ThemePalette.Parse(hex) : palette.Map(hex);
	}

	private CornerRadius Radius(double designRadius)
	{
		if (designRadius <= 0.0) return new CornerRadius(0.0);
		double profileScale = palette.Name == ThemePalette.Windows || palette.Name == ThemePalette.WindowsLight || palette.Name == ThemePalette.WindowsDark ? .35 : palette.Name == ThemePalette.OneUi ? 1.1 : palette.Name == ThemePalette.MaterialYou ? 1.25 : 1.0;
		return new CornerRadius(Math.Max(0.0, state.Visual.CornerRadius * designRadius / 16.0 * profileScale));
	}

	private bool MotionAllowed => state.ShowAnimations && !snapshotMode && !state.Visual.ReduceMotion && (!state.Visual.FollowSystemMotion || SystemParameters.ClientAreaAnimation);

	private double AnimMs(double milliseconds)
	{
		return milliseconds / Math.Max(0.5, Math.Min(2.0, state.Visual.AnimationSpeed));
	}

	private void BeginMotionAnimation(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
	{
		if (target == null || animation == null) return;
		int? frameRate = state?.Visual?.AnimationFrameRate == 0 ? (int?)null : state?.Visual?.AnimationFrameRate ?? 120;
		Timeline.SetDesiredFrameRate(animation, frameRate);
		target.BeginAnimation(property, animation);
	}

	private static TElement FindVisualChild<TElement>(DependencyObject parent) where TElement : DependencyObject
	{
		if (parent == null) return null;
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is TElement match) return match;
			TElement nested = FindVisualChild<TElement>(child);
			if (nested != null) return nested;
		}
		return null;
	}
}

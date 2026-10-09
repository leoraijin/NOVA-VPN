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

public sealed class MainWindow : Window
{
	private sealed class NavItem
	{
		public Button Button;

		public Border Indicator;

		public Viewbox Icon;

		public TextBlock Label;
	}

	private sealed class LearningLesson
	{
		public string Title;
		public string Summary;
		public string[] Steps;
		public string Important;
		public string Target;
		public string TargetLabel;
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

	private int learningLessonIndex;

	private TextBlock statusText;

	private Border statusDot;

	private Button connectButton;
	private ConnectionControl connectionControl;
	private Button homePauseButton;
	private Button homeBestButton;
	private TextBlock liveExternalIpText;

	private TextBlock connectButtonLabel;

	private TextBlock connectHeadline;
	private StackPanel protectionIndicators;

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

	private Forms.ToolStripMenuItem trayConnectItem;

	private HwndSource hotkeySource;

	private const int HotkeyId = 0x4E56;

	private const int WmHotkey = 0x0312;

	private DateTime currentSessionStartedUtc;

	private bool snapshotMode;

	public MainWindow(bool snapshot = false)
	{
		snapshotMode = snapshot;
		state = snapshotMode ? StateStore.CreateEphemeral() : StateStore.Load();
		if (snapshotMode) PrepareSnapshotData();
		ApplyVisualPalette();
		core = new CoreManager();
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
		core.StatusChanged += CoreStatusChanged;
		core.VerificationChanged += delegate
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				if (!closing && currentPage == "home") UpdateVerificationDisplay();
			});
		};
		core.UnexpectedExit += ScheduleReconnect;
		reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
		reconnectTimer.Tick += async delegate
		{
			reconnectTimer.Stop();
			if (!closing && !manualDisconnect && state.AutoReconnect && SelectedProfile() != null && core.Status != CoreStatus.Connected)
			{
				try
				{
					reconnectAttempt++;
					await core.StartAsync(SelectedProfile(), EffectiveRouting());
					currentSessionStartedUtc = DateTime.UtcNow;
					RecordHistory("Переподключено", "Туннель восстановлен автоматически.");
					VpnProfile connected = SelectedProfile();
					if (connected != null)
					{
						connected.ConsecutiveFailures = 0;
						connected.HealthStatus = "Подключён";
					}
					StateStore.Save(state);
					reconnectAttempt = 0;
				}
				catch
				{
					if (reconnectAttempt < 5)
					{
						reconnectTimer.Interval = TimeSpan.FromSeconds(Math.Min(20, reconnectAttempt * 3));
						reconnectTimer.Start();
					}
				}
			}
		};
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
		base.Title = "NOVA VPN";
		// Explicit window icon is also used when Windows pins a running WPF window.
		// Do not depend on the shell extracting it from a custom-chrome window.
		base.Icon = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/nova-v2.ico", UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(frame => frame.PixelWidth).First();
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
		if (!snapshotMode)
		{
			InitializeTray();
			base.SourceInitialized += delegate { RegisterGlobalHotkey(); };
		}
		Navigate("home");
		RoutedEventHandler value = async delegate
		{
			if (state.ShowTrainingOnStartup && state.Profiles.Count == 0 && currentPage == "home")
			{
				Navigate("learning");
			}
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
			if (state.AutoConnect && SelectedProfile() != null && state.PauseUntilUtc <= DateTime.UtcNow)
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
			closing = true;
			reconnectTimer.Stop();
			subscriptionTimer.Stop();
			visualTimer.Stop();
			if (!snapshotMode)
			{
				NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
			}
			UnregisterGlobalHotkey();
			core.Dispose();
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

	private UIElement BuildSidebar()
	{
		bool compact = responsiveMode == "Compact";
		Grid grid = new Grid();
		grid.Background = new SolidColorBrush(Sidebar);
		Grid grid2 = grid;
		grid2.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(compact ? 82.0 : 118.0)
		});
		grid2.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		grid2.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(compact ? 68.0 : 92.0)
		});
		Grid grid3 = new Grid();
		grid3.Margin = compact ? new Thickness(15.0, 13.0, 15.0, 12.0) : new Thickness(25.0, 25.0, 20.0, 22.0);
		Grid grid4 = grid3;
		grid4.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(50.0)
		});
		grid4.ColumnDefinitions.Add(new ColumnDefinition());
		Border border = new Border();
		border.Width = 40.0;
		border.Height = 40.0;
		border.CornerRadius = Radius(13.0);
		border.Background = Brushes.Transparent;
		Border border2 = border;
		try { border2.Child = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/nova-mark.png", UriKind.Absolute)), Stretch = Stretch.Uniform }; }
		catch { border2.Child = IconFactory.Create("shield", 25, AccentLight); }
		grid4.Children.Add(border2);
		if (!compact)
		{
			StackPanel stackPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
			stackPanel.Children.Add(T("NOVA", 16.0, TextColor, FontWeights.SemiBold));
			TextBlock brandCaption = T("PRIVATE NETWORK", 8.5, Muted, FontWeights.SemiBold);
			brandCaption.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
			stackPanel.Children.Add(brandCaption);
			Grid.SetColumn(stackPanel, 1);
			grid4.Children.Add(stackPanel);
		}
		grid2.Children.Add(grid4);
		StackPanel stackPanel3 = new StackPanel();
		stackPanel3.Margin = compact ? new Thickness(8.0, 5.0, 8.0, 0.0) : new Thickness(14.0, 4.0, 14.0, 0.0);
		StackPanel stackPanel4 = stackPanel3;
		stackPanel4.Children.Add(Nav("home", "home", "Главная"));
		stackPanel4.Children.Add(Nav("servers", "servers", "Серверы"));
		stackPanel4.Children.Add(Nav("routing", "routing", "Маршрутизация"));
		stackPanel4.Children.Add(Nav("diagnostics", "diagnostics", "Диагностика"));
		stackPanel4.Children.Add(Nav("settings", "settings", "Настройки"));
		stackPanel4.Children.Add(Nav("learning", "list", "Обучение"));
		Grid.SetRow(stackPanel4, 1);
		grid2.Children.Add(stackPanel4);
		Border border3 = new Border();
		border3.Margin = compact ? new Thickness(10.0, 0.0, 10.0, 12.0) : new Thickness(20.0, 0.0, 20.0, 22.0);
		border3.Padding = compact ? new Thickness(8.0, 8.0, 8.0, 8.0) : new Thickness(12.0, 10.0, 12.0, 10.0);
		border3.Background = new SolidColorBrush(Surface);
		border3.CornerRadius = Radius(12.0);
		Border border4 = border3;
		Grid grid5 = new Grid();
		if (compact) grid5.ColumnDefinitions.Add(new ColumnDefinition());
		else
		{
			grid5.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14.0) });
			grid5.ColumnDefinitions.Add(new ColumnDefinition());
		}
		statusDot = new Border
		{
			Width = 7.0,
			Height = 7.0,
			CornerRadius = new CornerRadius(4.0),
			Background = new SolidColorBrush(Muted),
			HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center
		};
		grid5.Children.Add(statusDot);
		if (!compact)
		{
			StackPanel stackPanel5 = new StackPanel();
			statusText = T("Не подключено", 11.0, Muted, FontWeights.SemiBold);
			stackPanel5.Children.Add(statusText);
			stackPanel5.Children.Add(T("Ядро sing-box", 9.0, C("#626A7A")));
			Grid.SetColumn(stackPanel5, 1);
			grid5.Children.Add(stackPanel5);
			border4.ToolTip = "Состояние NOVA VPN";
		}
		else
		{
			AutomationProperties.SetName(border4, "Состояние NOVA VPN");
			statusText = null;
		}
		border4.Child = grid5;
		Grid.SetRow(border4, 2);
		grid2.Children.Add(border4);
		return grid2;
	}

	private UIElement BuildTitlebar()
	{
		Grid grid = new Grid();
		grid.Background = new SolidColorBrush(Bg);
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
		TextBlock textBlock = T("Ключи защищены Windows", 10.0, C("#687184"));
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
		Grid outer = new Grid { Background = new SolidColorBrush(Bg) };
		UIElement backdrop = BuildBackdrop();
		if (backdrop != null) outer.Children.Add(backdrop);
		Grid app = new Grid();
		double sidebarWidth = responsiveMode == "Compact" ? 72.0 : responsiveMode == "Balanced" ? 202.0 : state.Visual.Density == "Compact" ? 205.0 : state.Visual.Density == "Comfortable" ? 244.0 : 228.0;
		if (palette.IsGlass) sidebarWidth += 20;
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
		shellRoot = outer;
		base.Content = outer;
		Navigate(currentPage);
	}

	private UIElement BuildBackdrop() => VisualDesign.Backdrop(state.Visual);

	private static ScrollViewer TouchScrollViewer(ScrollViewer viewer)
	{
		ScrollViewer.SetPanningMode(viewer, PanningMode.VerticalOnly);
		return viewer;
	}

	private UIElement Nav(string key, string icon, string label)
	{
		bool compact = responsiveMode == "Compact";
		Button button = new Button();
		button.Height = Math.Max(52.0, (compact ? 54.0 : palette.Name == ThemePalette.OneUi ? 62.0 : 56.0) * VisualDesign.Density(state.Visual));
		button.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		button.Background = Brushes.Transparent;
		button.BorderThickness = new Thickness(0.0);
		button.Cursor = Cursors.Hand;
		button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
		button.Template = ButtonTemplate(12.0);
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
		border.Width = 3.0;
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
		textBlock2.VerticalAlignment = VerticalAlignment.Center;
		if (compact) textBlock2.Visibility = Visibility.Collapsed;
		Grid.SetColumn(textBlock2, 2);
		grid.Children.Add(textBlock2);
		button2.Content = grid;
		button2.Click += delegate
		{
			Navigate(key);
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
			"learning" => BuildLearningPage(),
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
	}

	public void ShowPageForSnapshot(string page)
	{
		Navigate(string.IsNullOrWhiteSpace(page) ? "home" : page);
	}

	private UIElement BuildLearningPage()
	{
		List<LearningLesson> lessons = LearningLessons();
		learningLessonIndex = Math.Max(0, Math.Min(lessons.Count - 1, learningLessonIndex));
		LearningLesson selected = lessons[learningLessonIndex];
		Grid page = new Grid();
		page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
		page.RowDefinitions.Add(new RowDefinition());
		page.Children.Add(PageHeader("Обучение", "Пошаговое руководство по основным функциям NOVA VPN", null, null));
		Grid body = new Grid();
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
		body.ColumnDefinitions.Add(new ColumnDefinition());
		StackPanel menu = new StackPanel();
		menu.Children.Add(SectionTitle("УРОКИ", new Thickness(2, 0, 0, 10)));
		for (int i = 0; i < lessons.Count; i++)
		{
			int index = i;
			Button item = SmallButton((i + 1) + ".  " + lessons[i].Title);
			item.HorizontalContentAlignment = HorizontalAlignment.Left;
			item.HorizontalAlignment = HorizontalAlignment.Stretch;
			item.Height = 38;
			item.Margin = new Thickness(0, 0, 0, 7);
			item.Padding = new Thickness(13, 0, 8, 0);
			item.Background = new SolidColorBrush(i == learningLessonIndex ? C("#2A2350") : Surface);
			item.BorderBrush = new SolidColorBrush(i == learningLessonIndex ? Accent : BorderColor);
			item.Click += delegate { learningLessonIndex = index; Navigate("learning"); };
			menu.Children.Add(item);
		}
		Border menuCard = new Border { Padding = new Thickness(14), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(16), Child = menu };
		body.Children.Add(menuCard);
		Grid.SetColumn(menuCard, 0);
		Border lessonCard = new Border { Padding = new Thickness(25), Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(18) };
		StackPanel content = new StackPanel();
		content.Children.Add(Label("УРОК " + (learningLessonIndex + 1) + " ИЗ " + lessons.Count));
		content.Children.Add(T(selected.Title, 25, TextColor, FontWeights.SemiBold));
		TextBlock summary = T(selected.Summary, 12, Muted);
		summary.TextWrapping = TextWrapping.Wrap;
		summary.Margin = new Thickness(0, 8, 0, 20);
		content.Children.Add(summary);
		for (int i = 0; i < selected.Steps.Length; i++)
		{
			Grid step = new Grid { Margin = new Thickness(0, 0, 0, 13) };
			step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
			step.ColumnDefinitions.Add(new ColumnDefinition());
			Border number = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(C("#2A2350")), Child = T((i + 1).ToString(), 11, AccentLight, FontWeights.SemiBold) };
			number.Child.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
			number.Child.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
			step.Children.Add(number);
			TextBlock stepText = T(selected.Steps[i], 12, TextColor);
			stepText.TextWrapping = TextWrapping.Wrap;
			Grid.SetColumn(stepText, 1);
			step.Children.Add(stepText);
			content.Children.Add(step);
		}
		Border important = new Border { Padding = new Thickness(14), Margin = new Thickness(0, 7, 0, 18), Background = new SolidColorBrush(C("#202233")), BorderBrush = new SolidColorBrush(Warning), BorderThickness = new Thickness(1), CornerRadius = Radius(12) };
		TextBlock importantText = T("Важно: " + selected.Important, 11, TextColor);
		importantText.TextWrapping = TextWrapping.Wrap;
		important.Child = importantText;
		content.Children.Add(important);
		WrapPanel actions = new WrapPanel();
		Button previous = SmallButton("← Назад");
		previous.IsEnabled = learningLessonIndex > 0;
		previous.Click += delegate { learningLessonIndex--; Navigate("learning"); };
		Button next = SmallButton(learningLessonIndex == lessons.Count - 1 ? "Завершить" : "Далее →");
		next.Margin = new Thickness(8, 0, 8, 0);
		next.Click += delegate { learningLessonIndex = (learningLessonIndex + 1) % lessons.Count; Navigate("learning"); };
		actions.Children.Add(previous);
		actions.Children.Add(next);
		if (!string.IsNullOrWhiteSpace(selected.Target))
		{
			Button open = SmallButton(selected.TargetLabel ?? "Открыть раздел");
			open.Click += delegate { Navigate(selected.Target); };
			actions.Children.Add(open);
		}
		content.Children.Add(actions);
		lessonCard.Child = content;
		body.Children.Add(lessonCard);
		Grid.SetColumn(lessonCard, 2);
		Grid.SetRow(body, 2);
		page.Children.Add(body);
		return page;
	}

	private List<LearningLesson> LearningLessons()
	{
		return new List<LearningLesson>
		{
			new LearningLesson { Title = "Первое подключение", Summary = "Как добавить профиль и запустить защищённое соединение.", Steps = new[] { "Откройте раздел «Серверы» и нажмите «Добавить ключ».", "Вставьте VPN-ссылку, JSON-конфигурацию или ссылку на подписку.", "Выберите профиль и нажмите кнопку питания на главной странице.", "Проверьте индикаторы TUN, DNS и Интернет: они должны перейти в активное состояние." }, Important = "Не передавайте VPN-ссылку посторонним: она может содержать пароль или UUID.", Target = "servers", TargetLabel = "Открыть серверы" },
			new LearningLesson { Title = "Серверы и лучший конфиг", Summary = "Как проверять профили и выбирать самый быстрый стабильный сервер.", Steps = new[] { "На странице «Серверы» нажмите «Проверить все», чтобы обновить задержку и потери.", "Нажмите «Выбрать лучший», чтобы NOVA выбрала доступный профиль с минимальным стабильным пингом.", "Автоматический выбор перед подключением можно включить или отключить в настройках.", "Сервер с самым маленьким пингом не всегда лучший при высокой потере пакетов." }, Important = "Выбор другого профиля при подключённом VPN автоматически перезапускает туннель; если новый конфиг не поднимется, NOVA попробует вернуть прежний.", Target = "servers", TargetLabel = "Открыть серверы" },
			new LearningLesson { Title = "Маршрутизация", Summary = "Как направлять разные сайты и приложения через VPN или напрямую.", Steps = new[] { "В разделе «Маршрутизация» выберите режим: по правилам, весь трафик или только выбранные через VPN.", "В списке программ найдите приложение по названию или выберите его .exe. Переключатель последовательно задаёт маршрут по умолчанию, VPN или напрямую.", "При активном VPN нажмите «Применить маршруты»: NOVA проверит конфигурацию и переподключит туннель; при ошибке попробует вернуть предыдущий маршрут.", "Добавляйте домены в списки «Через VPN», «Напрямую» или «Блокировать». Для сложных случаев доступны текстовые правила по имени процесса и пути.", "Используйте проверку маршрута, чтобы увидеть, какое правило сработало." }, Important = "При активном Zapret маршруты, временно перемещённые напрямую, нельзя редактировать. Выключите Zapret — исходные VPN-правила вернутся. Пути к программам на другом ПК нужно выбрать заново.", Target = "routing", TargetLabel = "Открыть маршрутизацию" },
			new LearningLesson { Title = "VPN и Zapret", Summary = "Разница между тремя режимами обхода и правильное переключение.", Steps = new[] { "VPN запускает только sing-box: домены и приложения используют выбранные VPN-маршруты.", "VPN + Zapret запускает оба компонента. Домены из списка Zapret и выбранные VPN-маршруты приложений физически перемещаются из VPN-списков в «Напрямую».", "Zapret запускает только обход DPI без VPN-туннеля, сохраняя эти записи напрямую на время работы Zapret.", "При переходе в режим VPN приложение останавливает winws, возвращает записи в исходные VPN-списки и затем запускает туннель." }, Important = "Для запуска или остановки повышенного процесса Windows может запросить права администратора.", Target = "home", TargetLabel = "Открыть режимы" },
			new LearningLesson { Title = "Домены Zapret", Summary = "Домены и приложения автоматически переносятся между списками маршрутизации при переключении Zapret.", Steps = new[] { "В режиме VPN YouTube, Discord и остальные домены Zapret идут по правилам VPN.", "При включении Zapret домены из его списков добавляются в реальный список «Напрямую», а совпадающие VPN-домены удаляются из списка «Через VPN».", "Явные VPN-правила приложений также перемещаются в «Напрямую», чтобы приложения не отправляли этот трафик обратно в VPN.", "Исходные VPN-правила сохраняются отдельно и автоматически возвращаются при выключении Zapret или выходе из приложения.", "В режиме «Только Zapret» VPN-туннель отключён; Zapret обрабатывает трафик самостоятельно." }, Important = "Списки маршрутов временно заблокированы для редактирования, пока активен Zapret, чтобы переход можно было точно отменить.", Target = "home", TargetLabel = "Открыть Zapret" },
			new LearningLesson { Title = "Обновление Zapret", Summary = "Проверка версии и установка выполняются раздельно.", Steps = new[] { "Нажмите «Проверка обновлений»: NOVA только сравнит версии и ничего не будет скачивать или устанавливать.", "Если найдена новая версия, нажмите отдельную кнопку «Обновить Zapret», чтобы подтвердить установку.", "Архив проверяется, безопасно распаковывается и устанавливается с резервной копией.", "При ошибке приложение пытается вернуть предыдущую рабочую версию." }, Important = "Только кнопка «Обновить Zapret» останавливает текущий процесс на время установки; после обновления он может быть запущен снова.", Target = "home", TargetLabel = "Открыть обновление" },
			new LearningLesson { Title = "Перенос и безопасность", Summary = "Выберите перенос с VPN-ключами или без них.", Steps = new[] { "Зашифрованный бэкап открывается только в текущей учётной записи Windows.", "Полный .nova-config переносит VPN-профили и секреты — храните его как пароль.", "Кнопка «Экспорт настроек без ключей» переносит оформление, маршруты и настройки Zapret без профилей VPN.", "Файл .nova-settings можно импортировать на другом компьютере; VPN-серверы добавляются там отдельно." }, Important = "Установщик NOVA не содержит ваших VPN-ключей. Для обмена конфигурациями используйте экспорт без ключей.", Target = "settings", TargetLabel = "Открыть настройки" },
			new LearningLesson { Title = "Настройки и диагностика", Summary = "Где находятся автоматические функции и как искать причины ошибок.", Steps = new[] { "В настройках можно включить автоподключение, автопереподключение и выбор лучшего сервера.", "Защищённый DNS и строгий маршрут помогают уменьшить утечки DNS.", "В разделе «Диагностика» проверяйте ядро, профиль, DNS, сеть и конфигурацию.", "Если проблема сохраняется, сохраните текст ошибки и результаты проверки." }, Important = "Kill Switch в текущей версии использует строгую маршрутизацию и автопереподключение; постоянные правила Windows Firewall не создаются.", Target = "diagnostics", TargetLabel = "Открыть диагностику" }
		};
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
		Button pauseButton = SmallButton(core.Status == CoreStatus.Connected ? "Пауза 15 минут" : "Возобновить");
		homePauseButton = pauseButton;
		pauseButton.Visibility = core.Status == CoreStatus.Connected || state.PauseUntilUtc > DateTime.UtcNow ? Visibility.Visible : Visibility.Collapsed;
		pauseButton.Click += async delegate
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
			else if (state.PauseUntilUtc > DateTime.UtcNow)
			{
				state.PauseUntilUtc = default(DateTime); StateStore.Save(state);
				await ToggleConnectionAsync();
			}
		};
		WrapPanel headerActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
		if (pauseButton != null) { pauseButton.Margin = new Thickness(0, 0, 8, 0); headerActions.Children.Add(pauseButton); }
		Button chooseBestHome = SmallButton("⚡  Выбрать лучший конфиг");
		homeBestButton = chooseBestHome;
		chooseBestHome.IsEnabled = state.Profiles.Count > 0 && core.Status != CoreStatus.Connecting && !profileSwitchInProgress;
		chooseBestHome.ToolTip = "Проверить серверы и выбрать конфиг с минимальным стабильным пингом";
		chooseBestHome.Click += async delegate { await ChooseBestProfileAsync(); };
		headerActions.Children.Add(chooseBestHome);
		Grid element = PageHeader(Greeting(), "Ваше защищенное соединение", headerActions, null);
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
		stackPanel2.Children.Add(Label("АКТИВНЫЙ СЕРВЕР"));
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
		stackPanel4.Children.Add(T((vpnProfile == null) ? "Добавить сервер" : vpnProfile.Name, 13.0, TextColor, FontWeights.SemiBold));
		stackPanel4.Children.Add(T((vpnProfile == null) ? "Вставьте ключ или подписку" : vpnProfile.Endpoint, 10.5, Muted));
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
		connectButtonLabel = connectionControl.Label;
		connectButton.Click += async delegate { await ToggleConnectionAsync(); };
		Viewbox grid5 = new Viewbox { Width = connectionFrameSize, Height = connectionFrameSize, Child = connectionControl, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		StackPanel stackPanel9 = new StackPanel();
		stackPanel9.VerticalAlignment = VerticalAlignment.Center;
		stackPanel9.Margin = new Thickness(24.0, 0.0, 0.0, 0.0);
		StackPanel stackPanel10 = stackPanel9;
		stackPanel10.Children.Add(Label("СОСТОЯНИЕ"));
		connectHeadline = T(StatusHeadline(), responsiveMode == "Wide" ? 24.0 : 20.0, TextColor, FontWeights.SemiBold);
		connectHeadline.Margin = new Thickness(0.0, 11.0, 0.0, 5.0);
		stackPanel10.Children.Add(connectHeadline);
		connectCaption = T(StatusCaption(), palette.IsGlass ? 13.0 : 11.0, Muted);
		connectCaption.TextWrapping = TextWrapping.Wrap;
		connectCaption.MaxWidth = 230.0;
		connectCaption.HorizontalAlignment = HorizontalAlignment.Left;
		stackPanel10.Children.Add(connectCaption);
		StackPanel protection = new StackPanel { Margin = new Thickness(0.0, 12.0, 0.0, 0.0) };
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
		border2.Child = grid2;
		VisualDesign.ApplyGlassSurface(border2, state.Visual);
		grid.Children.Add(border2);
		Border zapretPanel = BuildZapretPanel(vpnProfile);
		Grid.SetRow(zapretPanel, 4);
		grid.Children.Add(zapretPanel);
		UIElement insights = BuildHomeInsights(vpnProfile);
		Grid.SetRow(insights, 6);
		grid.Children.Add(insights);
		return TouchScrollViewer(new ScrollViewer
		{
			Content = grid,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Padding = new Thickness(0, 0, 8, 0)
		});
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
			modeButton.Foreground = isSelected ? Brushes.White : new SolidColorBrush(TextColor);
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

		Canvas map = ServerRegionMapService.BuildThemedCanvas(region, 250, 132, palette);
		map.Margin = new Thickness(0, 5, 0, 0);
		System.Windows.Automation.AutomationProperties.SetName(map, region.HasCountry
			? "Географическая карта региона VPN-сервера: " + region.CountryName + (region.CityName.Length > 0 ? ", " + region.CityName : "")
			: "Офлайн-карта мира; страна сервера в профиле не определена");
		panel.Children.Add(map);
		return panel;
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
		if (zapret.LifecycleState == ZapretLifecycleState.Starting) zapret.StopAndVerify();
		await trafficModeGate.WaitAsync();
		try
		{
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
			if (mode != "Zapret" && SelectedProfile() != null)
			{
				manualDisconnect = false;
				connectionRequested = true;
				await core.StartAsync(SelectedProfile(), EffectiveRouting());
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
			connectionRequested = SelectedProfile() != null;
			StateStore.Save(state);
			if (SelectedProfile() != null)
			{
				try { await core.StartAsync(SelectedProfile(), EffectiveRouting()); }
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
				AppReleaseInfo latest;
				try
				{
					Toast("Ищу последнюю версию на GitHub…", Muted);
					latest = await updater.GetLatestAsync();
				}
				catch
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppUpdateService.LatestReleasePageUrl) { UseShellExecute = true });
					Toast("Не удалось получить прямую ссылку. Открыта страница релизов GitHub.", Warning);
					return;
				}
				Version installedVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
				if (AppUpdateService.IsOlderThanInstalled(installedVersion, latest.Version))
				{
					Toast("GitHub пока предлагает более старую версию " + latest.Version + ". Откат не выполнялся.", Warning);
					return;
				}
				try
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(latest.DownloadUrl) { UseShellExecute = true });
					Toast("Открыто скачивание NOVA VPN " + latest.Version + " с GitHub.", Green);
				}
				catch
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(latest.ReleaseUrl) { UseShellExecute = true });
					Toast("Не удалось открыть прямое скачивание. Открыта страница релиза GitHub.", Warning);
				}
			}
		}
		catch (Exception ex)
		{
			Toast("Не удалось открыть обновление NOVA VPN: " + LogSanitizer.Sanitize(ex.Message), Danger);
		}
		finally
		{
			appUpdateInProgress = false;
			if (!closing) button.IsEnabled = true;
		}
	}

	private async Task<bool> CheckApplicationUpdatesOnStartupAsync()
	{
		if (!ShouldCheckForApplicationUpdates(snapshotMode, closing, appUpdateInProgress)) return false;
		appUpdateInProgress = true;
		string installerPath = null;
		bool installerStarted = false;
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

				string updateDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NOVA VPN Updates");
				installerPath = System.IO.Path.Combine(updateDirectory, "NOVA VPN Setup " + latest.Version.ToString(3) + " " + Guid.NewGuid().ToString("N") + ".exe");
				Toast("Скачиваю установщик NOVA VPN " + latest.Version.ToString(3) + "…", Muted);
				await updater.DownloadInstallerAsync(latest, installerPath);

				string installDirectory = System.IO.Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
				int currentProcessId;
				using (System.Diagnostics.Process currentProcess = System.Diagnostics.Process.GetCurrentProcess())
					currentProcessId = currentProcess.Id;
				ProcessStartInfo startInfo = new ProcessStartInfo(installerPath)
				{
					Arguments = "--update-path " + AppUpdateService.QuoteWindowsArgument(installDirectory) + " --wait-pid " + currentProcessId + " --delete-self-on-reboot",
					UseShellExecute = true,
					Verb = "runas",
					WorkingDirectory = updateDirectory
				};
				using (System.Diagnostics.Process updateProcess = System.Diagnostics.Process.Start(startInfo))
				{
					if (updateProcess == null) throw new InvalidOperationException("Не удалось запустить установщик обновления.");
				}
				installerStarted = true;
				Application.Current.Shutdown();
				return true;
			}
		}
		catch (Exception ex)
		{
			if (!installerStarted && !String.IsNullOrWhiteSpace(installerPath))
			{
				try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
			}
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
		await trafficModeGate.WaitAsync();
		bool restartVpn = core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting;
		try
		{
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
			if (restartVpn && SelectedProfile() != null)
			{
				manualDisconnect = false;
				connectionRequested = true;
				await core.StartAsync(SelectedProfile(), EffectiveRouting());
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
			if (restartVpn && SelectedProfile() != null && core.Status != CoreStatus.Connected && core.Status != CoreStatus.Connecting)
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
					await core.StartAsync(SelectedProfile(), EffectiveRouting());
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
		return RoutingOverlayService.Clone(state.Routing);
	}

	public void ConfigureSnapshot(string theme, string background, string density, string serverView, double textScale, bool highContrast)
	{
		if (!snapshotMode) return;
		if (!string.IsNullOrWhiteSpace(theme)) state.Visual.ThemeName = theme;
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
			new ApplicationEntry { Name = "Discord", ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "discord.exe"), Source = "Пример" },
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
				case "Latency": cards.Children.Add(InfoCard("ЗАДЕРЖКА", profile == null || profile.LastLatency < 0 ? "—" : profile.LastLatency + " мс", "До VPN-сервера", i)); break;
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
		StackPanel node = new StackPanel { Width = 44 }; Viewbox glyph = IconFactory.Create(icon, 16, color); glyph.HorizontalAlignment = HorizontalAlignment.Center; node.Children.Add(glyph); TextBlock copy = T(label, 7.5, color, FontWeights.SemiBold); copy.HorizontalAlignment = HorizontalAlignment.Center; copy.Margin = new Thickness(0, 3, 0, 0); node.Children.Add(copy); return node;
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
		ShowBusySkeleton("Ищем сервер с минимальным пингом", Math.Max(3, Math.Min(7, state.Profiles.Count)));
		await Dispatcher.Yield(DispatcherPriority.Background);
		await HealthCheckService.TestAllAsync(state.Profiles);
		VpnProfile best = HealthCheckService.BestAvailable(state.Profiles);
		if (best == null) Toast("Доступные серверы не найдены.", Warning);
		else await SelectProfileAsync(best);
		Navigate("servers");
	}

	private async Task SelectProfileAsync(VpnProfile profile)
	{
		if (profile == null || closing) return;
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
				state.SelectedProfileId = profile.Id;
				StateStore.Save(state);
				Toast("Выбран конфиг: " + profile.Name, Green);
				return;
			}

			RecordHistory("Переключение", "Автоматический перезапуск туннеля на конфиге «" + profile.Name + "».");
			reconnectTimer.Stop();
			manualDisconnect = true;
			connectionRequested = false;
			ShowBusySkeleton("Переключаем VPN на «" + profile.Name + "»", 3);
			await Dispatcher.Yield(DispatcherPriority.Background);

			ProfileSwitchResult result = await ProfileSwitchService.SwitchAsync(
				state,
				profile,
				() => core.Stop(),
				async target =>
				{
					await core.StartAsync(target, EffectiveRouting());
					if (core.Status != CoreStatus.Connected)
						throw new InvalidOperationException(string.IsNullOrWhiteSpace(core.LastError) ? "Новое VPN-подключение не запустилось." : core.LastError);
				},
				() => StateStore.Save(state));
			StateStore.Save(state);

			if (result.TargetConnected)
			{
				manualDisconnect = false;
				connectionRequested = true;
				currentSessionStartedUtc = DateTime.UtcNow;
				result.TargetProfile.ConsecutiveFailures = 0;
				result.TargetProfile.HealthStatus = "Подключён";
				result.TargetProfile.LastTestMessage = "Конфиг подключён автоматически после переключения.";
				reconnectAttempt = 0;
				RecordHistory("Переключено", "Соединение автоматически восстановлено на новом конфиге.");
				Toast("VPN переключён на «" + result.TargetProfile.Name + "».", Green);
			}
			else if (result.PreviousRestored)
			{
				manualDisconnect = false;
				connectionRequested = true;
				currentSessionStartedUtc = DateTime.UtcNow;
				result.PreviousProfile.ConsecutiveFailures++;
				result.PreviousProfile.HealthStatus = "Подключён";
				result.PreviousProfile.LastTestMessage = "Сохранённое подключение восстановлено после ошибки нового конфига.";
				Toast("Не удалось подключить «" + profile.Name + "». Возвращён прежний конфиг «" + result.PreviousProfile.Name + "»: " + LogSanitizer.Sanitize(result.TargetError?.Message), Warning);
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
		Button addButton = PrimaryButton("+  Добавить ключ", 145.0);
		addButton.Click += async delegate
		{
			await ShowImportDialogAsync();
		};
		Button testAll = SmallButton("Проверить все");
		testAll.IsEnabled = !profileSwitchInProgress && core.Status != CoreStatus.Connecting;
		testAll.Click += async delegate
		{
			testAll.IsEnabled = false;
			testAll.Content = "Проверяем…";
			ShowBusySkeleton("Проверяем серверы", Math.Max(3, Math.Min(7, state.Profiles.Count)));
			await Dispatcher.Yield(DispatcherPriority.Background);
			await HealthCheckService.TestAllAsync(state.Profiles);
			StateStore.Save(state);
			VpnProfile best = HealthCheckService.BestAvailable(state.Profiles);
			if (best != null && state.AutoSelectBestServer) await SelectProfileAsync(best);
			else Toast(best == null ? "Доступные серверы не найдены." : "Лучший сервер: " + best.Name + " · " + best.LastLatency + " мс", best == null ? Warning : Green);
			Navigate("servers");
		};
		Button chooseBest = SmallButton("Выбрать лучший");
		chooseBest.IsEnabled = !profileSwitchInProgress && core.Status != CoreStatus.Connecting;
		chooseBest.Click += async delegate
		{
			chooseBest.IsEnabled = false;
			chooseBest.Content = "Выбираем…";
			ShowBusySkeleton("Ищем сервер с минимальным пингом", Math.Max(3, Math.Min(7, state.Profiles.Count)));
			await Dispatcher.Yield(DispatcherPriority.Background);
			await HealthCheckService.TestAllAsync(state.Profiles);
			VpnProfile best = HealthCheckService.BestAvailable(state.Profiles);
			if (best == null) Toast("Доступные серверы не найдены.", Warning);
			else await SelectProfileAsync(best);
			Navigate("servers");
		};
		Button update = SmallButton("Обновить подписки");
		update.IsEnabled = state.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.SubscriptionUrl));
		update.Click += async delegate
		{
			update.IsEnabled = false;
			update.Content = "Обновляем…";
			ShowBusySkeleton("Обновляем подписки", 4);
			await Dispatcher.Yield(DispatcherPriority.Background);
			SubscriptionUpdateResult result = await SubscriptionManager.UpdateAllAsync(state);
			Toast(result.Success ? "Подписки обновлены: " + result.UpdatedProfiles + " серверов." : string.Join(" ", result.Errors.Take(2)), result.Success ? Green : Warning);
			Navigate("servers");
		};
		WrapPanel actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 620 };
		Button viewMode = SmallButton(state.Visual.ServerViewMode == "Grid" ? "▦  Сетка" : state.Visual.ServerViewMode == "Compact" ? "☷  Компактно" : "☰  Подробно");
		viewMode.ToolTip = "Переключить вид серверов";
		viewMode.Click += delegate { state.Visual.ServerViewMode = state.Visual.ServerViewMode == "Detailed" ? "Compact" : state.Visual.ServerViewMode == "Compact" ? "Grid" : "Detailed"; StateStore.Save(state); Navigate("servers"); };
		viewMode.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		testAll.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		chooseBest.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		update.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		actions.Children.Add(viewMode);
		actions.Children.Add(testAll);
		actions.Children.Add(chooseBest);
		actions.Children.Add(update);
		actions.Children.Add(addButton);
		grid.Children.Add(PageHeader("Серверы", state.Profiles.Count + " профилей · " + state.Profiles.Count(p => p.IsFavorite) + " избранных", actions, null));
		StackPanel stackPanel = new StackPanel();
		if (state.Profiles.Count == 0)
		{
			Border border = EmptyState("servers", "Здесь появятся ваши серверы", "Вставьте одиночный VPN-ключ или ссылку на подписку.");
			border.Margin = new Thickness(0.0, 36.0, 0.0, 0.0);
			stackPanel.Children.Add(border);
		}
		else
		{
			var groups = state.Profiles.Where(p => p != null).GroupBy(ServerGroupingService.CountryLabel).OrderBy(group => group.Key == "Другие" ? 1 : 0).ThenBy(group => group.Key);
			foreach (var group in groups)
			{
				TextBlock groupTitle = SectionTitle(group.Key.ToUpperInvariant() + " · " + group.Count());
				groupTitle.Margin = new Thickness(2.0, stackPanel.Children.Count == 0 ? 0.0 : 12.0, 0.0, 8.0);
				stackPanel.Children.Add(groupTitle);
				IEnumerable<VpnProfile> sorted = group.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.LastLatency > 0 ? p.LastLatency : int.MaxValue);
				if (state.Visual.ServerViewMode == "Grid")
				{
					WrapPanel cards = new WrapPanel { Orientation = Orientation.Horizontal };
					foreach (VpnProfile profile in sorted) cards.Children.Add(ServerGridCard(profile));
					stackPanel.Children.Add(cards);
				}
				else foreach (VpnProfile profile in sorted) stackPanel.Children.Add(ServerCard(profile));
			}
		}
		ScrollViewer scrollViewer = TouchScrollViewer(new ScrollViewer());
		scrollViewer.Content = stackPanel;
		scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
		ScrollViewer element = scrollViewer;
		Grid.SetRow(element, 2);
		grid.Children.Add(element);
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
			Width = new GridLength(112.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(96.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(38.0)
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
			TextBlock textBlock = T("АКТИВНЫЙ", 8.0, AccentLight, FontWeights.Bold);
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
		if (!compact) stackPanel2.Children.Add(provider);
		TextBlock health = T(profile.HealthStatus ?? "Не проверен", 9.0, profile.ConsecutiveFailures > 0 ? Danger : profile.LastLatency > 0 ? Green : Muted);
		health.ToolTip = profile.LastTestMessage;
		if (!compact) { stackPanel2.Children.Add(health); stackPanel2.Children.Add(T(profile.LastLatency > 0 ? "Потери " + profile.LastLossPercent + "% · средняя " + profile.AverageLatency + " мс" : "Проверка ещё не выполнялась", 8.5, profile.LastLossPercent > 0 ? Warning : Muted)); }
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
			TextPromptWindow dialog = new TextPromptWindow("Название сервера", "Введите понятное название профиля", profile.Name, state.Visual) { Owner = this };
			if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
			{
				profile.Name = dialog.Value;
				StateStore.Save(state);
				Navigate("servers");
			}
		};
		Grid.SetColumn(edit, 4);
		grid.Children.Add(edit);
		Button favorite = VectorIconButton("star", profile.IsFavorite ? "Убрать из избранного" : "Добавить в избранное", profile.IsFavorite ? Warning : Muted, profile.IsFavorite);
		favorite.ToolTip = profile.IsFavorite ? "Убрать из избранного" : "Добавить в избранное";
		favorite.Foreground = new SolidColorBrush(profile.IsFavorite ? Warning : Muted);
		favorite.Click += async delegate(object sender, RoutedEventArgs e)
		{
			e.Handled = true;
			await AnimateIconPopAsync(favorite);
			profile.IsFavorite = !profile.IsFavorite;
			StateStore.Save(state);
			Navigate("servers");
		};
		Grid.SetColumn(favorite, 5);
		grid.Children.Add(favorite);
		Button button = VectorIconButton("trash", "Удалить", Danger);
		button.ToolTip = "Удалить";
		button.Click += delegate(object sender, RoutedEventArgs e)
		{
			e.Handled = true;
			if ((core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting) && selected)
			{
				Toast("Сначала отключите активный сервер.", Danger);
			}
			else
			{
				state.Profiles.Remove(profile);
				if (state.SelectedProfileId == profile.Id)
				{
					state.SelectedProfileId = ((state.Profiles.Count > 0) ? state.Profiles[0].Id : null);
				}
				StateStore.Save(state);
				Navigate("servers");
			}
		};
		Grid.SetColumn(button, 6);
		grid.Children.Add(button);
		border2.Child = grid;
		border2.MouseLeftButtonUp += async delegate { await SelectProfileAsync(profile); };
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
		Button favorite = VectorIconButton("star", "Избранное", profile.IsFavorite ? Warning : Muted, profile.IsFavorite); favorite.Click += async delegate(object sender, RoutedEventArgs e) { e.Handled = true; await AnimateIconPopAsync(favorite); profile.IsFavorite = !profile.IsFavorite; StateStore.Save(state); Navigate("servers"); }; Grid.SetColumn(favorite, 2); top.Children.Add(favorite); grid.Children.Add(top);
		Grid metrics = new Grid { Margin = new Thickness(0, 14, 0, 0) }; metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition());
		metrics.Children.Add(Metric("ЗАДЕРЖКА", profile.LastLatency > 0 ? profile.LastLatency + " мс" : "—", LatencyColor(profile.LastLatency), 0)); metrics.Children.Add(Metric("ПОТЕРИ", profile.LastLatency > 0 ? profile.LastLossPercent + "%" : "—", profile.LastLossPercent > 0 ? Warning : Green, 1)); metrics.Children.Add(Metric("СТАТУС", profile.HealthStatus ?? "Не проверен", profile.ConsecutiveFailures > 0 ? Danger : profile.LastLatency > 0 ? Green : Muted, 2)); Grid.SetRow(metrics, 1); grid.Children.Add(metrics); card.Child = grid;
		VisualDesign.ApplyGlassSurface(card, state.Visual);
		card.MouseLeftButtonUp += async delegate { await SelectProfileAsync(profile); }; EnableCardMotion(card); return card;
	}

	private UIElement BuildRoutingPage()
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
		grid.Children.Add(PageHeader("Маршрутизация", "Что направлять через VPN", importRules, null));
		StackPanel stackPanel = new StackPanel();
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
		stackPanel.Children.Add(SectionTitle("ПРИЛОЖЕНИЯ", new Thickness(0.0, 0.0, 0.0, 10.0)));
		stackPanel.Children.Add(BuildApplicationRoutingSection());
		StackPanel presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0.0, 0.0, 0.0, 18.0) };
		presets.Children.Add(PresetButton("Мессенджеры через VPN", "messengers"));
		presets.Children.Add(PresetButton("Игры напрямую", "games-direct"));
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
		stackPanel.Children.Add(grid4);
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
		stackPanel.Children.Add(T("Подсказка: домен *.example.com включает все его поддомены. Одно правило — одна строка.", 10.5, Muted));
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
			Padding = new Thickness(16), Background = new SolidColorBrush(Surface),
			BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1),
			CornerRadius = Radius(14)
		};
		StackPanel content = new StackPanel();
		content.Children.Add(T("Выберите программу и нажимайте переключатель: по умолчанию → VPN → напрямую.", 10.5, TextColor));
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
			Height = 380, Background = new SolidColorBrush(Surface2),
			BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1),
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		VirtualizingStackPanel.SetIsVirtualizing(results, true);
		ScrollViewer.SetCanContentScroll(results, true);
		ScrollViewer.SetPanningMode(results, PanningMode.VerticalOnly);
		content.Children.Add(results);
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
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
		row.ColumnDefinitions.Add(new ColumnDefinition());
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
		ImageSource icon = GetApplicationIcon(entry.ExecutablePath);
		if (icon != null) row.Children.Add(new Image { Source = icon, Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
		else row.Children.Add(IconFactory.Create("servers", 25, Muted));
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
		StackPanel control = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
		TextBlock label = T(RouteLabel(route), 9.0, route == ApplicationRoutingService.Proxy ? AccentLight : Muted, FontWeights.SemiBold);
		label.HorizontalAlignment = HorizontalAlignment.Center;
		control.Children.Add(label);
		Border track = new Border
		{
			Width = 50, Height = 27, CornerRadius = Radius(14),
			Background = new SolidColorBrush(route == ApplicationRoutingService.Proxy ? Accent : route == ApplicationRoutingService.Direct ? Teal : BorderColor)
		};
		track.Child = new Ellipse
		{
			Width = 17, Height = 17, Fill = Brushes.White, Margin = new Thickness(3),
			HorizontalAlignment = route == ApplicationRoutingService.Proxy ? HorizontalAlignment.Right : route == ApplicationRoutingService.Direct ? HorizontalAlignment.Left : HorizontalAlignment.Center
		};
		Button toggle = new Button
		{
			Content = track, Width = 64, Height = 48, BorderThickness = new Thickness(0),
			Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(0),
			ToolTip = state.Zapret?.RoutingChangesApplied == true
				? "Отключите Zapret, чтобы менять маршруты приложений. Текущие VPN-маршруты временно направлены напрямую."
				: "Нажмите для смены маршрута. Повторное нажатие вернёт правило по умолчанию.",
			IsEnabled = state.Zapret?.RoutingChangesApplied != true
		};
		toggle.Click += delegate
		{
			string next = route == ApplicationRoutingService.Automatic ? ApplicationRoutingService.Proxy
				: route == ApplicationRoutingService.Proxy ? ApplicationRoutingService.Direct : ApplicationRoutingService.Automatic;
			ApplicationRoutingService.SetRoute(state.Routing, entry.ExecutablePath, next);
			StateStore.Save(state);
			applicationRoutingPending = ApplicationRoutingNeedsApply();
			Dispatcher.BeginInvoke(refresh);
		};
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
		if (!applicationRoutingPending || core.Status != CoreStatus.Connected) return;
		await trafficModeGate.WaitAsync();
		bool stopped = false;
		VpnProfile profile = SelectedProfile();
		RoutingSettings previous = core.LastConnectedRouting;
		try
		{
			if (profile == null) throw new InvalidOperationException("Сначала выберите VPN-конфиг.");
			RoutingSettings updated = EffectiveRouting();
			string error = await core.ValidateAsync(profile, updated);
			if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
			manualDisconnect = true;
			connectionRequested = false;
			core.Stop();
			stopped = true;
			manualDisconnect = false;
			connectionRequested = true;
			await core.StartAsync(profile, updated);
			applicationRoutingPending = false;
			Toast("Маршруты приложений применены.", Green);
		}
		catch (Exception ex)
		{
			if (stopped && previous != null)
			{
				try { core.Stop(); manualDisconnect = false; connectionRequested = true; await core.StartAsync(profile, previous); }
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
		stackPanel.Children.Add(T(title, 12.5, TextColor, FontWeights.SemiBold));
		stackPanel.Children.Add(T(caption, 9.5, Muted));
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
			Height = new GridLength(76.0)
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
		stackPanel.Children.Add(T(caption, 9.0, Muted));
		Grid.SetColumn(stackPanel, 1);
		grid2.Children.Add(stackPanel);
		grid.Children.Add(grid2);
		Border border3 = new Border();
		border3.Background = new SolidColorBrush(C("#0F1219"));
		border3.BorderBrush = new SolidColorBrush(C("#242A37"));
		border3.BorderThickness = new Thickness(1.0);
		border3.CornerRadius = Radius(10.0);
		border3.Padding = new Thickness(10.0, 7.0, 8.0, 7.0);
		Border border4 = border3;
		TextBox editor = new TextBox
		{
			Text = string.Join(Environment.NewLine, values),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Foreground = new SolidColorBrush(C("#CBD1DC")),
			CaretBrush = new SolidColorBrush(AccentLight),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 10.5,
			AcceptsReturn = true,
			TextWrapping = TextWrapping.NoWrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(0.0)
		};
		bool zapretTransitionActive = state.Zapret?.RoutingChangesApplied == true;
		bool routingListLocked = zapretTransitionActive && (key == "proxy-domains" || key == "direct-domains"
			|| key == "proxy-apps" || key == "direct-apps" || key == "proxy-paths" || key == "direct-paths");
		if (routingListLocked)
		{
			editor.IsReadOnly = true;
			editor.ToolTip = "Выключите Zapret, чтобы редактировать списки маршрутов. Исходные VPN-маршруты временно сохранены и вернутся автоматически.";
		}
		editor.TextChanged += delegate
		{
			if (routingListLocked) return;
			values.Clear();
			values.AddRange(from x in editor.Text.Replace("\r", "").Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
				select x.Trim() into x
				where x.Length > 0
				select x);
			StateStore.Save(state);
		};
		border4.Child = editor;
		Grid.SetRow(border4, 2);
		grid.Children.Add(border4);
		border2.Child = grid;
		VisualDesign.ApplyGlassSurface(border2, state.Visual);
		EnableCardMotion(border2);
		return border2;
	}

	private UIElement BuildDiagnosticsPage()
	{
		Grid page = new Grid();
		page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18.0) });
		page.RowDefinitions.Add(new RowDefinition());
		Button run = PrimaryButton("Запустить проверку", 160.0);
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
				DiagnosticReport report = await DiagnosticService.RunAsync(core, SelectedProfile(), EffectiveRouting());
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
		Button explain = SmallButton("Объяснить");
		explain.Height = 38.0;
		explain.Click += delegate
		{
			RoutingDecision decision = RoutingInspector.Explain(EffectiveRouting(), destination.Text, process.Text);
			ShowRouteDecision(routeCard, decision, destination.Text, process.Text);
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
		Color color = decision.Route == "proxy" ? AccentLight : decision.Route == "direct" ? Teal : Danger;
		StackPanel flow = new StackPanel();
		Grid nodes = new Grid(); nodes.ColumnDefinitions.Add(new ColumnDefinition()); nodes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) }); nodes.ColumnDefinitions.Add(new ColumnDefinition()); nodes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) }); nodes.ColumnDefinitions.Add(new ColumnDefinition());
		nodes.Children.Add(RouteNode("globe", string.IsNullOrWhiteSpace(destination) ? "Назначение" : destination, Muted, 0)); TextBlock arrow1 = T("→", 18, Muted); arrow1.HorizontalAlignment = HorizontalAlignment.Center; arrow1.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow1, 1); nodes.Children.Add(arrow1);
		nodes.Children.Add(RouteNode("routing", string.IsNullOrWhiteSpace(process) ? "Правила" : process, AccentLight, 2)); TextBlock arrow2 = T("→", 18, color); arrow2.HorizontalAlignment = HorizontalAlignment.Center; arrow2.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow2, 3); nodes.Children.Add(arrow2);
		nodes.Children.Add(RouteNode(decision.Route == "block" ? "diagnostics" : decision.Route == "direct" ? "home" : "shield", decision.DisplayRoute.ToUpperInvariant(), color, 4)); flow.Children.Add(nodes); TextBlock reason = T(decision.Reason, 9.5, Muted); reason.TextWrapping = TextWrapping.Wrap; reason.Margin = new Thickness(0, 12, 0, 0); flow.Children.Add(reason); routeCard.Child = flow;
	}

	private UIElement RouteNode(string icon, string label, Color color, int column)
	{
		Border node = new Border { Padding = new Thickness(10), Background = new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)), BorderThickness = new Thickness(1), CornerRadius = Radius(10) };
		StackPanel content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center }; content.Children.Add(IconFactory.Create(icon, 16, color)); TextBlock copy = T(label, 9.5, color, FontWeights.SemiBold); copy.Margin = new Thickness(7, 0, 0, 0); content.Children.Add(copy); node.Child = content; Grid.SetColumn(node, column); return node;
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
			FontSize = 10.5,
			IsReadOnly = readOnly,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = true,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0)
		};
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
		Button appearance = PrimaryButton("✦  Персонализация", 154.0);
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
		grid.Children.Add(PageHeader("Настройки", "Поведение клиента и оформление", appearance, null));
		Grid grid2 = new Grid();
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(18.0)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(SectionTitle("ОФОРМЛЕНИЕ"));
		stackPanel.Children.Add(AppearanceSummaryCard());
		stackPanel.Children.Add(SectionTitle("ЗАПУСК", new Thickness(0.0, 20.0, 0.0, 10.0)));
		stackPanel.Children.Add(SettingCard("Автоподключение", "Подключаться при открытии NOVA", state.AutoConnect, delegate
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
		stackPanel.Children.Add(SettingCard("Kill Switch", "Строгая маршрутизация и восстановление туннеля при обрыве", state.KillSwitch, delegate
		{
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
		stackPanel.Children.Add(SettingCard("Показывать обучение при запуске", "Открывать вкладку обучения для новых пользователей", state.ShowTrainingOnStartup, delegate
		{
			state.ShowTrainingOnStartup = !state.ShowTrainingOnStartup;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Подсказки обучения", "Показывать пояснения рядом с важными функциями", state.ShowTrainingHints, delegate
		{
			state.ShowTrainingHints = !state.ShowTrainingHints;
			StateStore.Save(state);
			Navigate("settings");
		}));
		stackPanel.Children.Add(SettingCard("Запускать с Windows", "Открывать NOVA после входа в систему", state.StartWithWindows, delegate
		{
			state.StartWithWindows = !state.StartWithWindows;
			SetStartup(state.StartWithWindows);
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
		stackPanel2.Children.Add(SectionTitle("ДИАГНОСТИКА"));
		Border border = new Border();
		border.Padding = new Thickness(16.0);
		border.Height = 264.0;
		border.Background = new SolidColorBrush(Surface);
		border.BorderBrush = new SolidColorBrush(BorderColor);
		border.BorderThickness = new Thickness(1.0);
		border.CornerRadius = Radius(15.0);
		Border border2 = border;
		Grid grid3 = new Grid();
		grid3.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid3.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(10.0)
		});
		grid3.RowDefinitions.Add(new RowDefinition());
		Grid grid4 = new Grid();
		grid4.ColumnDefinitions.Add(new ColumnDefinition());
		grid4.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(84.0)
		});
		StackPanel stackPanel3 = new StackPanel();
		stackPanel3.Children.Add(T("Журнал подключения", 12.0, TextColor, FontWeights.SemiBold));
		stackPanel3.Children.Add(T("Полезен, если сервер не подключается", 9.5, Muted));
		grid4.Children.Add(stackPanel3);
		Button button = SmallButton("Копировать");
		button.Click += delegate
		{
			if (!string.IsNullOrWhiteSpace(core.Logs))
			{
				Clipboard.SetText(core.Logs);
			}
			Toast("Журнал скопирован.", Green);
		};
		Grid.SetColumn(button, 1);
		grid4.Children.Add(button);
		grid3.Children.Add(grid4);
		TextBox textBox = new TextBox();
		textBox.Text = (string.IsNullOrWhiteSpace(core.Logs) ? "Журнал пока пуст.\nПодключитесь к серверу — здесь появятся технические детали." : core.Logs);
		textBox.Background = new SolidColorBrush(C("#0E1117"));
		textBox.Foreground = new SolidColorBrush(C("#9CA6B7"));
		textBox.BorderBrush = new SolidColorBrush(C("#242A37"));
		textBox.BorderThickness = new Thickness(1.0);
		textBox.FontFamily = new FontFamily("Consolas");
		textBox.FontSize = 9.5;
		textBox.IsReadOnly = true;
		textBox.TextWrapping = TextWrapping.Wrap;
		textBox.AcceptsReturn = true;
		textBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		textBox.Padding = new Thickness(10.0);
		TextBox element = textBox;
		Grid.SetRow(element, 2);
		grid3.Children.Add(element);
		border2.Child = grid3;
		stackPanel2.Children.Add(border2);
		stackPanel2.Children.Add(SectionTitle("О ПРИЛОЖЕНИИ", new Thickness(0.0, 20.0, 0.0, 10.0)));
		Border border3 = new Border();
		border3.Padding = new Thickness(16.0);
		border3.Background = new SolidColorBrush(Surface);
		border3.BorderBrush = new SolidColorBrush(BorderColor);
		border3.BorderThickness = new Thickness(1.0);
		border3.CornerRadius = Radius(15.0);
		Border border4 = border3;
		StackPanel stackPanel4 = new StackPanel();
		stackPanel4.Children.Add(T("NOVA VPN  2.3.15", 12.0, TextColor, FontWeights.SemiBold));
		stackPanel4.Children.Add(T(core.IsCoreInstalled ? "VPN-ядро установлено · Windows TUN" : "VPN-ядро не найдено", 9.5, core.IsCoreInstalled ? Green : Danger));
		stackPanel4.Children.Add(T("Профили хранятся зашифрованно в вашей учётной записи Windows.", 9.5, Muted));
		stackPanel4.Children.Add(T("Горячая клавиша подключения: Ctrl + Alt + V", 9.5, Muted));
		border4.Child = stackPanel4;
		stackPanel2.Children.Add(border4);
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
		updateCopy.Children.Add(T("Версия 2.3.15", 11.0, TextColor, FontWeights.SemiBold));
		TextBlock updateHint = T("GitHub проверяется при запуске. Кнопка «Обновить» скачивает последний установщик. Конфиги и VPN-ключи не меняются.", 9.5, Muted);
		updateHint.TextWrapping = TextWrapping.Wrap;
		updateHint.Margin = new Thickness(0.0, 3.0, 12.0, 0.0);
		updateCopy.Children.Add(updateHint);
		updateLayout.Children.Add(updateCopy);
		Button appUpdateButton = PrimaryButton("Обновить", 112.0);
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
					CopyState(StateStore.ImportEncrypted(dialog.FileName));
					StateStore.Save(state);
					Toast("Резервная копия восстановлена.", Green);
					Navigate("settings");
				}
				catch (Exception ex)
				{
					Toast("Не удалось открыть копию: " + LogSanitizer.Sanitize(ex.Message), Danger);
				}
			}
		};
		backupButtons.Children.Add(export);
		backupButtons.Children.Add(import);
		Button portableExport = SmallButton("Экспорт для другого ПК");
		portableExport.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		portableExport.Click += delegate
		{
			if (MessageBox.Show(this, "Переносимый файл содержит VPN-ключи, пароли и правила маршрутизации. Передавайте его только доверенному человеку.", "NOVA VPN", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
			SaveFileDialog dialog = new SaveFileDialog { Filter = "NOVA Portable Config (*.nova-config)|*.nova-config", FileName = "NOVA-Config-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".nova-config" };
			if (dialog.ShowDialog(this) == true)
			{
				try { PortableConfigService.Export(state, dialog.FileName); Toast("Переносимый конфиг сохранён.", Green); }
				catch (Exception ex) { Toast("Не удалось сохранить конфиг: " + LogSanitizer.Sanitize(ex.Message), Danger); }
			}
		};
		Button portableImport = SmallButton("Импорт конфига");
		portableImport.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		portableImport.Click += async delegate
		{
			if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting) { Toast("Сначала отключите VPN.", Warning); return; }
			OpenFileDialog dialog = new OpenFileDialog { Filter = "Конфиги NOVA, VPN-ссылки и JSON (*.nova-config;*.json;*.txt;*.conf)|*.nova-config;*.json;*.txt;*.conf|Все файлы (*.*)|*.*" };
			if (dialog.ShowDialog(this) == true && MessageBox.Show(this, "Заменить текущие профили, маршруты и настройки данными из переносимого конфига?", "NOVA VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				try
				{
					string extension = System.IO.Path.GetExtension(dialog.FileName);
					if (string.Equals(extension, ".nova-config", StringComparison.OrdinalIgnoreCase))
					{
						CopyState(PortableConfigService.Import(dialog.FileName));
						StateStore.Save(state);
						Toast("Переносимый конфиг импортирован.", Green);
					}
					else
					{
						ImportResult importedFile = await LinkParser.ImportDetailedAsync(File.ReadAllText(dialog.FileName));
						int added = MergeImportedProfiles(importedFile);
						StateStore.Save(state);
						Toast("Импортировано серверов: " + added, Green);
					}
					Navigate("settings");
				}
				catch (Exception ex) { Toast("Не удалось импортировать конфиг: " + LogSanitizer.Sanitize(ex.Message), Danger); }
			}
		};
		backupButtons.Children.Add(portableExport);
		backupButtons.Children.Add(portableImport);
		Button settingsExport = SmallButton("Экспорт настроек без ключей");
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
		Button settingsImport = SmallButton("Импорт настроек без ключей");
		settingsImport.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		settingsImport.Click += delegate
		{
			if (core.Status == CoreStatus.Connected || core.Status == CoreStatus.Connecting) { Toast("Сначала отключите VPN.", Warning); return; }
			OpenFileDialog dialog = new OpenFileDialog { Filter = "NOVA Settings (*.nova-settings)|*.nova-settings" };
			if (dialog.ShowDialog(this) == true && MessageBox.Show(this, "Заменить маршрутизацию и оформление настройками из файла? VPN-профили не переносятся.", "NOVA VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
			{
				try
				{
					CopyState(PortableConfigService.ImportSettingsOnly(dialog.FileName));
					StateStore.Save(state);
					Toast("Настройки импортированы без VPN-профилей.", Green);
					Navigate("settings");
				}
				catch (Exception ex) { Toast("Не удалось импортировать настройки: " + LogSanitizer.Sanitize(ex.Message), Danger); }
			}
		};
		backupButtons.Children.Add(settingsExport);
		backupButtons.Children.Add(settingsImport);
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
		ImportWindow dialog = new ImportWindow(state.Visual)
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			int num = default(int);
			_ = num;
			_ = 0;
			try
			{
				Mouse.OverrideCursor = Cursors.Wait;
				ImportResult importResult = await LinkParser.ImportDetailedAsync(dialog.ImportText);
				List<VpnProfile> imported = importResult.Profiles;
				if (importResult.IsSubscription)
				{
					SubscriptionManager.MergeImported(state, importResult);
				}
				else foreach (VpnProfile profile in imported)
				{
					List<VpnProfile> profiles = state.Profiles;
					Func<VpnProfile, bool> predicate = (VpnProfile x) => x.Source == profile.Source && !string.IsNullOrWhiteSpace(x.Source);
					if (profiles.FirstOrDefault(predicate) == null)
					{
						state.Profiles.Add(profile);
					}
				}
				if (string.IsNullOrWhiteSpace(state.SelectedProfileId) && state.Profiles.Count > 0)
				{
					state.SelectedProfileId = state.Profiles[0].Id;
				}
				StateStore.Save(state);
				Navigate("servers");
				Toast("Добавлено серверов: " + imported.Count, Green);
			}
			catch (Exception ex)
			{
				Toast(ex.Message, Danger);
			}
		}
		finally
		{
			Mouse.OverrideCursor = null;
		}
	}

	private async Task ToggleConnectionAsync()
	{
		if (profileSwitchInProgress)
		{
			Toast("Дождитесь завершения переключения VPN-конфига.", Warning);
			return;
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
		VpnProfile profile = SelectedProfile();
		if (profile == null)
		{
			await ShowImportDialogAsync();
			return;
		}
		try
		{
			manualDisconnect = false;
			connectionRequested = true;
			await core.StartAsync(profile, EffectiveRouting());
			currentSessionStartedUtc = DateTime.UtcNow;
			RecordHistory("Подключено", "Туннель sing-box запущен.");
			profile.ConsecutiveFailures = 0;
			profile.HealthStatus = "Подключён";
			profile.LastTestMessage = "Туннель sing-box запущен.";
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
		if (closing || core.Status == CoreStatus.Connecting || core.Status == CoreStatus.Connected)
		{
			return;
		}
		bool hasSubscriptions = state.Profiles.Any(p => p != null && !string.IsNullOrWhiteSpace(p.SubscriptionUrl));
		bool updateDue = state.LastSubscriptionUpdateUtc == default(DateTime) || DateTime.UtcNow - state.LastSubscriptionUpdateUtc >= TimeSpan.FromHours(state.SubscriptionUpdateIntervalHours);
		if (!hasSubscriptions || !updateDue)
		{
			return;
		}
		try
		{
			SubscriptionUpdateResult result = await SubscriptionManager.UpdateAllAsync(state);
			if (notify)
			{
				Toast(result.Success ? "Подписки обновлены автоматически." : string.Join(" ", result.Errors.Take(2)), result.Success ? Green : Warning);
			}
		}
		catch (Exception ex)
		{
			if (notify) Toast("Не удалось обновить подписки: " + ex.Message, Warning);
		}
	}

	private void SyncSubscriptionTimer()
	{
		if (snapshotMode || closing || subscriptionTimer == null) return;
		bool hasSubscriptions = state.Profiles.Any(p => p != null && !String.IsNullOrWhiteSpace(p.SubscriptionUrl));
		bool connectionBusy = core.Status == CoreStatus.Connecting || core.Status == CoreStatus.Connected;
		if (!hasSubscriptions || connectionBusy)
		{
			subscriptionTimer.Stop();
			return;
		}

		int hours = Math.Max(1, Math.Min(168, state.SubscriptionUpdateIntervalHours));
		DateTime dueUtc = state.LastSubscriptionUpdateUtc == default(DateTime)
			? DateTime.UtcNow
			: state.LastSubscriptionUpdateUtc.AddHours(hours);
		TimeSpan untilDue = dueUtc - DateTime.UtcNow;
		if (untilDue < TimeSpan.FromMinutes(1)) untilDue = TimeSpan.FromMinutes(1);
		if (untilDue > TimeSpan.FromDays(1)) untilDue = TimeSpan.FromDays(1);
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
			if (!closing && !manualDisconnect && connectionRequested && state.AutoReconnect && reconnectAttempt < 5)
			{
				VpnProfile failed = SelectedProfile();
				if (failed != null)
				{
					failed.ConsecutiveFailures++;
					failed.HealthStatus = "Сбой";
					failed.LastTestMessage = core.LastError;
				}
				RecordHistory("Обрыв", core.LastError);
				if (state.AutoSelectBestServer)
				{
					VpnProfile alternative = HealthCheckService.BestAvailable(state.Profiles.Where(p => p != failed));
					if (alternative != null)
					{
						state.SelectedProfileId = alternative.Id;
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
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (closing || !connectionRequested || !state.AutoReconnect)
			{
				return;
			}
			if (!e.IsAvailable)
			{
				RecordHistory("Обрыв", "Сеть устройства стала недоступна.");
				core.Stop();
				Toast("Сеть недоступна. Подключение будет восстановлено автоматически.", Warning);
			}
			else
			{
				reconnectTimer.Interval = TimeSpan.FromSeconds(1.5);
				reconnectTimer.Stop();
				reconnectTimer.Start();
			}
		});
	}

	private void CoreStatusChanged(CoreStatus newStatus, string message)
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			SyncSubscriptionTimer();
			if (newStatus == CoreStatus.Connected) applicationRoutingPending = ApplicationRoutingNeedsApply();
			if (currentPage == "routing") applicationListRefresh?.Invoke();
			if (statusText != null)
			{
				statusText.Text = ((newStatus == CoreStatus.Connected) ? "Защищено" : ((newStatus == CoreStatus.Connecting) ? "Подключение…" : ((newStatus == CoreStatus.Error) ? "Ошибка" : "Не подключено")));
				statusText.Foreground = new SolidColorBrush((newStatus == CoreStatus.Connected) ? Green : ((newStatus == CoreStatus.Error) ? Danger : Muted));
			}
			if (statusDot != null)
			{
				statusDot.Background = new SolidColorBrush((newStatus == CoreStatus.Connected) ? Green : ((newStatus == CoreStatus.Error) ? Danger : ((newStatus == CoreStatus.Connecting) ? Warning : Muted)));
			}
			if (currentPage == "home")
			{
				UpdateHomeConnectionStatus();
				if (newStatus == CoreStatus.Error) AnimateConnectionError();
			}
			if (trayConnectItem != null)
			{
				trayConnectItem.Text = (newStatus == CoreStatus.Connected || newStatus == CoreStatus.Connecting) ? "Отключить" : "Подключить";
			}
			if (newStatus == CoreStatus.Connected)
			{
				ShowNotification("VPN подключён: " + (SelectedProfile()?.Name ?? "сервер"));
			}
			else if (newStatus == CoreStatus.Error)
			{
				ShowNotification("Ошибка VPN: " + message);
			}
		});
		_ = RefreshExternalIpForStatusAsync(newStatus);
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
			trayConnectItem = new Forms.ToolStripMenuItem("Подключить");
			trayConnectItem.Click += async delegate { await base.Dispatcher.InvokeAsync(async () => await ToggleConnectionAsync()); };
			Forms.ToolStripMenuItem pause = new Forms.ToolStripMenuItem("Пауза на 15 минут");
			pause.Click += delegate
			{
				base.Dispatcher.BeginInvoke((Action)delegate
				{
					manualDisconnect = true;
					connectionRequested = false;
					core.Stop();
					try
					{
						StopZapretForPause();
					}
					catch (Exception ex)
					{
						Toast("VPN приостановлен, но Zapret не удалось полностью остановить: " + LogSanitizer.Sanitize(ex.Message), Danger);
					}
					state.PauseUntilUtc = DateTime.UtcNow.AddMinutes(15.0);
					RecordHistory("Пауза", "VPN приостановлен на 15 минут из меню уведомлений.");
					StateStore.Save(state);
				});
			};
			Forms.ToolStripMenuItem exit = new Forms.ToolStripMenuItem("Выход");
			exit.Click += delegate { base.Dispatcher.BeginInvoke((Action)Close); };
			menu.Items.Add(open);
			menu.Items.Add(trayConnectItem);
			menu.Items.Add(pause);
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

	private async Task RefreshExternalIpForStatusAsync(CoreStatus status)
	{
		if (snapshotMode || (status != CoreStatus.Connected && status != CoreStatus.Disconnected)) return;
		string value = await ExternalIpService.GetAsync();
		if (status == CoreStatus.Connected) tunnelExternalIp = value; else directExternalIp = value;
		await base.Dispatcher.InvokeAsync(delegate { if (currentPage == "home" && !closing && liveExternalIpText != null) liveExternalIpText.Text = core.Status == CoreStatus.Connected ? tunnelExternalIp : directExternalIp; });
	}

	private void ShowNotification(string message)
	{
		if (!state.ShowNotifications || trayIcon == null || string.IsNullOrWhiteSpace(message))
		{
			return;
		}
		try
		{
			trayIcon.BalloonTipTitle = "NOVA VPN";
			trayIcon.BalloonTipText = message.Length > 180 ? message.Substring(0, 180) : message;
			trayIcon.ShowBalloonTip(2500);
		}
		catch
		{
		}
	}

	private void RefreshCurrentPage()
	{
		string page = currentPage;
		Navigate(page, false);
	}

	private void UpdateVerificationDisplay()
	{
		if (protectionIndicators == null) return;
		bool tunnelOk = core.Status == CoreStatus.Connected;
		if (connectHeadline != null) connectHeadline.Text = StatusHeadline();
		if (connectCaption != null) connectCaption.Text = StatusCaption();
		protectionIndicators.Children.Clear();
		protectionIndicators.Children.Add(StatusPill("TUN", tunnelOk ? "активен" : core.Status == CoreStatus.Connecting ? "запускается" : "выключен", tunnelOk ? Green : core.Status == CoreStatus.Connecting ? Warning : Muted));
		protectionIndicators.Children.Add(StatusPill("DNS", core.Verification.DnsOk ? "проверен" : tunnelOk ? "проверяется" : "не проверен", core.Verification.DnsOk ? Green : tunnelOk ? Warning : Muted));
		protectionIndicators.Children.Add(StatusPill("ИНТЕРНЕТ", core.Verification.InternetOk ? core.Verification.InternetLatencyMs + " мс" : tunnelOk ? "проверяется" : "не проверен", core.Verification.InternetOk ? Green : tunnelOk ? Warning : Muted));
	}

	private void UpdateHomeConnectionStatus()
	{
		if (trafficModeStatusText != null) trafficModeStatusText.Text = ZapretStatusCaption();
		connectionControl?.Apply(state.Visual, core.Status, MotionAllowed);
		UpdateVerificationDisplay();
		if (homePauseButton != null)
		{
			homePauseButton.Content = core.Status == CoreStatus.Connected ? "Пауза 15 минут" : "Возобновить";
			homePauseButton.Visibility = core.Status == CoreStatus.Connected || state.PauseUntilUtc > DateTime.UtcNow ? Visibility.Visible : Visibility.Collapsed;
		}
		if (homeBestButton != null) homeBestButton.IsEnabled = state.Profiles.Count > 0 && core.Status != CoreStatus.Connecting && !profileSwitchInProgress;
		if (liveSessionText != null) liveSessionText.Text = SessionLabel();
		if (liveExternalIpText != null) liveExternalIpText.Text = core.Status == CoreStatus.Connected ? tunnelExternalIp : directExternalIp;
		SyncVisualTimerState();
	}

	private VpnProfile SelectedProfile()
	{
		return state.Profiles.FirstOrDefault((VpnProfile p) => p.Id == state.SelectedProfileId) ?? state.Profiles.FirstOrDefault();
	}

	private void CopyState(AppState source)
	{
		if (source == null)
		{
			throw new InvalidOperationException("Резервная копия пуста.");
		}
		if (state.Zapret?.RoutingChangesApplied == true)
		{
			throw new InvalidOperationException("Сначала выключите Zapret: исходные VPN-маршруты временно сохранены для автоматического возврата.");
		}
		state.Profiles = source.Profiles ?? new List<VpnProfile>();
		state.SelectedProfileId = source.SelectedProfileId;
		state.Routing = source.Routing ?? new RoutingSettings();
		state.AutoConnect = source.AutoConnect;
		state.StartWithWindows = source.StartWithWindows;
		state.MinimizeToTray = source.MinimizeToTray;
		state.Theme = source.Theme;
		state.AutoReconnect = source.AutoReconnect;
		state.AutoSelectBestServer = source.AutoSelectBestServer;
		state.KillSwitch = source.KillSwitch;
		state.ShowAnimations = source.ShowAnimations;
		state.ShowNotifications = source.ShowNotifications;
		state.SuppressUpdatePrompts = source.SuppressUpdatePrompts;
		state.LastSeenReleaseNotesVersion = source.LastSeenReleaseNotesVersion;
		state.ShowTrainingOnStartup = source.ShowTrainingOnStartup;
		state.ShowTrainingHints = source.ShowTrainingHints;
		state.SubscriptionUpdateIntervalHours = source.SubscriptionUpdateIntervalHours;
		state.LastSubscriptionUpdateUtc = source.LastSubscriptionUpdateUtc;
		state.PauseUntilUtc = source.PauseUntilUtc;
		state.ConnectionHistory = source.ConnectionHistory ?? new List<ConnectionHistoryItem>();
		state.Visual = source.Visual ?? new VisualPreferences();
		state.Zapret = source.Zapret ?? new ZapretSettings();
		state.SchemaVersion = source.SchemaVersion;
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
		if (core.Status == CoreStatus.Connected)
		{
			return "Вы защищены";
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
		if (core.Status == CoreStatus.Connected)
		{
			return "Трафик Windows проходит через выбранный защищённый маршрут.";
		}
		if (core.Status == CoreStatus.Connecting)
		{
			return "Создаём защищённый туннель и применяем маршруты.";
		}
		if (core.Status == CoreStatus.Error)
		{
			return "Откройте журнал в настройках или проверьте ключ.";
		}
		if (SelectedProfile() != null)
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
		stackPanel.Children.Add(T(title, palette.Name == ThemePalette.OneUi ? 38 : palette.IsGlass ? 32 : 25, TextColor, palette.Name == ThemePalette.OneUi ? FontWeights.Bold : FontWeights.SemiBold));
		stackPanel.Margin = new Thickness(0, palette.Name == ThemePalette.OneUi ? 18 : 0, 12, palette.Name == ThemePalette.OneUi ? 18 : 0);
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
			Margin = new Thickness(0.0, 0.0, 0.0, 5.0),
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
		button.Height = Math.Max(48.0, Math.Max(48.0 * VisualDesign.Density(state.Visual), 32.0 * state.Visual.TextScale));
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
		button.Height = Math.Max(48.0, Math.Max(48.0 * VisualDesign.Density(state.Visual), 32.0 * state.Visual.TextScale));
		button.MinWidth = 76.0;
		button.Padding = new Thickness(13.0, 0.0, 13.0, 0.0);
		button.Background = new SolidColorBrush(Surface2);
		button.Foreground = new SolidColorBrush(C("#BBC2CF"));
		button.BorderBrush = new SolidColorBrush(BorderColor);
		button.BorderThickness = new Thickness(1.0);
		button.FontSize = 11.5 * state.Visual.TextScale;
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
		button.Template = ButtonTemplate(14.0);
		button.HorizontalAlignment = HorizontalAlignment.Right;
		button.VerticalAlignment = VerticalAlignment.Center;
		button.ToolTip = on ? "Включено" : "Выключено";
		Grid toggle = new Grid();
		Border track = new Border { Width = 50, Height = 27, CornerRadius = new CornerRadius(13.5), Background = new SolidColorBrush(on ? Accent : palette.Name == ThemePalette.OneUi ? palette.SoftSelection : Surface2), BorderBrush = new SolidColorBrush(on ? AccentLight : BorderColor), BorderThickness = new Thickness(1) };
		toggle.Children.Add(track);
		Ellipse thumb = new Ellipse { Width = 19, Height = 19, Fill = new SolidColorBrush(on ? palette.AccentForeground : Muted), HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
		toggle.Children.Add(thumb); button.Content = toggle;
		EnableButtonMotion(button);
		return button;
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
			if (state.Visual.GlowIntensity > 0.1) card.Effect = new DropShadowEffect { Color = Accent, BlurRadius = 12 + 12 * state.Visual.GlowIntensity, ShadowDepth = 2, Opacity = 0.08 + 0.12 * state.Visual.GlowIntensity, RenderingBias = RenderingBias.Performance };
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
		textBlock.FontSize = Math.Max(10.5, size) * (state?.Visual?.TextScale ?? 1.0);
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
		double profileScale = palette.Name == ThemePalette.WindowsLight || palette.Name == ThemePalette.WindowsDark ? .35 : palette.Name == ThemePalette.OneUi ? .8 : 1.0;
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

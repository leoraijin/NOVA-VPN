using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NovaVpn;

public sealed class AppearanceWindow : Window
{
	private VisualPreferences working;
	private readonly bool animationsEnabled;
	private StackPanel body;
	private ThemePalette palette;
	private ScrollViewer mainScroll;
	private Border previewCard;
	private Border previewRoute;
	private TextBlock previewTheme;
	private TextBlock previewTitle;
	private TextBlock previewSubtitle;
	private TextBlock previewRouteText;
	private bool previewQueued;
	private bool previewDirty;
	private readonly DispatcherTimer previewTimer;
	private ConnectionControl previewConnection;
	private CoreStatus previewStatus = CoreStatus.Connected;
	private Grid previewLayout;
	private Border previewSurface;
	private Border previewBackdropHost;
	private Grid previewBackdrop;
	private string previewBackdropKey;
	private StackPanel previewDescription;
	private StackPanel previewServer;
	private TextBlock previewServerText;
	private WrapPanel previewMetrics;
	private Polyline previewGraph;
	private Viewbox previewConnectionHost;

	public VisualPreferences Result => working;

	public AppearanceWindow(VisualPreferences preferences) : this(preferences, true) { }

	public AppearanceWindow(VisualPreferences preferences, bool allowAnimations)
	{
		animationsEnabled = allowAnimations;
		working = (preferences ?? new VisualPreferences()).Clone();
		Title = "Персонализация NOVA";
		Rect workArea = SystemParameters.WorkArea;
		Width = Math.Min(820.0, workArea.Width);
		Height = Math.Min(860.0, workArea.Height);
		MinWidth = Math.Min(540.0, workArea.Width);
		MinHeight = Math.Min(460.0, workArea.Height);
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ResizeMode = ResizeMode.CanResize;
		FontFamily = new FontFamily("Segoe UI");
		previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
		previewTimer.Tick += delegate { previewTimer.Stop(); FlushPreview(); };
		Closed += delegate { previewTimer.Stop(); previewQueued = false; previewDirty = false; };
		Build();
	}

	private void Build()
	{
		palette = ThemePalette.Create(working);
		FontFamily = VisualDesign.InterfaceFont(working);
		Background = new SolidColorBrush(palette.Bg);
		Foreground = new SolidColorBrush(palette.Text);
		Grid root = new Grid { Margin = new Thickness(Width < 680.0 ? 16.0 : 24.0) };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
		root.RowDefinitions.Add(new RowDefinition());
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		StackPanel header = new StackPanel();
		header.Children.Add(Text("Персонализация NOVA", 25, palette.Text, FontWeights.SemiBold));
		header.Children.Add(Text("Выберите стиль и настройте его под себя", 11, palette.Muted));
		root.Children.Add(header);
		body = new StackPanel();
		mainScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
		ScrollViewer.SetPanningMode(mainScroll, PanningMode.VerticalOnly);
		Grid.SetRow(mainScroll, 2);
		root.Children.Add(mainScroll);
		Grid buttons = new Grid();
		buttons.ColumnDefinitions.Add(new ColumnDefinition());
		buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Button reset = ActionButton("Сбросить всё", false);
		reset.Click += delegate { FlushPreview(); working = new VisualPreferences(); RefreshControls(); };
		buttons.Children.Add(reset);
		Button cancel = ActionButton("Отмена", false); cancel.Margin = new Thickness(8, 0, 0, 0); cancel.Click += delegate { DialogResult = false; };
		Grid.SetColumn(cancel, 2); buttons.Children.Add(cancel);
		Button save = ActionButton("Сохранить", true); save.Margin = new Thickness(8, 0, 0, 0); save.Click += delegate { FlushPreview(); working.StoreActiveThemeSettings(); DialogResult = true; };
		Grid.SetColumn(save, 3); buttons.Children.Add(save);
		Grid.SetRow(buttons, 4); root.Children.Add(buttons);
		Content = root;
		BuildControls();
	}

	public void ScrollForSnapshot(double offset)
	{
		UpdateLayout(); mainScroll?.ScrollToVerticalOffset(Math.Max(0, offset)); UpdateLayout();
	}

	private void BuildControls()
	{
		body.Children.Clear();
		body.Children.Add(Section("ПРОФИЛЬ ОФОРМЛЕНИЯ"));
		body.Children.Add(ThemeSelectionRow());
		body.Children.Add(Section("ПРЕДПРОСМОТР", 12));
		body.Children.Add(PreviewCard());

		body.Children.Add(Section("РАЗМЕР И ФОРМА", 14));
		body.Children.Add(ChoiceRow("Плотность интерфейса", new[] { "Compact", "Standard", "Comfortable" }, working.Density, value => working.Density = value));
		body.Children.Add(ChoiceRow("Отображение серверов", new[] { "Compact", "Detailed", "Grid" }, working.ServerViewMode, value => working.ServerViewMode = value));
		body.Children.Add(SliderRow("Размер текста", "Масштаб подписей и заголовков", 85, 135, working.TextScale * 100, value => working.TextScale = value / 100.0, "0%"));
		body.Children.Add(SliderRow("Скругление", "Форма карточек и кнопок", 8, 32, working.CornerRadius, value => working.CornerRadius = value, "0"));
		body.Children.Add(SliderRow("Непрозрачность поверхностей", "Меньше — сквозь карточки сильнее виден фон. Контрастный режим делает их непрозрачными.", 78, 100, working.SurfaceOpacity * 100, value => working.SurfaceOpacity = value / 100.0, "0%"));
		if (palette.IsGlass)
		{
			body.Children.Add(SliderRow("Прозрачность стекла", "Прозрачность заливки: текст и значки остаются чёткими", 0, 100, (1.0 - working.GlassFrost) * 100, value => working.GlassFrost = 1.0 - value / 100.0, "0%"));
			body.Children.Add(SliderRow("Матовость стекла", "Размытие подложки независимо от прозрачности", 0, 18, working.GlassBlur, value => working.GlassBlur = value, "0"));
			body.Children.Add(SliderRow("Сила теней", "Мягкая тень от верхнего левого источника света", 0, 100, working.GlassShadow * 100, value => working.GlassShadow = value / 100.0, "0%"));
			body.Children.Add(SliderRow("Сила бликов", "Светлый край и отражение на стекле", 0, 100, working.GlassHighlight * 100, value => working.GlassHighlight = value / 100.0, "0%"));
		}

		body.Children.Add(Section("ДВИЖЕНИЕ И ЭФФЕКТЫ", 14));
		if (!animationsEnabled) body.Children.Add(Text("Анимации выключены в общих настройках. Предпросмотр тоже остаётся неподвижным.", 11, palette.Muted));
		body.Children.Add(ChoiceRow("Фон", new[] { "Solid", "Gradient", "Glass" }, working.BackgroundMode, value => working.BackgroundMode = value));
		body.Children.Add(SliderRow("Мягкий акцент", "Интенсивность свечения и цветового блика", 0, 60, working.GlowIntensity * 100, value => working.GlowIntensity = value / 100.0, "0%"));
		body.Children.Add(SliderRow("Скорость анимаций", "От спокойных переходов до быстрых откликов", 50, 200, working.AnimationSpeed * 100, value => working.AnimationSpeed = value / 100.0, "0%"));
		body.Children.Add(ChoiceRow("Частота анимаций", new[] { "Auto", "60", "120" }, FrameRateChoice(), value => working.AnimationFrameRate = value == "Auto" ? 0 : int.Parse(value)));
		body.Children.Add(CheckRow("Уменьшить движение", "Остановить декоративные эффекты и сократить переходы", working.ReduceMotion, value => working.ReduceMotion = value));
		body.Children.Add(CheckRow("Следовать Windows", "Учитывать системное отключение анимаций", working.FollowSystemMotion, value => working.FollowSystemMotion = value));
		body.Children.Add(CheckRow("Высокая контрастность", "Усилить границы и читаемость", working.HighContrast, value => working.HighContrast = value));
		body.Children.Add(SliderRow("Мягкое усиление контраста", "Плавно усилить текст и границы без жёстких обводок", 0, 100, working.SoftContrast * 100, value => working.SoftContrast = value / 100.0, "0%"));

		body.Children.Add(Section("ГЛАВНАЯ СТРАНИЦА", 14));
		body.Children.Add(CheckRow("Живой график скорости", "Входящий и исходящий трафик", working.ShowLiveGraph, value => working.ShowLiveGraph = value));
		body.Children.Add(CheckRow("Схема соединения", "Устройство → NOVA → сервер → интернет", working.ShowConnectionMap, value => working.ShowConnectionMap = value));
		foreach (string key in new[] { "Speed", "Protocol", "Latency", "Session", "Privacy" })
		{
			string captured = key;
			bool visible = !working.HiddenHomeCards.Contains(key);
			body.Children.Add(CheckRow("Карточка: " + CardName(key), "Показывать в нижней панели", visible, value => { if (value) working.HiddenHomeCards.Remove(captured); else if (!working.HiddenHomeCards.Contains(captured)) working.HiddenHomeCards.Add(captured); }));
		}
		body.Children.Add(HomeCardOrderEditor());
		UpdatePreviewCard();
	}

	private UIElement ThemeSelectionRow()
	{
		StackPanel wrapper = new StackPanel();
		WrapPanel panel = new WrapPanel();
		foreach (string themeName in ThemePalette.ThemeNames)
		{
			string selectedTheme = themeName;
			bool selected = string.Equals(working.ThemeName, themeName, StringComparison.OrdinalIgnoreCase);
			ThemePalette sample = ThemePalette.Create(new VisualPreferences { ThemeName = themeName });
			Border sampleSurface = new Border { Height = 28, Background = new LinearGradientBrush(sample.Surface2, sample.Surface, 35), BorderBrush = new SolidColorBrush(sample.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 7) };
			Grid sampleGrid = new Grid(); sampleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) }); sampleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.3, GridUnitType.Star) });
			Border sampleNav = new Border { Background = new SolidColorBrush(sample.Sidebar), CornerRadius = new CornerRadius(6, 0, 0, 6) }; sampleGrid.Children.Add(sampleNav);
			Border sampleAction = new Border { Width = 24, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(sample.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(sampleAction, 1); sampleGrid.Children.Add(sampleAction); sampleSurface.Child = sampleGrid;
			StackPanel content = new StackPanel(); content.Children.Add(sampleSurface); content.Children.Add(Text(ThemeLabel(themeName), 10.5, palette.IsGlass ? palette.Text : selected ? (sample.IsLight ? sample.Accent : sample.Text) : sample.Text, FontWeights.SemiBold));
			Button card = new Button { Content = content, Width = 166, Height = 69, Padding = new Thickness(9), Margin = new Thickness(0, 0, 8, 8), Background = new SolidColorBrush(selected ? sample.SoftSelection : sample.Surface), Foreground = new SolidColorBrush(sample.Text), BorderBrush = new SolidColorBrush(selected ? sample.Accent : sample.Border), BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand, ToolTip = ThemeLabel(themeName), Tag = themeName };
			card.Click += delegate { if (working.ThemeName != selectedTheme) SwitchTheme(selectedTheme); };
			EnableAppearanceButtonMotion(card);
			panel.Children.Add(card);
		}
		wrapper.Children.Add(panel);
		Button resetProfile = ActionButton("Сбросить выбранный профиль", false);
		resetProfile.HorizontalAlignment = HorizontalAlignment.Left;
		resetProfile.Click += delegate
		{
			double offset = mainScroll.VerticalOffset;
			if (working.ThemeOverrides != null) working.ThemeOverrides.Remove(ThemePalette.NormalizeName(working.ThemeName));
			VisualThemeOverrides.DefaultsFor(working.ThemeName).ApplyTo(working);
			RefreshControls(offset);
		};
		wrapper.Children.Add(resetProfile);
		return wrapper;
	}

	private UIElement PreviewCard()
	{
		previewCard = new Border { Margin = new Thickness(0, 0, 0, 12), ClipToBounds = true };
		Grid layers = new Grid();
		previewBackdropHost = new Border { Height = 320 };
		previewBackdrop = VisualDesign.Backdrop(working);
		previewBackdropKey = BackdropKey(working);
		previewBackdropHost.Child = previewBackdrop;
		layers.Children.Add(previewBackdropHost);
		previewSurface = new Border { Margin = new Thickness(12), BorderThickness = new Thickness(1) }; layers.Children.Add(previewSurface);
		previewLayout = new Grid();
		previewLayout.ColumnDefinitions.Add(new ColumnDefinition());
		previewLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
		for (int i = 0; i < 4; i++) previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		previewDescription = new StackPanel();
		previewTheme = Text(ThemeLabel(working.ThemeName), 9, palette.Muted, FontWeights.SemiBold);
		previewTitle = Text("Ваше пространство", 20, palette.Text, FontWeights.SemiBold);
		previewSubtitle = Text("Демонстрация оформления", 10, palette.Muted);
		previewDescription.Children.Add(previewTheme); previewDescription.Children.Add(previewTitle); previewDescription.Children.Add(previewSubtitle);
		previewLayout.Children.Add(previewDescription);
		previewConnection = new ConnectionControl();
		previewConnection.ActionButton.Click += delegate
		{
			previewStatus = previewStatus == CoreStatus.Connected ? CoreStatus.Connecting : previewStatus == CoreStatus.Connecting ? CoreStatus.Disconnected : CoreStatus.Connected;
			previewConnection.Apply(working, previewStatus, CanAnimate());
		};
		previewConnectionHost = new Viewbox { Width = 180, Height = 180, Child = previewConnection };
		Grid.SetColumn(previewConnectionHost, 1); Grid.SetRowSpan(previewConnectionHost, 3); previewLayout.Children.Add(previewConnectionHost);
		previewServer = new StackPanel { Margin = new Thickness(0, 12, 8, 4) };
		previewServerText = Text("Frankfurt · Германия\nVLESS · 24 мс", 12, palette.Text, FontWeights.SemiBold);
		previewServer.Children.Add(previewServerText);
		Grid.SetRow(previewServer, 1); previewLayout.Children.Add(previewServer);
		previewRoute = new Border { Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
		previewRouteText = Text("ПК  ›  NOVA  ›  Сервер", 10, palette.Text); previewRoute.Child = previewRouteText;
		Grid.SetRow(previewRoute, 2); previewLayout.Children.Add(previewRoute);
		StackPanel footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
		previewGraph = new Polyline { Height = 28, Stretch = Stretch.Fill, StrokeThickness = 1.8, Points = new PointCollection { new Point(0,20),new Point(25,20),new Point(40,9),new Point(60,17),new Point(90,12),new Point(115,15),new Point(140,1),new Point(170,15),new Point(220,10) } };
		footer.Children.Add(previewGraph);
		previewMetrics = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
		foreach (string key in new[] { "Speed", "Protocol", "Latency", "Session", "Privacy" })
			previewMetrics.Children.Add(new Border { Tag = key, Child = Text(CardName(key), 9, palette.Text), BorderThickness = new Thickness(1), Margin = new Thickness(0,0,6,6) });
		footer.Children.Add(previewMetrics);
		footer.Children.Add(Text("Нажмите кнопку, чтобы посмотреть состояния и движение", 9, palette.Muted));
		Grid.SetRow(footer, 3); Grid.SetColumnSpan(footer, 2); previewLayout.Children.Add(footer);
		previewSurface.Child = previewLayout; previewCard.Child = layers;
		previewCard.SizeChanged += delegate { previewCard.Clip = new RectangleGeometry(new Rect(0, 0, previewCard.ActualWidth, previewCard.ActualHeight), working.CornerRadius, working.CornerRadius); };
		return previewCard;
	}

	private void UpdatePreviewCard()
	{
		palette = ThemePalette.Create(working);
		Resources["NovaAccentBrush"] = new SolidColorBrush(palette.Accent);
		Resources["NovaGlassFill"] = VisualDesign.GlassFill(working);
		Resources["NovaGlassSheen"] = VisualDesign.GlassSheen(working);
		Resources["NovaGlassRim"] = VisualDesign.GlassRim(working);
		Resources["NovaGlassPreferences"] = working.Clone();
		Color shadowTint = VisualDesign.KeyShadowTint(palette);
		Resources["NovaGlassShadow"] = VisualDesign.KeyShadow(false, working.GlassShadow, shadowTint);
		Resources["NovaGlassPressedShadow"] = VisualDesign.KeyShadow(true, working.GlassShadow, shadowTint);
		Resources["NovaSurfaceBrush"] = new SolidColorBrush(palette.Surface2);
		Resources["NovaBorderBrush"] = new SolidColorBrush(palette.Border);
		Resources["NovaTextBrush"] = new SolidColorBrush(palette.Text);
		if (previewCard == null) return;
		string backdropKey = BackdropKey(working);
		if (previewBackdrop != null && !string.Equals(previewBackdropKey, backdropKey, StringComparison.Ordinal))
		{
			VisualDesign.UpdateBackdrop(previewBackdrop, working);
			previewBackdropKey = backdropKey;
		}
		previewSurface.Background = VisualDesign.Surface(working, true);
		previewSurface.BorderBrush = new SolidColorBrush(palette.Border);
		previewSurface.CornerRadius = new CornerRadius(working.CornerRadius);
		previewSurface.Padding = new Thickness(16 * VisualDesign.Density(working));
		previewTheme.Text = ThemeLabel(working.ThemeName); previewTheme.Foreground = new SolidColorBrush(palette.Muted);
		previewTitle.Foreground = new SolidColorBrush(palette.Text);
		previewTitle.FontSize = (palette.Name == ThemePalette.OneUi ? 25 : palette.IsGlass ? 22 : 18) * working.TextScale;
		previewSubtitle.Foreground = new SolidColorBrush(palette.Muted);
		previewSubtitle.FontSize = 10 * working.TextScale;
		previewServerText.FontSize = 12 * working.TextScale; previewServerText.Foreground = new SolidColorBrush(palette.Text);
		previewServerText.Text = working.ServerViewMode == "Compact" ? "DE · Frankfurt · 24 мс" : working.ServerViewMode == "Grid" ? "DE · Frankfurt     NL · Amsterdam\n24 мс                     38 мс" : "Frankfurt · Германия\nVLESS · 24 мс";
		previewServer.Margin = new Thickness(0, 12 * VisualDesign.Density(working), 8, 8);
		previewRoute.Visibility = working.ShowConnectionMap ? Visibility.Visible : Visibility.Collapsed;
		previewRoute.Background = new SolidColorBrush(palette.Surface2);
		previewRoute.CornerRadius = new CornerRadius(working.CornerRadius / 2);
		previewRouteText.Foreground = new SolidColorBrush(palette.Text); previewRouteText.FontSize = 10 * working.TextScale;
		previewGraph.Visibility = working.ShowLiveGraph ? Visibility.Visible : Visibility.Collapsed;
		previewGraph.Stroke = new SolidColorBrush(palette.Accent);
		var cards = previewMetrics.Children.Cast<Border>().ToDictionary(b => (string)b.Tag);
		previewMetrics.Children.Clear();
		foreach (string key in working.HomeCardOrder)
		{
			if (!cards.TryGetValue(key, out Border card)) continue;
			card.Visibility = working.HiddenHomeCards.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
			card.Background = new SolidColorBrush(palette.Surface2); card.BorderBrush = new SolidColorBrush(palette.Border);
			card.Padding = new Thickness(8 * VisualDesign.Density(working)); card.CornerRadius = new CornerRadius(working.CornerRadius / 2);
			((TextBlock)card.Child).FontSize = 9 * working.TextScale; ((TextBlock)card.Child).Foreground = new SolidColorBrush(palette.Text);
			previewMetrics.Children.Add(card);
		}
		previewConnection.Apply(working, previewStatus, CanAnimate());
		VisualDesign.ApplyGlassSurface(previewSurface, working, true);
		if (previewCard.ActualWidth > 0) previewCard.Clip = new RectangleGeometry(new Rect(0,0,previewCard.ActualWidth,previewCard.ActualHeight),working.CornerRadius,working.CornerRadius);
	}

	private UIElement HomeCardOrderEditor()
	{
		Border card = Card(); StackPanel panel = new StackPanel();
		panel.Children.Add(Text("Порядок карточек", 12, palette.Text, FontWeights.SemiBold));
		ListBox list = new ListBox { Height = 125, Margin = new Thickness(0, 9, 0, 8), Background = new SolidColorBrush(palette.Input), Foreground = new SolidColorBrush(palette.Text), BorderBrush = new SolidColorBrush(palette.Border), ItemsSource = working.HomeCardOrder.Select(CardName).ToList() };
		StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
		Button up = ActionButton("Выше", false); Button down = ActionButton("Ниже", false); down.Margin = new Thickness(8, 0, 0, 0);
		up.Click += delegate { MoveCard(list, -1); };
		down.Click += delegate { MoveCard(list, 1); };
		actions.Children.Add(up); actions.Children.Add(down); panel.Children.Add(list); panel.Children.Add(actions); card.Child = panel; return card;
	}

	private void MoveCard(ListBox list, int offset)
	{
		int index = list.SelectedIndex;
		if (index < 0 || index + offset < 0 || index + offset >= working.HomeCardOrder.Count) return;
		string item = working.HomeCardOrder[index]; working.HomeCardOrder.RemoveAt(index); working.HomeCardOrder.Insert(index + offset, item);
		int nextIndex = index + offset;
		list.ItemsSource = null; list.ItemsSource = working.HomeCardOrder.Select(CardName).ToList(); list.SelectedIndex = nextIndex;
		NotifyVisualChanged();
	}

	private UIElement SliderRow(string title, string caption, double min, double max, double value, Action<double> setter, string format)
	{
		Border card = Card(); StackPanel panel = new StackPanel(); Grid header = TwoColumns(); StackPanel text = new StackPanel();
		text.Children.Add(Text(title, 12, palette.Text, FontWeights.SemiBold)); text.Children.Add(Text(caption, 9.5, palette.Muted)); header.Children.Add(text);
		TextBlock number = Text(FormatValue(value, format), 11, palette.Accent, FontWeights.SemiBold); number.HorizontalAlignment = HorizontalAlignment.Right; number.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(number, 1); header.Children.Add(number); panel.Children.Add(header);
		Slider slider = new Slider { Minimum = min, Maximum = max, Value = value, Height = 48, Margin = new Thickness(0, 8, 0, 0), IsSnapToTickEnabled = false };
		slider.ValueChanged += delegate { number.Text = FormatValue(slider.Value, format); setter(slider.Value); NotifyVisualChanged(); };
		panel.Children.Add(slider); card.Child = panel; return card;
	}

	private UIElement ChoiceRow(string title, IEnumerable<string> values, string selected, Action<string> setter)
	{
		Border card = Card(); StackPanel panel = new StackPanel(); panel.Children.Add(Text(title, 12, palette.Text, FontWeights.SemiBold));
		WrapPanel choices = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
		List<Button> chips = new List<Button>();
		foreach (string value in values)
		{
			string captured = value;
			Button chip = new Button { Content = DisplayOption(value), Height = 48, MinWidth = 88, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Tag = value };
			StyleChoice(chip, string.Equals(value, selected, StringComparison.OrdinalIgnoreCase));
			chip.Click += delegate
			{
				setter(captured);
				foreach (Button candidate in chips) StyleChoice(candidate, string.Equals((string)candidate.Tag, captured, StringComparison.OrdinalIgnoreCase));
				NotifyVisualChanged();
			};
			EnableAppearanceButtonMotion(chip);
			chips.Add(chip); choices.Children.Add(chip);
		}
		panel.Children.Add(choices); card.Child = panel; return card;
	}

	private void StyleChoice(Button button, bool selected)
	{
		button.Background = new SolidColorBrush(selected ? palette.Accent : palette.Surface2);
		button.Foreground = new SolidColorBrush(selected ? palette.AccentForeground : palette.Text);
		button.BorderBrush = new SolidColorBrush(selected ? palette.Accent : palette.Border);
		button.BorderThickness = new Thickness(1);
	}

	private UIElement CheckRow(string title, string caption, bool value, Action<bool> setter)
	{
		Border card = Card(); Grid grid = TwoColumns(); StackPanel text = new StackPanel();
		text.Children.Add(Text(title, 12, palette.Text, FontWeights.SemiBold)); text.Children.Add(Text(caption, 9.5, palette.Muted)); grid.Children.Add(text);
		bool isOn = value;
		Border track = new Border { Width = 48, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(isOn ? palette.Accent : palette.Border), HorizontalAlignment = HorizontalAlignment.Center };
		Ellipse thumb = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 4, 0), RenderTransform = new TranslateTransform(isOn ? 22 : 0, 0) };
		track.Child = thumb;
		Button toggle = new Button { Width = 68, Height = 48, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Content = track, ToolTip = title };
		toggle.Click += delegate
		{
			isOn = !isOn;
			setter(isOn);
			UpdateToggle(track, thumb, isOn);
			NotifyVisualChanged();
		};
		EnableAppearanceButtonMotion(toggle);
		Grid.SetColumn(toggle, 1); grid.Children.Add(toggle); card.Child = grid; return card;
	}

	private void UpdateToggle(Border track, Ellipse thumb, bool isOn)
	{
		Color from = (track.Background as SolidColorBrush)?.Color ?? palette.Border;
		Color to = isOn ? palette.Accent : palette.Border;
		SolidColorBrush brush = new SolidColorBrush(from); track.Background = brush;
		TranslateTransform movement = thumb.RenderTransform as TranslateTransform;
		if (movement == null) { movement = new TranslateTransform(); thumb.RenderTransform = movement; }
		double target = isOn ? 22.0 : 0.0;
		if (!CanAnimate()) { brush.Color = to; movement.X = target; return; }
		int? fps = working.AnimationFrameRate == 0 ? (int?)null : working.AnimationFrameRate;
		double duration = 150.0 / Math.Max(0.5, Math.Min(2.0, working.AnimationSpeed));
		ColorAnimation color = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
		DoubleAnimation slide = new DoubleAnimation(movement.X, target, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
		Timeline.SetDesiredFrameRate(color, fps); Timeline.SetDesiredFrameRate(slide, fps);
		brush.BeginAnimation(SolidColorBrush.ColorProperty, color);
		movement.BeginAnimation(TranslateTransform.XProperty, slide);
	}

	private Border Card() => new Border { Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Background = new SolidColorBrush(palette.Surface), BorderBrush = new SolidColorBrush(palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Math.Min(16, working.CornerRadius)) };
	private Grid TwoColumns() { Grid grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); return grid; }
	private TextBlock Section(string text, double top = 0) => new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(palette.Muted), Margin = new Thickness(2, top, 0, 9) };
	private TextBlock Text(string value, double size, Color color, FontWeight weight = default(FontWeight)) => new TextBlock { Text = value, FontSize = Math.Max(9.0, size * working.TextScale), FontWeight = weight == default(FontWeight) ? FontWeights.Normal : weight, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap };
	private Button ActionButton(string label, bool primary)
	{
		Button button = new Button { Content = label, Height = 48, MinWidth = 104, Padding = new Thickness(16, 0, 16, 0), Background = new SolidColorBrush(primary ? palette.Accent : palette.Surface2), Foreground = new SolidColorBrush(primary ? palette.AccentForeground : palette.Text), BorderBrush = new SolidColorBrush(primary ? palette.Accent : palette.Border), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
		EnableAppearanceButtonMotion(button);
		return button;
	}

	private void NotifyVisualChanged()
	{
		previewDirty = true;
		if (previewQueued || !IsLoaded) return;
		previewQueued = true;
		previewTimer.Start();
	}

	private void FlushPreview()
	{
		previewTimer.Stop();
		previewQueued = false;
		if (!previewDirty) return;
		previewDirty = false;
		UpdatePreviewCard();
	}

	private static string BackdropKey(VisualPreferences value) => string.Join("|", ThemePalette.NormalizeName(value?.ThemeName), value?.BackgroundMode ?? "", value?.HighContrast ?? false, value?.GlowIntensity ?? 0.0);

	private void RefreshControls(double scrollOffset = 0.0)
	{
		Build();
		mainScroll.UpdateLayout();
		mainScroll.ScrollToVerticalOffset(Math.Min(scrollOffset, mainScroll.ScrollableHeight));
	}

	private void SwitchTheme(string themeName)
	{
		FlushPreview();
		double offset = mainScroll.VerticalOffset;
		working.StoreActiveThemeSettings();
		working.LoadThemeSettings(themeName);
		RefreshControls(offset);
		AnimatePreview();
	}

	private bool CanAnimate() => animationsEnabled && !working.ReduceMotion && (!working.FollowSystemMotion || SystemParameters.ClientAreaAnimation);

	private void EnableAppearanceButtonMotion(Button button)
	{
		if (button == null) return;
		button.Template = VisualDesign.ButtonTemplate(working, 12);
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		button.RenderTransformOrigin = new Point(0.5, 0.5);
		button.RenderTransform = scale;
		button.MouseEnter += delegate { AnimateButtonScale(scale, 1.015); };
		button.MouseLeave += delegate { AnimateButtonScale(scale, 1.0); };
		button.PreviewMouseLeftButtonDown += delegate { AnimateButtonScale(scale, 0.988); };
		button.PreviewMouseLeftButtonUp += delegate { AnimateButtonScale(scale, 1.0); };
	}

	private void AnimateButtonScale(ScaleTransform scale, double target)
	{
		if (scale == null) return;
		if (!CanAnimate()) { scale.ScaleX = target; scale.ScaleY = target; return; }
		double duration = 105.0 / Math.Max(0.5, Math.Min(2.0, working.AnimationSpeed));
		int? fps = working.AnimationFrameRate == 0 ? (int?)null : working.AnimationFrameRate;
		DoubleAnimation horizontal = new DoubleAnimation(scale.ScaleX, target, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
		DoubleAnimation vertical = new DoubleAnimation(scale.ScaleY, target, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
		Timeline.SetDesiredFrameRate(horizontal, fps); Timeline.SetDesiredFrameRate(vertical, fps);
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, horizontal);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, vertical);
	}

	private void AnimatePreview()
	{
		if (previewCard == null || !CanAnimate()) return;
		double duration = 170.0 / Math.Max(0.5, Math.Min(2.0, working.AnimationSpeed));
		int? fps = working.AnimationFrameRate == 0 ? (int?)null : working.AnimationFrameRate;
		DoubleAnimation fade = new DoubleAnimation(0.84, 1.0, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
		Timeline.SetDesiredFrameRate(fade, fps);
		TranslateTransform movement = new TranslateTransform(0, 5); previewCard.RenderTransform = movement;
		DoubleAnimation slide = new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
		Timeline.SetDesiredFrameRate(slide, fps);
		previewCard.Opacity = 0.84;
		previewCard.BeginAnimation(UIElement.OpacityProperty, fade);
		movement.BeginAnimation(TranslateTransform.YProperty, slide);
	}

	private string FrameRateChoice() => working.AnimationFrameRate == 0 ? "Auto" : working.AnimationFrameRate.ToString();
	private static string CardName(string key) { switch (key) { case "Speed": return "Скорость"; case "Protocol": return "Протокол"; case "Latency": return "Задержка"; case "Session": return "Сеанс"; default: return "Приватность"; } }
	private static string ThemeLabel(string key) => ThemePalette.NormalizeName(key);
	private static string FormatValue(double value, string format) => format == "0%" ? value.ToString("0") + "%" : value.ToString(format);
	private static string DisplayOption(string value)
	{
		switch (value)
		{
			case "Compact": return "Компактно"; case "Standard": return "Стандартно"; case "Comfortable": return "Просторно";
			case "Detailed": return "Подробно"; case "Grid": return "Сетка";
			case "Solid": return "Сплошной"; case "Gradient": return "Мягкий градиент"; case "Glass": return "Стекло";
			case "Auto": return "Авто"; case "60": return "60 кадр./с"; case "120": return "120 кадр./с";
			default: return value;
		}
	}
}

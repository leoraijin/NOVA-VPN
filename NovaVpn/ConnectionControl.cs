using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace NovaVpn;

// One visual tree for the live connection and the appearance preview. Only
// transforms/opacity have continuous clocks; status changes never replace it.
public sealed class ConnectionControl : Grid
{
	public Button ActionButton { get; private set; }
	public TextBlock Label { get; private set; }
	public bool IsAnimating { get; private set; }
	public int RequestedFrameRate => preferences.AnimationFrameRate;
	private readonly Border face = new Border();
	private readonly Border highlight = new Border { IsHitTestVisible = false };
	private readonly GlassBackdropLayer backdrop = new GlassBackdropLayer { MaterialFill = false };
	private readonly Ellipse aura = new Ellipse { IsHitTestVisible = false };
	private readonly Ellipse ring = new Ellipse { IsHitTestVisible = false, StrokeThickness = 2 };
	private readonly RotateTransform rotation = new RotateTransform();
	private readonly ScaleTransform press = new ScaleTransform(1, 1);
	private readonly TranslateTransform keyDepth = new TranslateTransform();
	private readonly StackPanel content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
	private readonly Viewbox icon;
	private VisualPreferences preferences = new VisualPreferences();
	private CoreStatus status;
	private bool motionEnabled;
	private Window owner;

	public ConnectionControl()
	{
		Width = 220; Height = 220; ClipToBounds = false;
		aura.Width = 212; aura.Height = 212;
		Children.Add(aura);
		ring.Width = 190; ring.Height = 190; ring.RenderTransformOrigin = new Point(.5, .5); ring.RenderTransform = rotation;
		Children.Add(ring);
		Grid layers = new Grid(); layers.Children.Add(backdrop); layers.Children.Add(face); layers.Children.Add(highlight);
		layers.RenderTransform = keyDepth;
		icon = IconFactory.Create("power", 36, Colors.Black);
		icon.HorizontalAlignment = HorizontalAlignment.Center;
		Label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
		content.Children.Add(icon); content.Children.Add(Label); layers.Children.Add(content);
		ControlTemplate template = new ControlTemplate(typeof(Button));
		template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
		ActionButton = new Button { Content = layers, Template = template, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5), RenderTransform = press, Background = Brushes.Transparent };
		ActionButton.MouseEnter += delegate { AnimateScale(1.025); };
		ActionButton.MouseLeave += delegate { AnimateScale(1); };
		ActionButton.PreviewMouseLeftButtonDown += delegate { AnimateScale(.96); };
		ActionButton.PreviewMouseLeftButtonUp += delegate { AnimateScale(ActionButton.IsMouseOver ? 1.025 : 1); };
		ActionButton.PreviewMouseLeftButtonDown += delegate { SetKeyDepth(true); };
		ActionButton.PreviewMouseLeftButtonUp += delegate { SetKeyDepth(false); };
		ActionButton.LostMouseCapture += delegate { SetKeyDepth(false); AnimateScale(1); };
		ActionButton.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Space || e.Key == Key.Enter) { SetKeyDepth(true); AnimateScale(.97); } };
		ActionButton.PreviewKeyUp += delegate { SetKeyDepth(false); AnimateScale(1); };
		ActionButton.LostKeyboardFocus += delegate { SetKeyDepth(false); AnimateScale(1); };
		Children.Add(ActionButton);
		Loaded += delegate { owner = Window.GetWindow(this); if (owner != null) owner.StateChanged += OwnerStateChanged; RestartMotion(); };
		Unloaded += delegate { if (owner != null) owner.StateChanged -= OwnerStateChanged; owner = null; StopMotion(); };
		IsVisibleChanged += delegate { RestartMotion(); };
	}

	public void Apply(VisualPreferences visual, CoreStatus connectionStatus, bool allowMotion)
	{
		bool changedState = status != connectionStatus;
		Color previousColor = (face.Background as SolidColorBrush)?.Color ?? ThemePalette.Create(visual).Accent;
		bool restart = status != connectionStatus || motionEnabled != allowMotion || preferences.ThemeName != visual.ThemeName || preferences.AnimationSpeed != visual.AnimationSpeed || preferences.AnimationFrameRate != visual.AnimationFrameRate || preferences.ReduceMotion != visual.ReduceMotion || preferences.FollowSystemMotion != visual.FollowSystemMotion || preferences.HighContrast != visual.HighContrast || (preferences.GlowIntensity > 0) != (visual.GlowIntensity > 0);
		preferences = visual.Clone(); status = connectionStatus; motionEnabled = allowMotion;
		ThemePalette p = ThemePalette.Create(preferences);
		bool glass = p.IsGlass;
		bool amberOrb = p.Name == ThemePalette.AmberGlass;
		bool one = p.Name == ThemePalette.OneUi;
		bool active = status == CoreStatus.Connected;
		Color color = p.Accent;
		double radius = glass ? 86.0
			: one ? Math.Min(7, preferences.CornerRadius / 3.0)
			: Math.Max(2, Math.Min(8, preferences.CornerRadius / 4.5));
		ActionButton.Width = glass ? 172 : one ? 208 : 180;
		ActionButton.Height = glass ? 172 : one ? 88 : Math.Max(52, 42 * preferences.TextScale);
		face.CornerRadius = new CornerRadius(radius);
		backdrop.Radius = face.CornerRadius; backdrop.Preferences = preferences;
		face.Effect = glass && !preferences.HighContrast ? VisualDesign.KeyShadow(false, preferences.GlassShadow, VisualDesign.KeyShadowTint(p)) : null;
		face.BorderThickness = new Thickness(preferences.HighContrast ? 2 : 1);
		face.BorderBrush = preferences.HighContrast ? new SolidColorBrush(p.Text) : glass ? VisualDesign.GlassRim(preferences) : new SolidColorBrush(color);
		bool filled = one || !glass || amberOrb;
		Color foreground = ThemePalette.ReadableForeground(color);
		face.Background = preferences.HighContrast ? new SolidColorBrush(p.Surface) : amberOrb ? VisualDesign.AmberConnectionFill() : filled ? (Brush)new SolidColorBrush(color) : VisualDesign.GlassFill(preferences);
		highlight.CornerRadius = new CornerRadius(radius);
		highlight.Margin = new Thickness(2);
		highlight.Background = glass ? VisualDesign.GlassSheen(preferences) : new LinearGradientBrush(Color.FromArgb(35,255,255,255), Colors.Transparent,75);
		highlight.Visibility = preferences.HighContrast || !glass ? Visibility.Collapsed : Visibility.Visible;
		((Path)icon.Child).Stroke = new SolidColorBrush(filled ? foreground : color);
		content.Orientation = glass ? Orientation.Vertical : Orientation.Horizontal;
		icon.Width = icon.Height = glass ? 40 : one ? 28 : 20;
		icon.Margin = glass ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 12, 0);
		Label.FontSize = (glass ? 12 : 14) * preferences.TextScale;
		Label.VerticalAlignment = VerticalAlignment.Center;
		Label.Foreground = new SolidColorBrush(filled ? foreground : p.Text);
		Label.Text = active ? "Отключить" : status == CoreStatus.Connecting ? "Отменить" : status == CoreStatus.Error ? "Повторить" : "Подключить";
		AutomationProperties.SetName(ActionButton, Label.Text + " VPN");
		ActionButton.ToolTip = Label.Text + " защищённое соединение";
		aura.Fill = new RadialGradientBrush(Color.FromArgb((byte)(preferences.GlowIntensity * 200), color.R, color.G, color.B), Colors.Transparent);
		aura.Visibility = preferences.HighContrast || !glass ? Visibility.Hidden : Visibility.Visible;
		ring.Stroke = new SolidColorBrush(color);
		ring.StrokeDashArray = new DoubleCollection { 18, 180 };
		ring.Visibility = status == CoreStatus.Connecting ? Visibility.Visible : Visibility.Hidden;
		if (restart) RestartMotion();
		if (changedState && CanAnimate && face.Background is SolidColorBrush fill)
			Animate(fill, SolidColorBrush.ColorProperty, new ColorAnimation(previousColor, color, TimeSpan.FromMilliseconds(180 / preferences.AnimationSpeed)));
	}

	private bool CanAnimate => motionEnabled && IsLoaded && IsVisible && (owner == null || owner.WindowState != WindowState.Minimized) && !preferences.ReduceMotion && (!preferences.FollowSystemMotion || SystemParameters.ClientAreaAnimation);
	private void OwnerStateChanged(object sender, EventArgs e) { RestartMotion(); }
	private void StopMotion()
	{
		keyDepth.BeginAnimation(TranslateTransform.XProperty, null); keyDepth.BeginAnimation(TranslateTransform.YProperty, null);
		keyDepth.X = keyDepth.Y = 0;
		rotation.BeginAnimation(RotateTransform.AngleProperty, null);
		aura.BeginAnimation(OpacityProperty, null);
		press.BeginAnimation(ScaleTransform.ScaleXProperty, null); press.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		press.ScaleX = press.ScaleY = 1;
		IsAnimating = false;
	}
	private void RestartMotion()
	{
		StopMotion();
		if (!CanAnimate) return;
		if (ring.Visibility == Visibility.Visible)
		{
			Animate(rotation, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds((status == CoreStatus.Connecting ? 1100 : 9000) / preferences.AnimationSpeed)) { RepeatBehavior = RepeatBehavior.Forever });
			IsAnimating = true;
		}
	}
	private void AnimateScale(double value)
	{
		if (!CanAnimate) { press.BeginAnimation(ScaleTransform.ScaleXProperty, null); press.BeginAnimation(ScaleTransform.ScaleYProperty, null); press.ScaleX = press.ScaleY = 1; return; }
		double duration = (value < 1 ? 90 : 140) / preferences.AnimationSpeed;
		Animate(press, ScaleTransform.ScaleXProperty, new DoubleAnimation(press.ScaleX, value, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
		Animate(press, ScaleTransform.ScaleYProperty, new DoubleAnimation(press.ScaleY, value, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
	}
	private void SetKeyDepth(bool down)
	{
		ThemePalette palette = ThemePalette.Create(preferences);
		if (!palette.IsGlass || preferences.HighContrast) return;
		face.Effect = VisualDesign.KeyShadow(down, preferences.GlassShadow, VisualDesign.KeyShadowTint(palette));
		double target = down ? 2 : 0;
		if (CanAnimate) {
			Animate(keyDepth, TranslateTransform.XProperty, new DoubleAnimation(keyDepth.X, target * .75, TimeSpan.FromMilliseconds(95 / preferences.AnimationSpeed)));
			Animate(keyDepth, TranslateTransform.YProperty, new DoubleAnimation(keyDepth.Y, target, TimeSpan.FromMilliseconds(95 / preferences.AnimationSpeed)));
		} else { keyDepth.X = target * .75; keyDepth.Y = target; }
	}
	private void Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
	{
		Timeline.SetDesiredFrameRate(animation, preferences.AnimationFrameRate == 0 ? (int?)null : preferences.AnimationFrameRate);
		target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
	}
}

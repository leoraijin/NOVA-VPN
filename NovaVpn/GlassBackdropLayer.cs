using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace NovaVpn;

// Samples only the decorative backdrop, never text, controls or the layer itself.
// Viewboxes update on layout changes; there is no per-frame bitmap capture or timer.
public sealed class GlassBackdropLayer : Grid
{
	public static readonly DependencyProperty PreferencesProperty = DependencyProperty.Register(nameof(Preferences), typeof(VisualPreferences), typeof(GlassBackdropLayer), new PropertyMetadata(null, Changed));
	public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(CornerRadius), typeof(GlassBackdropLayer), new PropertyMetadata(new CornerRadius(12), Changed));
	public static readonly DependencyProperty MaterialFillProperty = DependencyProperty.Register(nameof(MaterialFill), typeof(bool), typeof(GlassBackdropLayer), new PropertyMetadata(true, Changed));
	public VisualPreferences Preferences { get => (VisualPreferences)GetValue(PreferencesProperty); set => SetValue(PreferencesProperty, value); }
	public CornerRadius Radius { get => (CornerRadius)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
	public bool MaterialFill { get => (bool)GetValue(MaterialFillProperty); set => SetValue(MaterialFillProperty, value); }
	private readonly Border sample = new Border { Margin = new Thickness(-24) };
	private readonly Border tint = new Border();
	private FrameworkElement source;
	private VisualBrush brush;
	private Rect previous = Rect.Empty;
	private bool retrySource;
	public bool HasBackdrop => brush != null;
	public double AppliedBlur => (sample.Effect as BlurEffect)?.Radius ?? 0;
	public GlassBackdropLayer()
	{
		IsHitTestVisible = false;
		Children.Add(sample); Children.Add(tint);
		Loaded += delegate { Refresh(); };
		Unloaded += delegate { source = null; brush = null; sample.Background = null; previous = Rect.Empty; };
		SizeChanged += delegate { UpdateView(); };
		LayoutUpdated += delegate { if (IsLoaded && IsVisible && Preferences?.GlassBlur > 0) { if (retrySource) Refresh(); else UpdateView(); } };
	}
	private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
	{
		GlassBackdropLayer layer = (GlassBackdropLayer)sender;
		if (args.Property == RadiusProperty) { layer.UpdateClip(); return; }
		if (args.Property == MaterialFillProperty) { layer.UpdateTint(); return; }
		if (args.Property != PreferencesProperty) return;
		VisualPreferences before = args.OldValue as VisualPreferences;
		VisualPreferences after = args.NewValue as VisualPreferences;
		if (RequiresBackdropRefresh(before, after)) layer.Refresh();
		else
		{
			if (before != null && after != null && before.GlassBlur != after.GlassBlur) layer.UpdateBlur();
			if (RequiresTintRefresh(before, after)) layer.UpdateTint();
		}
	}
	private static bool RequiresBackdropRefresh(VisualPreferences before, VisualPreferences after)
	{
		if (before == null || after == null) return true;
		bool wasEnabled = ThemePalette.Create(before).IsGlass && !before.HighContrast;
		bool isEnabled = ThemePalette.Create(after).IsGlass && !after.HighContrast;
		return wasEnabled != isEnabled;
	}
	private static bool RequiresTintRefresh(VisualPreferences before, VisualPreferences after)
	{
		if (before == null || after == null) return true;
		return !string.Equals(ThemePalette.NormalizeName(before.ThemeName), ThemePalette.NormalizeName(after.ThemeName), StringComparison.OrdinalIgnoreCase)
			|| before.GlassFrost != after.GlassFrost
			|| before.SurfaceOpacity != after.SurfaceOpacity
			|| before.HighContrast != after.HighContrast;
	}
	private void Refresh()
	{
		retrySource = false;
		VisualPreferences v = Preferences;
		bool enabled = v != null && ThemePalette.Create(v).IsGlass && !v.HighContrast;
		Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
		UpdateTint();
		UpdateBlur();
		UpdateView();
	}
	private void UpdateBlur()
	{
		VisualPreferences v = Preferences;
		bool enabled = v != null && ThemePalette.Create(v).IsGlass && !v.HighContrast;
		if (!enabled || v.GlassBlur <= 0)
		{
			sample.Background = null; sample.Effect = null; source = null; brush = null; previous = Rect.Empty;
			return;
		}
		if (brush == null || source == null)
		{
			source = FindSource();
			if (source != null)
			{
				brush = new VisualBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
				RenderOptions.SetCachingHint(brush, CachingHint.Cache);
				sample.Background = brush;
			}
			previous = Rect.Empty;
		}
		BlurEffect effect = sample.Effect as BlurEffect;
		if (effect == null)
		{
			effect = new BlurEffect { RenderingBias = RenderingBias.Performance };
			sample.Effect = effect;
		}
		effect.Radius = Math.Max(0, Math.Min(18, v.GlassBlur));
	}
	private void UpdateTint()
	{
		VisualPreferences v = Preferences;
		if (v == null || !ThemePalette.Create(v).IsGlass || v.HighContrast || !MaterialFill)
			tint.Background = Brushes.Transparent;
		else
		{
			var fill = VisualDesign.GlassFill(v).Clone();
			fill.Opacity = Math.Max(0, Math.Min(1, v.SurfaceOpacity));
			fill.Freeze();
			tint.Background = fill;
		}
		tint.CornerRadius = Radius;
	}
	private void UpdateClip()
	{
		Rect bounds = new Rect(0, 0, ActualWidth, ActualHeight);
		var clip = Clip as RectangleGeometry;
		if (clip == null || clip.Rect != bounds || clip.RadiusX != Radius.TopLeft)
			Clip = new RectangleGeometry(bounds, Radius.TopLeft, Radius.TopLeft);
		tint.CornerRadius = Radius;
	}
	private FrameworkElement FindSource()
	{
		for (DependencyObject ancestor = VisualTreeHelper.GetParent(this); ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
		{
			FrameworkElement result = FindBackdrop(ancestor);
			if (result != null) return result;
		}
		return null;
	}
	private FrameworkElement FindBackdrop(DependencyObject node)
	{
		if (node == this) return null;
		if (node is FrameworkElement element && Equals(element.Tag, "NovaDecorativeBackdrop")) return element;
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
		{
			FrameworkElement result = FindBackdrop(VisualTreeHelper.GetChild(node, i));
			if (result != null) return result;
		}
		return null;
	}
	private void UpdateView()
	{
		if (ActualWidth <= 0 || ActualHeight <= 0) return;
		Rect bounds = new Rect(0, 0, ActualWidth, ActualHeight);
		UpdateClip();
		if (brush == null || source == null) return;
		try
		{
			Point origin = TransformToVisual(source).Transform(new Point(-24, -24));
			Rect view = new Rect(origin.X, origin.Y, ActualWidth + 48, ActualHeight + 48);
			if (view == previous) return;
			previous = view; brush.Viewbox = view;
		}
		catch (InvalidOperationException) { source = null; brush = null; sample.Background = null; retrySource = true; }
	}
}

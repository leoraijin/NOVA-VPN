using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Documents;
using System.Windows.Shapes;

namespace NovaVpn;

public static class VisualDesign
{
	public static ControlTemplate ButtonTemplate(VisualPreferences v, double radius)
	{
		ThemePalette p = ThemePalette.Create(v);
		if (p.IsGlass && !v.HighContrast) return GlassButtonTemplate(v, radius);
		ControlTemplate template = new ControlTemplate(typeof(Button));
		FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border), "ButtonFace");
		border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
		border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
		border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
		border.SetValue(Border.CornerRadiusProperty, new CornerRadius(ButtonRadius(v, radius)));
		FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
		presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
		border.AppendChild(presenter); template.VisualTree = border;
		Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(p.AccentLight))); template.Triggers.Add(hover);
		Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
		Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
		pressed.Setters.Add(new Setter(UIElement.OpacityProperty, p.Name == ThemePalette.OneUi ? .72 : .84, "ButtonFace")); template.Triggers.Add(pressed);
		Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
		focused.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(p.AccentLight), "ButtonFace"));
		focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "ButtonFace")); template.Triggers.Add(focused);
		return template;
	}
	public static double ButtonRadius(VisualPreferences v, double radius) => Math.Min(ThemePalette.NormalizeName(v.ThemeName) == ThemePalette.OneUi ? 7 : 4, v.CornerRadius * radius / 40);
	public static double Density(VisualPreferences v) => v.Density == "Compact" ? .82 : v.Density == "Comfortable" ? 1.2 : 1;
	public static FontFamily InterfaceFont(VisualPreferences v) => new FontFamily(ThemePalette.NormalizeName(v?.ThemeName) == ThemePalette.OneUi ? "Segoe UI" : "Segoe UI Variable, Segoe UI");
	private sealed class GlassSurfaceParts
	{
		public GlassBackdropLayer Layer;
		public Border Content;
	}
	private static readonly DependencyProperty SurfacePartsProperty = DependencyProperty.RegisterAttached("SurfaceParts", typeof(GlassSurfaceParts), typeof(VisualDesign));
	public static void ApplyGlassSurface(Border surface, VisualPreferences v, bool controlLayer = false)
	{
		if (surface == null || v == null) return;
		ThemePalette palette = ThemePalette.Create(v);
		if (!controlLayer)
		{
			// Content and controls are distinct material layers; no glass/reflection on data cards.
			Color material = palette.Surface;
			material.A = palette.IsGlass && !v.HighContrast ? (byte)(205 + 40 * Math.Max(0, Math.Min(1, v.SoftContrast))) : (byte)255;
			surface.Background = new SolidColorBrush(material);
			if (palette.Name == ThemePalette.OneUi && !v.HighContrast)
			{
				surface.BorderBrush = new SolidColorBrush(Color.FromArgb(65, palette.Border.R, palette.Border.G, palette.Border.B));
				surface.Effect = new DropShadowEffect { Color = Color.FromRgb(64, 46, 84), Direction = 270, BlurRadius = 12, ShadowDepth = 2, Opacity = .07 };
			}
			return;
		}
		var parts = surface.GetValue(SurfacePartsProperty) as GlassSurfaceParts;
		if (parts == null)
		{
			if (!ThemePalette.Create(v).IsGlass || v.HighContrast || surface.Child == null) return;
			UIElement original = surface.Child; surface.Child = null;
			parts = new GlassSurfaceParts { Layer = new GlassBackdropLayer(), Content = new Border { Child = original } };
			Grid layers = new Grid(); layers.Children.Add(parts.Layer); layers.Children.Add(parts.Content);
			surface.Child = layers; surface.SetValue(SurfacePartsProperty, parts);
		}
		parts.Content.Padding = surface.Padding; surface.Padding = new Thickness(0);
		parts.Layer.Radius = surface.CornerRadius; parts.Layer.Preferences = v.Clone();
		surface.Background = v.HighContrast ? new SolidColorBrush(ThemePalette.Create(v).Surface) : Brushes.Transparent;
	}
	public static Brush GlassFill(VisualPreferences v)
	{
		double frost = Math.Max(0, Math.Min(1, v.GlassFrost));
		ThemePalette palette = ThemePalette.Create(v);
		Color first = palette.Name == ThemePalette.AmberGlass ? ThemePalette.Parse("#FFF7EA") : Colors.White;
		Color second = palette.Name == ThemePalette.AmberGlass ? ThemePalette.Parse("#F5D8B6") : ThemePalette.Parse("#DBE7F9");
		var fill = new LinearGradientBrush(Color.FromArgb((byte)(24 + 206 * frost), first.R, first.G, first.B), Color.FromArgb((byte)(16 + 209 * frost), second.R, second.G, second.B), new Point(0, 0), new Point(1, 1));
		fill.Freeze(); return fill;
	}
	public static Color KeyShadowTint(ThemePalette palette) => palette?.Name == ThemePalette.AmberGlass ? Color.FromRgb(119, 72, 39) : Color.FromRgb(47, 64, 95);
	public static DropShadowEffect KeyShadow(bool pressed = false, double strength = 1.0, Color? tint = null) => new DropShadowEffect { Color = tint ?? Color.FromRgb(47, 64, 95), Direction = 315, BlurRadius = pressed ? 4 : 10, ShadowDepth = pressed ? 1 : 4, Opacity = (pressed ? .10 : .19) * Math.Max(0, Math.Min(1, strength)) };
	public static Brush AmberConnectionFill()
	{
		var brush = new LinearGradientBrush();
		brush.StartPoint = new Point(0.18, 0.05); brush.EndPoint = new Point(0.82, 0.96);
		brush.GradientStops.Add(new GradientStop(ThemePalette.Parse("#E4772D"), 0));
		brush.GradientStops.Add(new GradientStop(ThemePalette.Parse("#CF591A"), .56));
		brush.GradientStops.Add(new GradientStop(ThemePalette.Parse("#AE410F"), 1));
		brush.Freeze(); return brush;
	}
	public static Brush GlassSheen(VisualPreferences v)
	{
		var brush = new LinearGradientBrush(Color.FromArgb((byte)((24 - 12 * Math.Max(0, Math.Min(1, v.GlassFrost))) * v.GlassHighlight),255,255,255),Colors.Transparent,new Point(0,0),new Point(1,1));
		brush.Freeze(); return brush;
	}
	public static Brush GlassRim(VisualPreferences v)
	{
		ThemePalette palette = ThemePalette.Create(v);
		Color edge = palette.Name == ThemePalette.AmberGlass ? palette.AccentLight : Color.FromRgb(130, 151, 187);
		var brush = new LinearGradientBrush(Color.FromArgb((byte)((150 - 60 * Math.Max(0, Math.Min(1, v.GlassFrost))) * v.GlassHighlight),255,255,255),Color.FromArgb((byte)(45 * v.GlassHighlight),edge.R,edge.G,edge.B),new Point(0,0),new Point(1,1));
		brush.Freeze(); return brush;
	}
	private static ControlTemplate GlassButtonTemplate(VisualPreferences v, double radius)
	{
		var template = new ControlTemplate(typeof(Button));
		var root = new FrameworkElementFactory(typeof(Grid), "KeyBody");
		var corners = new CornerRadius(ButtonRadius(v, radius));
		var backdrop = new FrameworkElementFactory(typeof(GlassBackdropLayer), "GlassBackdrop");
		backdrop.SetValue(GlassBackdropLayer.RadiusProperty, corners);
		backdrop.SetValue(GlassBackdropLayer.MaterialFillProperty, false);
		backdrop.SetResourceReference(GlassBackdropLayer.PreferencesProperty, "NovaGlassPreferences");
		root.AppendChild(backdrop);
		var face = new FrameworkElementFactory(typeof(Border), "GlassFace");
		face.SetValue(Border.CornerRadiusProperty, corners);
		face.SetResourceReference(Border.BackgroundProperty, "NovaGlassFill");
		face.SetValue(Border.BorderThicknessProperty, new Thickness(1));
		face.SetResourceReference(Border.BorderBrushProperty, "NovaGlassRim");
		face.SetResourceReference(UIElement.EffectProperty, "NovaGlassShadow"); root.AppendChild(face);
		var tint = new FrameworkElementFactory(typeof(Border), "GlassTint");
		tint.SetValue(Border.CornerRadiusProperty, corners);
		tint.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
		tint.SetValue(UIElement.OpacityProperty, .04); root.AppendChild(tint);
		var sheen = new FrameworkElementFactory(typeof(Border), "Sheen");
		sheen.SetValue(Border.CornerRadiusProperty, corners);
		sheen.SetValue(Border.MarginProperty, new Thickness(1));
		sheen.SetResourceReference(Border.BackgroundProperty, "NovaGlassSheen");
		root.AppendChild(sheen);
		var content = new FrameworkElementFactory(typeof(ContentPresenter));
		content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
		content.SetValue(TextElement.ForegroundProperty, new SolidColorBrush(ThemePalette.Create(v).Text));
		root.AppendChild(content); template.VisualTree = root;
		var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
		pressed.Setters.Add(new Setter(UIElement.RenderTransformProperty, new TranslateTransform(1.5, 2), "KeyBody"));
		pressed.Setters.Add(new Setter(UIElement.EffectProperty, new DynamicResourceExtension("NovaGlassPressedShadow"), "GlassFace"));
		pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .35, "Sheen"));
		var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(UIElement.OpacityProperty, .8, "Sheen")); template.Triggers.Add(hover);
		template.Triggers.Add(pressed);
		var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
		focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("NovaAccentBrush"), "GlassFace"));
		focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "GlassFace"));
		template.Triggers.Add(focus);
		var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
		return template;
	}
	public static Brush Surface(VisualPreferences v, bool prominent)
	{
		ThemePalette p = ThemePalette.Create(v);
		if (v.HighContrast || p.Name == ThemePalette.OneUi) return new SolidColorBrush(p.Surface);
		Color edge = ThemePalette.Blend(p.Surface2, p.AccentLight, Math.Min(.16, v.GlowIntensity * .3));
		LinearGradientBrush brush = new LinearGradientBrush(edge, p.Surface, p.IsGlass ? 75 : 25);
		brush.Freeze(); return brush;
	}
	public static Grid Backdrop(VisualPreferences v)
	{
		Grid layer = new Grid { Tag = "NovaDecorativeBackdrop", IsHitTestVisible = false, ClipToBounds = true };
		UpdateBackdrop(layer, v);
		return layer;
	}
	public static void UpdateBackdrop(Grid layer, VisualPreferences v)
	{
		if (layer == null || v == null) return;
		ThemePalette p = ThemePalette.Create(v);
		layer.Tag = "NovaDecorativeBackdrop";
		layer.IsHitTestVisible = false;
		layer.ClipToBounds = true;
		layer.Children.Clear();
		layer.Background = new SolidColorBrush(p.Bg);
		if (v.HighContrast || v.BackgroundMode == "Solid") return;
		layer.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(p.BackdropStart, 0), new GradientStop(p.BackdropMiddle, .5), new GradientStop(p.BackdropEnd, 1) }, 35);
		if (v.BackgroundMode == "Glass")
		{
			layer.Children.Add(new Ellipse { Width = 800, Height = 700, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -180, -140, 0), Fill = new RadialGradientBrush(Color.FromArgb((byte)(30 + v.GlowIntensity * 180), p.AccentLight.R, p.AccentLight.G, p.AccentLight.B), Colors.Transparent) });
			Color secondaryGlow = p.Name == ThemePalette.AmberGlass ? ThemePalette.Parse("#5F9FCB") : Color.FromRgb(96, 185, 189);
			layer.Children.Add(new Ellipse { Width = 900, Height = 700, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-200, 0, 0, -300), Fill = new RadialGradientBrush(Color.FromArgb((byte)(20 + v.GlowIntensity * 120), secondaryGlow.R, secondaryGlow.G, secondaryGlow.B), Colors.Transparent) });
		}
	}
}

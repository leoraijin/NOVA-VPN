using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace NovaVpn;

public static class VisualResourceManager
{
	private static bool installed;

	public static void Apply(Application application, ThemePalette palette, VisualPreferences visual = null)
	{
		if (application == null || palette == null) return;
		var textStyle = new Style(typeof(TextBlock));
		textStyle.Setters.Add(new Setter(TextOptions.TextRenderingModeProperty, TextRenderingMode.Grayscale));
		textStyle.Setters.Add(new Setter(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display));
		application.Resources[typeof(TextBlock)] = textStyle;
		application.Resources["NovaAccentBrush"] = new SolidColorBrush(palette.Accent);
		application.Resources["NovaSurfaceBrush"] = new SolidColorBrush(palette.Surface2);
		application.Resources["NovaBorderBrush"] = new SolidColorBrush(palette.Border);
		application.Resources["NovaTextBrush"] = new SolidColorBrush(palette.Text);
		application.Resources["NovaSelectionBrush"] = new SolidColorBrush(palette.SoftSelection);
		application.Resources["NovaPopupBrush"] = new SolidColorBrush(Color.FromRgb(palette.Surface.R, palette.Surface.G, palette.Surface.B));
		visual = visual ?? new VisualPreferences();
		application.Resources["NovaGlassFill"] = VisualDesign.GlassFill(visual);
		application.Resources["NovaGlassSheen"] = VisualDesign.GlassSheen(visual);
		application.Resources["NovaGlassRim"] = VisualDesign.GlassRim(visual);
		application.Resources["NovaGlassPreferences"] = visual.Clone();
		Color shadowTint = VisualDesign.KeyShadowTint(palette);
		application.Resources["NovaGlassShadow"] = VisualDesign.KeyShadow(false, visual.GlassShadow, shadowTint);
		application.Resources["NovaGlassPressedShadow"] = VisualDesign.KeyShadow(true, visual.GlassShadow, shadowTint);
		application.Resources["NovaSelectorFill"] = palette.IsGlass && !visual.HighContrast ? VisualDesign.GlassFill(visual) : new SolidColorBrush(palette.Surface2);
		application.Resources["NovaSelectorShadow"] = palette.IsGlass && !visual.HighContrast ? VisualDesign.KeyShadow(false, visual.GlassShadow) : new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0 };
		application.Resources["NovaSelectorPressedShadow"] = palette.IsGlass && !visual.HighContrast ? VisualDesign.KeyShadow(true, visual.GlassShadow) : new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0 };
		application.Resources["NovaSelectorPressedOffset"] = new TranslateTransform(palette.IsGlass ? 1 : 0, palette.IsGlass ? 1 : 0);
		if (palette.IsGlass) {
			var buttons = new Style(typeof(Button));
			buttons.Setters.Add(new Setter(Control.TemplateProperty, VisualDesign.ButtonTemplate(visual, 12)));
			application.Resources[typeof(Button)] = buttons;
		} else application.Resources.Remove(typeof(Button));
		application.Resources["NovaControlRadius"] = new CornerRadius(Math.Min(7, visual.CornerRadius * .35));
		application.Resources["NovaControlFontSize"] = 12.0 * visual.TextScale;
		application.Resources["NovaControlHeight"] = Math.Max(34.0 * VisualDesign.Density(visual), 30.0 * visual.TextScale);
		if (installed) return;
		installed = true;
		application.Resources[typeof(ScrollBar)] = XamlReader.Parse(ScrollBarStyle) as Style;
		application.Resources[typeof(Slider)] = XamlReader.Parse(SliderStyle) as Style;
		application.Resources["NovaSelectorStyle"] = XamlReader.Parse(SelectorStyle) as Style;
	}

	private const string SelectorStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:nova='clr-namespace:NovaVpn;assembly=NOVA VPN' TargetType='{x:Type ComboBox}'>
  <Setter Property='MinHeight' Value='{DynamicResource NovaControlHeight}'/>
  <Setter Property='FontSize' Value='{DynamicResource NovaControlFontSize}'/>
  <Setter Property='Foreground' Value='{DynamicResource NovaTextBrush}'/>
  <Setter Property='ItemContainerStyle'><Setter.Value><Style TargetType='{x:Type ComboBoxItem}'>
    <Setter Property='Padding' Value='10,7'/><Setter Property='Foreground' Value='{DynamicResource NovaTextBrush}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'>
      <Border x:Name='Item' CornerRadius='{DynamicResource NovaControlRadius}' Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>
      <ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Item' Property='Background' Value='{DynamicResource NovaSelectionBrush}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='Item' Property='Background' Value='{DynamicResource NovaSelectionBrush}'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style></Setter.Value></Setter>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'>
    <Grid>
      <ToggleButton Focusable='False' ClickMode='Press' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
        <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'>
          <Grid x:Name='KeyBody'>
          <nova:GlassBackdropLayer Preferences='{DynamicResource NovaGlassPreferences}' Radius='{DynamicResource NovaControlRadius}' MaterialFill='False'/>
          <Border x:Name='Frame' CornerRadius='{DynamicResource NovaControlRadius}' Background='{DynamicResource NovaSelectorFill}' BorderBrush='{DynamicResource NovaBorderBrush}' BorderThickness='1' Effect='{DynamicResource NovaSelectorShadow}'>
            <Path Data='M 0 0 L 4 4 L 8 0' Stroke='{DynamicResource NovaTextBrush}' StrokeThickness='1.2' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='12'/>
          </Border>
          </Grid>
          <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='{DynamicResource NovaAccentBrush}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Frame' Property='Effect' Value='{DynamicResource NovaSelectorPressedShadow}'/><Setter TargetName='KeyBody' Property='RenderTransform' Value='{DynamicResource NovaSelectorPressedOffset}'/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate></ToggleButton.Template>
      </ToggleButton>
      <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' Margin='12,6,32,6' VerticalAlignment='Center' IsHitTestVisible='False'/>
      <Border x:Name='FocusOutline' CornerRadius='{DynamicResource NovaControlRadius}' BorderBrush='{DynamicResource NovaAccentBrush}' BorderThickness='2' IsHitTestVisible='False' Opacity='0'/>
      <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
        <Border MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='{TemplateBinding MaxDropDownHeight}' Background='{DynamicResource NovaPopupBrush}' BorderBrush='{DynamicResource NovaBorderBrush}' BorderThickness='1' CornerRadius='{DynamicResource NovaControlRadius}' Padding='4' Margin='0,4,0,0'>
          <ScrollViewer CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
        </Border>
      </Popup>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.55'/></Trigger>
      <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='FocusOutline' Property='Opacity' Value='1'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>";

	private const string ScrollBarStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>
  <Setter Property='Width' Value='9'/><Setter Property='Margin' Value='2'/><Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
    <Grid Background='Transparent'>
      <Track x:Name='PART_Track' Orientation='{TemplateBinding Orientation}' IsDirectionReversed='True' Focusable='False'>
        <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Opacity='0'/></Track.DecreaseRepeatButton>
        <Track.Thumb><Thumb MinHeight='38'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border x:Name='Grip' Background='{DynamicResource NovaBorderBrush}' CornerRadius='3' Width='6' HorizontalAlignment='Center' Opacity='0.7'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Opacity' Value='1'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='Grip' Property='Background' Value='{DynamicResource NovaAccentBrush}'/></Trigger><DataTrigger Binding='{Binding Orientation, RelativeSource={RelativeSource AncestorType={x:Type ScrollBar}}}' Value='Horizontal'><Setter TargetName='Grip' Property='Width' Value='Auto'/><Setter TargetName='Grip' Property='Height' Value='6'/><Setter TargetName='Grip' Property='HorizontalAlignment' Value='Stretch'/></DataTrigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Opacity='0'/></Track.IncreaseRepeatButton>
      </Track>
    </Grid>
  </ControlTemplate></Setter.Value></Setter>
</Style>";

	private const string SliderStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Slider}'>
  <Setter Property='Height' Value='22'/><Setter Property='Foreground' Value='{DynamicResource NovaAccentBrush}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Slider}'>
    <Grid>
      <Border Height='4' CornerRadius='2' Background='{DynamicResource NovaBorderBrush}' VerticalAlignment='Center'/>
      <Track x:Name='PART_Track'>
        <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static Slider.DecreaseLarge}' Background='{DynamicResource NovaAccentBrush}' Height='4' VerticalAlignment='Center' BorderThickness='0'/></Track.DecreaseRepeatButton>
        <Track.Thumb><Thumb Width='16' Height='16'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Ellipse Fill='{DynamicResource NovaAccentBrush}' Stroke='{DynamicResource NovaTextBrush}' StrokeThickness='1'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static Slider.IncreaseLarge}' Background='Transparent' Height='4' VerticalAlignment='Center' BorderThickness='0'/></Track.IncreaseRepeatButton>
      </Track>
    </Grid>
  </ControlTemplate></Setter.Value></Setter>
</Style>";
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NovaVpn;

public sealed class TextPromptWindow : Window
{
	private readonly TextBox input;

	public string Value => input.Text.Trim();

	private readonly ThemePalette palette;
	private readonly VisualPreferences preferences;

	public TextPromptWindow(string title, string caption, string initialValue, VisualPreferences visual = null)
	{
		preferences = (visual ?? new VisualPreferences()).Clone();
		palette = ThemePalette.Create(preferences);
		Resources["NovaGlassFill"] = VisualDesign.GlassFill(preferences);
		Resources["NovaGlassSheen"] = VisualDesign.GlassSheen(preferences);
		Resources["NovaGlassRim"] = VisualDesign.GlassRim(preferences);
		Resources["NovaGlassPreferences"] = preferences.Clone();
		Resources["NovaGlassShadow"] = VisualDesign.KeyShadow(false, preferences.GlassShadow);
		Resources["NovaGlassPressedShadow"] = VisualDesign.KeyShadow(true, preferences.GlassShadow);
		Title = title;
		Width = System.Math.Min(560.0, SystemParameters.WorkArea.Width);
		Height = System.Math.Min(280.0, SystemParameters.WorkArea.Height);
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Background = new SolidColorBrush(palette.Bg);
		Foreground = new SolidColorBrush(palette.Text);
		FontFamily = VisualDesign.InterfaceFont(preferences);
		Grid root = new Grid { Margin = new Thickness(24.0) };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12.0) });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46.0 * preferences.TextScale) });
		root.RowDefinitions.Add(new RowDefinition());
		TextBlock label = new TextBlock { Text = caption, FontSize = 12.0 * preferences.TextScale, Foreground = new SolidColorBrush(palette.Text), TextWrapping = TextWrapping.Wrap };
		root.Children.Add(label);
		input = new TextBox
		{
			Text = initialValue ?? "",
			FontSize = 12.0 * preferences.TextScale,
			Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
			Background = new SolidColorBrush(palette.Input),
			Foreground = new SolidColorBrush(palette.Text),
			CaretBrush = new SolidColorBrush(palette.AccentLight),
			BorderBrush = new SolidColorBrush(palette.Border),
			BorderThickness = new Thickness(1.0)
		};
		Grid.SetRow(input, 2);
		root.Children.Add(input);
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
		Button cancel = MakeButton("Отмена", false);
		cancel.Click += delegate { DialogResult = false; };
		Button ok = MakeButton("Сохранить", true);
		ok.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		ok.Click += delegate { DialogResult = true; };
		buttons.Children.Add(cancel);
		buttons.Children.Add(ok);
		Grid.SetRow(buttons, 3);
		root.Children.Add(buttons);
		Content = root;
		Loaded += delegate { input.Focus(); input.SelectAll(); };
		PreviewKeyDown += delegate(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter) { DialogResult = true; }
			if (e.Key == Key.Escape) { DialogResult = false; }
		};
	}

	private Button MakeButton(string text, bool primary)
	{
		return new Button
		{
			Content = text,
			MinWidth = 90.0,
			Height = System.Math.Max(48.0, 48.0 * VisualDesign.Density(preferences)),
			FontSize = 12.0 * preferences.TextScale,
			Template = VisualDesign.ButtonTemplate(preferences, 16),
			Padding = new Thickness(12.0, 0.0, 12.0, 0.0),
			Background = new SolidColorBrush(primary ? palette.Accent : palette.Surface2),
			Foreground = new SolidColorBrush(primary ? palette.AccentForeground : palette.Text),
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand
		};
	}
}

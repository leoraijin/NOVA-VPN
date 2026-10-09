using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace NovaVpn;

public sealed class ImportWindow : Window
{
	private readonly TextBox input;

	public string ImportText => input.Text;

	public ImportWindow(VisualPreferences visual = null)
	{
		VisualPreferences preferences = visual ?? new VisualPreferences();
		ThemePalette palette = ThemePalette.Create(preferences);
		Resources["NovaGlassFill"] = VisualDesign.GlassFill(preferences);
		Resources["NovaGlassSheen"] = VisualDesign.GlassSheen(preferences);
		Resources["NovaGlassRim"] = VisualDesign.GlassRim(preferences);
		Resources["NovaGlassPreferences"] = preferences.Clone();
		Resources["NovaGlassShadow"] = VisualDesign.KeyShadow(false, preferences.GlassShadow);
		Resources["NovaGlassPressedShadow"] = VisualDesign.KeyShadow(true, preferences.GlassShadow);
		base.Title = "Добавить VPN-ключ";
		base.Width = System.Math.Min(660.0, SystemParameters.WorkArea.Width);
		base.Height = System.Math.Min(480.0, SystemParameters.WorkArea.Height);
		base.MinWidth = System.Math.Min(520.0, base.Width);
		base.MinHeight = System.Math.Min(380.0, base.Height);
		base.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.CanResize;
		base.Background = new SolidColorBrush(palette.Bg);
		base.Foreground = new SolidColorBrush(palette.Text);
		base.FontFamily = VisualDesign.InterfaceFont(preferences);
		WindowChrome.SetWindowChrome(this, new WindowChrome
		{
			CaptionHeight = 0.0,
			ResizeBorderThickness = new Thickness(6.0),
			CornerRadius = new CornerRadius(0.0),
			GlassFrameThickness = new Thickness(0.0)
		});
		Grid grid = new Grid
		{
			Margin = new Thickness(26.0)
		};
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(12.0)
		});
		grid.RowDefinitions.Add(new RowDefinition());
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(14.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(50.0)
		});
		Grid grid2 = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(),
				new ColumnDefinition
				{
					Width = new GridLength(48.0)
				}
			}
		};
		StackPanel element = new StackPanel
		{
			Children =
			{
				(UIElement)new TextBlock
				{
					Text = "Добавить сервер",
					FontSize = (palette.Name == ThemePalette.OneUi ? 26.0 : 21.0) * preferences.TextScale,
					FontWeight = FontWeights.SemiBold,
					Foreground = new SolidColorBrush(palette.Text)
				},
				(UIElement)new TextBlock
				{
					Text = "Ключ, sing-box JSON или ссылка на подписку",
					FontSize = 10.5 * preferences.TextScale,
					TextWrapping = TextWrapping.Wrap,
					Foreground = new SolidColorBrush(palette.Muted),
					Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
				}
			}
		};
		grid2.Children.Add(element);
		Button button = new Button
		{
			Content = "×",
			Width = 48.0,
			Height = 48.0,
			Background = Brushes.Transparent,
			Foreground = new SolidColorBrush(palette.Muted),
			BorderThickness = new Thickness(0.0),
			FontSize = 20.0,
			Cursor = Cursors.Hand
		};
		RoutedEventHandler value = delegate
		{
			base.DialogResult = false;
		};
		button.Click += value;
		Grid.SetColumn(button, 1);
		grid2.Children.Add(button);
		grid2.MouseLeftButtonDown += delegate
		{
			DragMove();
		};
		grid.Children.Add(grid2);
		Border border = new Border
		{
			Background = new SolidColorBrush(palette.Input),
			BorderBrush = new SolidColorBrush(palette.Border),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(preferences.CornerRadius * 14.0 / 16.0),
			Padding = new Thickness(14.0)
		};
		input = new TextBox
		{
			Background = Brushes.Transparent,
			Foreground = new SolidColorBrush(palette.Text),
			CaretBrush = new SolidColorBrush(palette.AccentLight),
			BorderThickness = new Thickness(0.0),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 11.0 * preferences.TextScale,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = true,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			ToolTip = "vless://, vmess://, trojan://, ss://, hysteria2://, tuic:// или https://"
		};
		ScrollViewer.SetPanningMode(input, PanningMode.VerticalOnly);
		border.Child = input;
		Grid.SetRow(border, 2);
		grid.Children.Add(border);
		Grid grid3 = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(),
				new ColumnDefinition
				{
					Width = new GridLength(110.0)
				},
				new ColumnDefinition
				{
					Width = new GridLength(10.0)
				},
				new ColumnDefinition
				{
					Width = new GridLength(142.0)
				}
			}
		};
		TextBlock element2 = new TextBlock
		{
			Text = "Можно вставить несколько ключей — по одному в строке",
			FontSize = 9.5 * preferences.TextScale,
			TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(palette.Muted),
			VerticalAlignment = VerticalAlignment.Center
		};
		grid3.Children.Add(element2);
		Button button2 = new Button
		{
			Content = "Отмена",
			Height = 48.0,
			Background = new SolidColorBrush(palette.Surface2),
			Foreground = new SolidColorBrush(palette.Text),
			BorderBrush = new SolidColorBrush(palette.Border),
			BorderThickness = new Thickness(1.0),
			Cursor = Cursors.Hand
		};
		button2.Click += delegate
		{
			base.DialogResult = false;
		};
		Grid.SetColumn(button2, 1);
		button2.Template = VisualDesign.ButtonTemplate(preferences, 16);
		button2.FontSize = 12 * preferences.TextScale;
		grid3.Children.Add(button2);
		Button button3 = new Button
		{
			Content = "Добавить",
			Height = 48.0,
			Background = new SolidColorBrush(palette.Accent),
			Foreground = new SolidColorBrush(palette.AccentForeground),
			BorderThickness = new Thickness(0.0),
			FontWeight = FontWeights.SemiBold,
			Cursor = Cursors.Hand
		};
		button3.Click += delegate
		{
			if (!string.IsNullOrWhiteSpace(input.Text))
			{
				base.DialogResult = true;
			}
		};
		Grid.SetColumn(button3, 3);
		button3.Template = VisualDesign.ButtonTemplate(preferences, 16);
		button3.FontSize = 12 * preferences.TextScale;
		grid3.Children.Add(button3);
		Grid.SetRow(grid3, 4);
		grid.Children.Add(grid3);
		base.Content = grid;
		base.Loaded += delegate
		{
			input.Focus();
		};
	}
}

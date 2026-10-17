using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed class ConnectionControl : Grid
{
    public Button ActionButton { get; private set; }
    public TextBlock Label { get; private set; }
    public bool IsAnimating { get; private set; }
    public int RequestedFrameRate => preferences.AnimationFrameRate;
    private VisualPreferences preferences = new VisualPreferences();
    private readonly ProgressBar progress;
    public ConnectionControl()
    {
        Width = 220; Height = 72;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        Label = new TextBlock { TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        ActionButton = new Button { Content = Label, Margin = new Thickness(0,0,0,8), Padding = new Thickness(16,10,16,10), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch };
        Children.Add(ActionButton);
        progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 3, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0) };
        Grid.SetRow(progress,1);Children.Add(progress);
        Unloaded += delegate { progress.IsIndeterminate=false;IsAnimating=false; };
    }
    public void Apply(VisualPreferences visual, CoreStatus status, bool allowMotion)
    {
        preferences = visual.Clone();
        var palette = ThemePalette.Create(visual);
        Label.Text = status == CoreStatus.Connected ? "Отключить" : status == CoreStatus.Connecting ? "Отменить" : status == CoreStatus.Error ? "Повторить" : "Подключить";
        Label.FontSize = 14 * visual.TextScale;
        ActionButton.Background = new SolidColorBrush(palette.Accent);
        ActionButton.Foreground = new SolidColorBrush(palette.AccentForeground);
        ActionButton.BorderBrush = new SolidColorBrush(palette.Accent);
        ActionButton.Template = VisualDesign.ButtonTemplate(visual,4);
        AutomationProperties.SetName(ActionButton,Label.Text + " VPN");
        progress.Foreground = new SolidColorBrush(palette.Accent);
        progress.Background = new SolidColorBrush(palette.Surface2);
        progress.Visibility = status == CoreStatus.Connecting ? Visibility.Visible : Visibility.Collapsed;
        IsAnimating = status == CoreStatus.Connecting && allowMotion && !visual.ReduceMotion && (!visual.FollowSystemMotion || SystemParameters.ClientAreaAnimation);
        progress.IsIndeterminate = IsAnimating;progress.Value = 50;
    }
}

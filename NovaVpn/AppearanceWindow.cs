using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

// Theme selection lives in the main sidebar. This dialog owns accessibility only.
public sealed class AppearanceWindow : Window
{
    private readonly VisualPreferences working;
    private readonly ThemePalette palette;
    private ScrollViewer scroll;
    public VisualPreferences Result => working;
    public AppearanceWindow(VisualPreferences preferences) : this(preferences, true) { }
    public AppearanceWindow(VisualPreferences preferences, bool allowAnimations)
    {
        working = (preferences ?? new VisualPreferences()).Clone();
        working.MigrateWindowsDesign();
        palette = ThemePalette.Create(working);
        Title = "Интерфейс и доступность";
        Width = Math.Min(600, SystemParameters.WorkArea.Width);
        Height = Math.Min(640, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(400, Width); MinHeight = Math.Min(360, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = VisualDesign.InterfaceFont(working);
        Background = new SolidColorBrush(palette.Bg);
        Foreground = new SolidColorBrush(palette.Text);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = Title, FontSize = 24 * working.TextScale, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,20) });
        var body = new StackPanel();
        var label = new TextBlock { Text = "Размер текста · " + Math.Round(working.TextScale * 100) + "%", FontSize = 14 * working.TextScale, Margin = new Thickness(0,0,0,8) };
        body.Children.Add(label);
        var slider = new Slider { Minimum = 100, Maximum = 150, Value = Math.Max(100, working.TextScale * 100), TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0,8,0,24) };
        System.Windows.Automation.AutomationProperties.SetName(slider, "Размер текста в процентах");
        slider.ValueChanged += delegate { working.TextScale = slider.Value / 100; label.Text = "Размер текста · " + Math.Round(slider.Value) + "%"; };
        body.Children.Add(slider);
        body.Children.Add(Check("Уменьшить движение", working.ReduceMotion, value => working.ReduceMotion = value));
        body.Children.Add(Check("Учитывать отключение анимаций в Windows", working.FollowSystemMotion, value => working.FollowSystemMotion = value));
        body.Children.Add(Check("Усилить контраст", working.HighContrast, value => working.HighContrast = value));
        body.Children.Add(Check("Показывать график скорости", working.ShowLiveGraph, value => working.ShowLiveGraph = value));
        body.Children.Add(Check("Показывать схему соединения", working.ShowConnectionMap, value => working.ShowConnectionMap = value));
        scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScrollBehavior.Attach(scroll, () => allowAnimations && !working.ReduceMotion &&
            (!working.FollowSystemMotion || SystemParameters.ClientAreaAnimation));
        Grid.SetRow(scroll,1); root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        var cancel = Action("Отмена", false); cancel.IsCancel = true; cancel.Click += delegate { DialogResult = false; };
        var save = Action("Сохранить", true); save.IsDefault = true; save.Click += delegate { DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetRow(actions,2); root.Children.Add(actions);
        Content = root;
    }
    private CheckBox Check(string text, bool value, Action<bool> change)
    {
        var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, FontSize = 14 * working.TextScale, Foreground = new SolidColorBrush(palette.Text), Margin = new Thickness(0,8,8,16) };
        box.Checked += delegate { change(true); }; box.Unchecked += delegate { change(false); };
        return box;
    }
    private Button Action(string text, bool primary) => new Button {
        Content = text, FontSize = 13 * working.TextScale, MinWidth = 100, MinHeight = 34, Padding = new Thickness(14,6,14,6), Margin = new Thickness(8,0,0,0),
        Background = new SolidColorBrush(primary ? palette.Accent : palette.Surface),
        Foreground = new SolidColorBrush(primary ? palette.AccentForeground : palette.Text),
        BorderBrush = new SolidColorBrush(palette.Border), BorderThickness = new Thickness(1),
        Template = VisualDesign.ButtonTemplate(working,4)
    };
    public void ScrollForSnapshot(double offset) { UpdateLayout(); scroll.ScrollToVerticalOffset(Math.Max(0,offset)); UpdateLayout(); }
}

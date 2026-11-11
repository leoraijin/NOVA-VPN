using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NovaVpn;

public sealed class ImportPreviewRow
{
    public bool Selected { get; set; } = true;
    public VpnProfile Profile { get; set; }
    public string Name => Profile.Name;
    public string Endpoint => Profile.Endpoint;
    public string Details => Profile.Protocol == "sing-box" ? "Полный конфиг: проверка ядром перед подключением" :
        Profile.ProtocolLabel + " · " + (Profile.Transport ?? "TCP") + " · " + (Profile.Security ?? "");
}

public sealed class ImportPreviewWindow : Window
{
    private readonly List<ImportPreviewRow> rows;
    private readonly ImportResult original;
    public ImportResult Accepted { get; private set; }
    public bool ReviewRouting { get; private set; }

    public ImportPreviewWindow(ImportResult imported, VisualPreferences visual, string changeSummary = null)
    {
        original = imported ?? throw new ArgumentNullException(nameof(imported));
        rows = imported.Profiles.Select(p => new ImportPreviewRow { Profile = p }).ToList();
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = imported.IsSubscription ? "Изменения подписки" : "Предварительный просмотр импорта";
        Width = Math.Min(860, SystemParameters.WorkArea.Width); Height = Math.Min(620, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(560, Width); MinHeight = Math.Min(420, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text);
        FontSize = 13 * (visual?.TextScale ?? 1); FontFamily = VisualDesign.InterfaceFont(visual ?? new VisualPreferences());
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "Профилей: " + rows.Count + (string.IsNullOrEmpty(changeSummary) ? "" : "\n" + changeSummary),
            FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(heading);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            Background = new SolidColorBrush(palette.Input), Foreground = new SolidColorBrush(palette.Text),
            RowBackground = new SolidColorBrush(palette.Input), AlternatingRowBackground = new SolidColorBrush(palette.Surface),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            BorderBrush = new SolidColorBrush(palette.Border), EnableRowVirtualization = true, EnableColumnVirtualization = true };
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Surface2)));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(palette.Text)));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        grid.ColumnHeaderStyle = headerStyle;
        if (!imported.IsSubscription) grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Выбрать", Binding = new Binding("Selected"), Width = 80 * (visual?.TextScale ?? 1) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Название", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Сервер", Binding = new Binding("Endpoint"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Параметры", Binding = new Binding("Details"), IsReadOnly = true, Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6)));
            if (column.Binding is Binding binding) style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(binding.Path.Path)));
            column.ElementStyle = style;
        }
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        string warnings = string.Join("\n", imported.Warnings);
        foreach (var routing in imported.RoutingPreviews)
        {
            warnings += "\n" + routing.Summary + "\n" + string.Join("\n", routing.Rules.Take(50).Select(rule => rule.Summary));
            if (routing.Rules.Count > 50) warnings += "\nЕщё правил: " + (routing.Rules.Count - 50);
        }
        if (imported.Profiles.Any(p => p.Security == "insecure")) warnings += "\nВ части профилей отключена проверка TLS-сертификата. Проверьте источник этих настроек.";
        if (imported.Profiles.Any(p => p.Protocol == "sing-box")) warnings += "\nПолный конфиг сохраняется целиком; его входы и правила будут проверены перед запуском.";
        var notice = new TextBox { Name = "ImportWarnings", Text = string.IsNullOrWhiteSpace(warnings) ? "Ошибок разбора не найдено. Работоспособность проверяется отдельно через выбранное ядро." : warnings.Trim(),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(palette.Surface), Foreground = new SolidColorBrush(palette.Text),
            BorderThickness = new Thickness(0), Margin = new Thickness(0, 12, 0, 12) };
        var notices = new StackPanel(); notices.Children.Add(notice);
        var reviewRouting = new CheckBox { Name = "ReviewRouting", Content = new TextBlock { Text = "Перенести правила маршрутизации · сопоставить выходы на следующем шаге", TextWrapping = TextWrapping.Wrap }, Foreground = Foreground, IsChecked = false,
            Visibility = !imported.IsSubscription && imported.RoutingPreviews.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
            IsEnabled = imported.RoutingPreviews.Count == 1 && imported.RoutingPreviews[0].Rules.Count > 0 && !imported.RoutingPreviews[0].RequiresDnsMapping && imported.RoutingPreviews[0].ContainerIssue == "" && imported.RoutingPreviews[0].Rules.All(r => r.ConditionSupported), Margin = new Thickness(0, 0, 0, 8) }; notices.Children.Add(reviewRouting);
        Grid.SetRow(notices, 2); root.Children.Add(notices);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += delegate { DialogResult = false; };
        var apply = new Button { Name = "ApplyImport", Content = imported.IsSubscription ? "Применить изменения" : "Добавить выбранные", Padding = new Thickness(18, 9, 18, 9) };
        apply.Click += delegate
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = rows.Where(r => r.Selected).Select(r => r.Profile).ToList();
            if (selected.Count == 0) { heading.Text = "Выберите хотя бы один профиль."; return; }
            ReviewRouting = reviewRouting.IsChecked == true;
            Accepted = new ImportResult(selected, original.SubscriptionUrl) { Quota = original.Quota };
            Accepted.Warnings.AddRange(original.Warnings); Accepted.RoutingPreviews.AddRange(original.RoutingPreviews); DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NovaVpn;

public sealed class CollectionProfileRow
{
    public string Id { get; set; }
    public bool Selected { get; set; }
    public string Name { get; set; }
    public string Endpoint { get; set; }
    public string Description => Name + "\n" + Endpoint;
    public string Protocol { get; set; }
    public string Country { get; set; }
    public int Order { get; set; }
}

public sealed class ProfileCollectionWindow : Window
{
    public ProfileCollection Accepted { get; private set; }
    public Dictionary<string, string> AcceptedCountries { get; private set; }
    public bool Deleted { get; private set; }

    public ProfileCollectionWindow(ProfileCollection collection, IEnumerable<VpnProfile> profiles, VisualPreferences visual)
    {
        bool existing = collection != null;
        collection = collection ?? new ProfileCollection();
        var ids = collection.ProfileIds ?? new List<string>();
        var rows = profiles.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id).Select(g => g.First()).Select((p, i) => new CollectionProfileRow {
            Id = p.Id, Selected = ids.Contains(p.Id), Name = p.Name, Endpoint = p.Endpoint, Protocol = p.ProtocolLabel,
            Country = p.DeclaredCountryCode ?? "", Order = ids.Contains(p.Id) ? ids.IndexOf(p.Id) + 1 : ids.Count + i + 1
        }).OrderBy(r => r.Order).ToList();
        foreach (string id in ids.Where(id => !rows.Any(r => r.Id == id)).Distinct())
            rows.Add(new CollectionProfileRow { Id = id, Selected = true, Name = "Профиль временно отсутствует\n" + id, Protocol = "—", Country = "", Order = ids.IndexOf(id) + 1 });
        rows = rows.OrderBy(r => r.Order).ToList();
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Коллекция профилей"; Width = Math.Min(900, SystemParameters.WorkArea.Width); Height = Math.Min(730, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(600, Width); MinHeight = Math.Min(580, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 13 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var fields = new StackPanel();
        var limits = new StackPanel();
        StackPanel fieldParent = fields;
        Func<string, string, TextBox> field = (label, text) => {
            fieldParent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 3), TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = text, Padding = new Thickness(7), Background = new SolidColorBrush(palette.Input), Foreground = Foreground, MaxLength = 2000 };
            fieldParent.Children.Add(box); return box;
        };
        var name = field("Название: например, работа, игры или видео", collection.Name);
        fieldParent = limits;
        var countries = field("Разрешённые страны: DE, NL, GB; пусто — любые", string.Join(", ", collection.CountryCodes ?? new List<string>()));
        var protocols = field("Протоколы через запятую: vless, trojan…; пусто — любые", string.Join(", ", collection.Protocols ?? new List<string>()));
        protocols.ToolTip = string.Join(", ", ProfileCollectionService.SupportedProtocols);
        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        var mode = new ComboBox { Width = 410, Margin = new Thickness(0, 0, 14, 4) };
        mode.Items.Add(new ComboBoxItem { Content = "Самый быстрый проверенный", Tag = "VerifiedFastest" });
        mode.Items.Add(new ComboBoxItem { Content = "Первый проверенный по порядку", Tag = "Ordered" });
        mode.SelectedIndex = collection.SelectionMode == "Ordered" ? 1 : 0;
        // Imported invalid modes must not be silently repaired just by opening/saving an unrelated field.
        if (collection.SelectionMode != null && collection.SelectionMode != "Ordered" && collection.SelectionMode != "VerifiedFastest") mode.SelectedIndex = -1;
        options.Children.Add(mode);
        var favorites = new CheckBox { Content = "Только избранные", IsChecked = collection.FavoritesOnly, VerticalAlignment = VerticalAlignment.Center, Foreground = Foreground };
        options.Children.Add(favorites); limits.Children.Add(options);
        string countrySummary = collection.CountryCodes?.Count > 0 ? string.Join(", ", collection.CountryCodes) : "любые";
        string protocolSummary = collection.Protocols?.Count > 0 ? string.Join(", ", collection.Protocols) : "любые";
        var summary = new TextBlock { Text = "Ограничения: страны — " + countrySummary + "; протоколы — " + protocolSummary +
            "; выбор — " + (collection.SelectionMode == "Ordered" ? "по порядку" : "по скорости") + (collection.FavoritesOnly ? "; только избранные" : ""), TextWrapping = TextWrapping.Wrap };
        var expander = new Expander { Header = summary, Content = limits, IsExpanded = !existing, Margin = new Thickness(0, 10, 0, 10), Foreground = Foreground, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        fields.Children.Add(expander); root.Children.Add(fields);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            Background = new SolidColorBrush(palette.Input), Foreground = Foreground, RowBackground = new SolidColorBrush(palette.Input), AlternatingRowBackground = new SolidColorBrush(palette.Surface),
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, EnableRowVirtualization = true };
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Surface2))); header.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6))); grid.ColumnHeaderStyle = header;
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "✓", Binding = new Binding("Selected"), Width = 36 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Профиль", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Протокол", Binding = new Binding("Protocol"), IsReadOnly = true, Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Страна", Binding = new Binding("Country"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Порядок", Binding = new Binding("Order"), Width = 110 });
        foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6)));
            if (column.Binding is Binding binding) style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(binding.Path.Path == "Name" ? "Description" : binding.Path.Path)));
            column.ElementStyle = style;
        }
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var notice = new TextBox { Text = "Отметьте состав коллекции. Страна — ваша отметка двухбуквенным кодом, а не проверенная геолокация. Меньшее число в столбце «Порядок» означает более высокий приоритет. Автовыбор использует только свежие HTTPS-проверки.",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(palette.Surface), Foreground = Foreground,
            BorderThickness = new Thickness(0), Margin = new Thickness(0, 12, 0, 12), MaxHeight = 90 };
        Grid.SetRow(notice, 2); root.Children.Add(notice);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Action<string, Action, bool> button = (label, click, cancel) => {
            var item = new Button { Content = label, IsCancel = cancel, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 8, 4) };
            item.Click += delegate { click(); }; actions.Children.Add(item);
        };
        if (existing) button("Удалить коллекцию", () => { Deleted = true; DialogResult = true; }, false);
        button("Отмена", () => DialogResult = false, true);
        button("Сохранить", () => {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) { notice.Text = "Исправьте значение порядка: требуется целое число."; return; }
            var selected = rows.Where(r => r.Selected).OrderBy(r => r.Order).ToList();
            var result = new ProfileCollection { Id = collection.Id, Name = name.Text.Trim(), ProfileIds = selected.Select(r => r.Id).Distinct().ToList(),
                CountryCodes = Split(countries.Text).Select(c => ProfileCollectionService.Country(c) == "" ? c.ToUpperInvariant() : ProfileCollectionService.Country(c)).ToList(),
                Protocols = Split(protocols.Text).Select(p => p.ToLowerInvariant()).ToList(), SelectionMode = (mode.SelectedItem as ComboBoxItem)?.Tag as string ?? "", FavoritesOnly = favorites.IsChecked == true };
            string error = ProfileCollectionService.Validate(result);
            if (error == null && rows.Any(r => !string.IsNullOrWhiteSpace(r.Country) && ProfileCollectionService.Country(r.Country) == "")) error = "Исправьте код страны профиля или оставьте поле пустым.";
            if (error != null) { notice.Text = error; return; }
            Accepted = result; AcceptedCountries = rows.ToDictionary(r => r.Id, r => ProfileCollectionService.Country(r.Country)); DialogResult = true;
        }, false);
        Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
    }

    private static IEnumerable<string> Split(string text) => (text ?? "").Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
}

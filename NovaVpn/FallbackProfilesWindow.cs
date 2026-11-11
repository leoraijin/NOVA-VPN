using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed class FallbackProfilesWindow : Window
{
    public List<string> Accepted { get; private set; }
    public FallbackProfilesWindow(VpnProfile root, IEnumerable<VpnProfile> profiles, VisualPreferences visual)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        var all = profiles.Where(p => p != null).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var ids = (root.FallbackProfileIds ?? new List<string>()).Where(id => id != root.Id && !string.IsNullOrWhiteSpace(id)).Distinct().Take(ConfiguredFallbackService.MaxAlternatives).ToList();
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Резервные подключения"; Width = Math.Min(760, SystemParameters.WorkArea.Width); Height = Math.Min(560, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(520, Width); MinHeight = Math.Min(440, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 13 * (visual?.TextScale ?? 1);
        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var description = new TextBlock { Text = "Основной: " + root.Name + "\nПри ошибке NOVA по порядку проверит выбранные ниже профили. Добавляйте только выданные сервером конфигурации: транспорт и адрес не подбираются автоматически. При отключении перебор прекращается.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(description);
        var list = new ListBox { Background = new SolidColorBrush(palette.Input), Foreground = Foreground, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        Action refresh = () => {
            int selected = list.SelectedIndex; list.Items.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                string text = all.TryGetValue(ids[i], out var p) ? p.Name + " · " + p.ProtocolLabel + " · " + (p.Transport ?? "TCP") + " · " + p.Endpoint : "Недоступный профиль: " + ids[i];
                list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = (i + 1) + ". " + text, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(8) });
            }
            list.SelectedIndex = Math.Min(Math.Max(0, selected), ids.Count - 1);
        };
        Grid.SetRow(list, 1); panel.Children.Add(list);
        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        var available = new ComboBox { Width = 270, Margin = new Thickness(0, 0, 8, 6) };
        foreach (var p in all.Values.Where(p => p.Id != root.Id && !p.RetiredFromSubscription && p.Protocol != "sing-box").OrderBy(p => p.Name))
            available.Items.Add(new ComboBoxItem { Content = p.Name + " · " + p.ProtocolLabel, ToolTip = p.Name + " · " + p.ProtocolLabel + " · " + p.Endpoint, Tag = p.Id });
        available.SelectionChanged += delegate { available.ToolTip = (available.SelectedItem as ComboBoxItem)?.ToolTip; };
        available.SelectedIndex = 0; controls.Children.Add(available);
        Action<string, Action> button = (label, click) => { var b = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) }; b.Click += delegate { click(); }; controls.Children.Add(b); };
        button("Добавить", () => { if (available.SelectedItem is ComboBoxItem item && !ids.Contains((string)item.Tag) && ids.Count < ConfiguredFallbackService.MaxAlternatives) { ids.Add((string)item.Tag); refresh(); } });
        button("Выше", () => { int i = list.SelectedIndex; if (i > 0) { string id = ids[i]; ids.RemoveAt(i); ids.Insert(i - 1, id); refresh(); list.SelectedIndex = i - 1; } });
        button("Ниже", () => { int i = list.SelectedIndex; if (i >= 0 && i < ids.Count - 1) { string id = ids[i]; ids.RemoveAt(i); ids.Insert(i + 1, id); refresh(); list.SelectedIndex = i + 1; } });
        button("Удалить", () => { int i = list.SelectedIndex; if (i >= 0) { ids.RemoveAt(i); refresh(); } });
        Grid.SetRow(controls, 2); panel.Children.Add(controls);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += delegate { DialogResult = false; };
        var save = new Button { Content = "Сохранить порядок", Padding = new Thickness(18, 9, 18, 9) };
        save.Click += delegate { Accepted = ids.ToList(); DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetRow(actions, 3); panel.Children.Add(actions);
        refresh(); Content = panel;
    }
}

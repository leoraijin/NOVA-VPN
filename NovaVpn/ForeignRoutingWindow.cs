using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed class ForeignRoutingWindow : Window
{
    public ForeignRoutingPolicy Accepted { get; private set; }
    public ForeignRoutingWindow(ForeignRoutingPreview source, VpnProfile profile, RoutingSettings baseline, VisualPreferences visual, bool preview)
    {
        if (source == null || profile == null) throw new ArgumentNullException();
        var frozen = RoutingOverlayService.Clone(baseline); frozen.MigratedPolicy = null;
        string prepared = ConfigBuilder.Build(profile, frozen);
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Перенос правил маршрутизации"; Width = Math.Min(780, SystemParameters.WorkArea.Width); Height = Math.Min(720, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(540, Width); MinHeight = Math.Min(460, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = 13 * (visual?.TextScale ?? 1); Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text);
        var root = new Grid { Margin = new Thickness(18) }; root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var fields = new StackPanel();
        fields.Children.Add(new TextBlock { Text = "Правила " + source.Format, FontSize = FontSize * 1.4, FontWeight = FontWeights.SemiBold });
        fields.Children.Add(new TextBlock { Text = "Порядок правил сохранится. Они заменят обычные правила NOVA; прежние настройки можно будет вернуть в разделе «Маршрутизация». DNS остаётся настроенным в NOVA. «VPN» означает выбранный в NOVA сервер. Соединение сейчас не запускается.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var mappings = new Dictionary<int, ComboBox>();
        ComboBox Selector(string name)
        {
            var box = new ComboBox { Name = name, Padding = new Thickness(6), MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch,
                Foreground = Foreground, Background = new SolidColorBrush(palette.Input), Margin = new Thickness(0, 4, 0, 8), Style = Application.Current?.Resources["NovaSelectorStyle"] as Style };
            foreach (var option in new[] { new[] { "Выберите выход", "" }, new[] { "VPN · выбранный сервер NOVA", "proxy" }, new[] { "Напрямую", "direct" }, new[] { "Блокировать", "block" } }) box.Items.Add(new ComboBoxItem { Content = option[0], Tag = option[1] });
            box.SelectedIndex = 0; return box;
        }
        foreach (var row in source.Rules)
        {
            fields.Children.Add(new TextBlock { Text = row.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            fields.Children.Add(new TextBlock{Name="MigrationCondition"+row.Position,Text=row.ConditionSummary,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,6)});
            if (!row.PolicyResolved) { var box = Selector("RuleTarget" + row.Position); mappings.Add(row.Position, box); fields.Children.Add(box); }
        }
        fields.Children.Add(new TextBlock { Text = "Если ни одно правило не совпало", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var fallback = Selector("MigrationFallback"); fields.Children.Add(fallback);
        var message = new TextBlock { Name = "MigrationMessage", Text = preview ? "Предпросмотр: сохранение отключено." : source.RequiresDnsMapping || source.ContainerIssue != "" || source.Rules.Any(r => !r.ConditionSupported) ? "Не все настройки источника поддерживаются. Частичный перенос недоступен; серверы можно импортировать без правил." : "Выберите выходы и проверьте порядок. Перед сохранением конфигурацию проверит ядро.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) }; fields.Children.Add(message);
        root.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += delegate { DialogResult = false; }; actions.Children.Add(cancel);
        var apply = new Button { Name = "ApplyMigration", Content = "Перенести правила", IsEnabled = !preview && source.Rules.Count > 0 && !source.RequiresDnsMapping && source.ContainerIssue == "" && source.Rules.All(r => r.ConditionSupported), Padding = new Thickness(14, 8, 14, 8) };
        apply.Click += delegate {
            if (preview || !apply.IsEnabled) return;
            try
            {
                var chosen = mappings.ToDictionary(p => p.Key, p => (p.Value.SelectedItem as ComboBoxItem)?.Tag as string);
                var plan = ForeignRoutingMigrationPlan.Prepare(prepared, source, chosen, (fallback.SelectedItem as ComboBoxItem)?.Tag as string);
                Accepted = plan.ToPolicy(); DialogResult = true;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is System.IO.InvalidDataException) { message.Text = error.Message; message.Foreground = new SolidColorBrush(palette.Warning); }
        }; actions.Children.Add(apply); Grid.SetRow(actions, 1); root.Children.Add(actions); Content = root;
    }
}

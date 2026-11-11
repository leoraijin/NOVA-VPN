using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace NovaVpn;

public sealed class ServiceMatrixRow
{
    public string Id { get; set; }
    public string Name { get; set; }
    public ObservableCollection<string> Cells { get; } = new ObservableCollection<string>();
    public List<ServiceCheckResult> Results { get; } = new List<ServiceCheckResult>();
}

public sealed class ServiceMatrixWindow : Window
{
    private readonly ServiceMatrixRunner runner = new ServiceMatrixRunner();
    private readonly Func<List<VpnProfile>> currentProfiles;
    private readonly string corePath, runtime;
    private readonly bool preview;
    private readonly CancellationToken lifetime;
    private readonly List<CheckBox> choices = new List<CheckBox>();
    private readonly TextBox addresses;
    private readonly TextBlock status;
    private readonly Button start, stop, use;
    private readonly DataGrid matrix;
    private readonly Expander options;
    private readonly DispatcherTimer timer;
    private readonly Style cellTextStyle;
    private bool busy, stopping, closePending, allowClose, canceled;
    private long generation;
    private ServiceCheckTarget[] targets = new ServiceCheckTarget[0];
    private List<ServiceMatrixRow> rows = new List<ServiceMatrixRow>();
    public string ChosenProfileId { get; private set; }

    public ServiceMatrixWindow(List<VpnProfile> profiles, Func<List<VpnProfile>> currentProfiles, string corePath, string runtime, VisualPreferences visual, bool preview, CancellationToken lifetime)
    {
        this.currentProfiles = currentProfiles; this.corePath = corePath; this.runtime = runtime; this.preview = preview; this.lifetime = lifetime;
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Доступность сервисов"; Width = Math.Min(1100, SystemParameters.WorkArea.Width); Height = Math.Min(780, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(600, Width); MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 12 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(18) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Профиль × HTTPS-адрес", FontSize = FontSize * 1.5, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Проверка не переключает основной VPN. HTTP-код подтверждает ответ адреса; вход в аккаунт, просмотр видео и права подписки проверяются в самом сервисе. Результаты устаревают через 3 минуты или при смене профиля, сети и ядра.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var fields = new Grid(); fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; left.Children.Add(new TextBlock { Text = "До 32 профилей из активной коллекции", TextWrapping = TextWrapping.Wrap });
        var profileList = new StackPanel();
        foreach (var profile in profiles.Where(p => p != null).GroupBy(p => p.Id).Select(g => g.First()))
        {
            var box = new CheckBox { Content = new TextBlock { Text = profile.Name + " · " + profile.ProtocolLabel, TextWrapping = TextWrapping.Wrap, Foreground = Foreground }, Foreground = Foreground, Tag = profile.Id, IsChecked = choices.Count < 8, Margin = new Thickness(0, 5, 4, 5) };
            choices.Add(box); profileList.Children.Add(box);
        }
        left.Children.Add(new ScrollViewer { Content = profileList, Height = 126, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); fields.Children.Add(left);
        var right = new StackPanel(); right.Children.Add(new TextBlock { Text = "До 8 адресов: название | HTTPS-URL, по одному на строку", TextWrapping = TextWrapping.Wrap });
        addresses = new TextBox { Text = "YouTube | https://www.youtube.com/\nDiscord | https://discord.com/\nChatGPT | https://chatgpt.com/", AcceptsReturn = true, Height = 126, MaxLength = 18000, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(palette.Input), Foreground = Foreground, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6) };
        right.Children.Add(addresses); Grid.SetColumn(right, 1); fields.Children.Add(right);
        options = new Expander { Header = "Профили и адреса проверки", Content = fields, IsExpanded = true, Margin = new Thickness(0, 0, 0, 10), Foreground = Foreground }; header.Children.Add(options); root.Children.Add(header);
        matrix = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, CanUserReorderColumns = false, FrozenColumnCount = 1, HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = new SolidColorBrush(palette.Input), Foreground = Foreground, RowBackground = new SolidColorBrush(palette.Input), AlternatingRowBackground = new SolidColorBrush(palette.Surface), EnableRowVirtualization = true, MinColumnWidth = 150, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        cellTextStyle = new Style(typeof(TextBlock)); cellTextStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); cellTextStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(5, 7, 5, 7))); cellTextStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Foreground));
        ScrollViewer.SetHorizontalScrollBarVisibility(matrix, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(matrix, ScrollBarVisibility.Auto);
        var columnHeader = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)); columnHeader.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Surface))); columnHeader.Setters.Add(new Setter(Control.ForegroundProperty, Foreground)); columnHeader.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        matrix.ColumnHeaderStyle = columnHeader; Grid.SetRow(matrix, 1); root.Children.Add(matrix);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        status = new TextBlock { Text = preview ? "Предпросмотр — сетевые проверки отключены." : "Запросы отправляются только после нажатия «Проверить». Адреса и результаты этого окна не сохраняются.", TextWrapping = TextWrapping.Wrap }; footer.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Func<string, Button> button = label => { var b = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 6) }; actions.Children.Add(b); return b; };
        start = button("Проверить"); start.Click += async delegate { await StartAsync(); };
        stop = button("Остановить"); stop.Click += async delegate { await StopAsync(); };
        use = button("Использовать профиль"); use.Click += delegate { ChooseProfile(); };
        var close = button("Закрыть"); close.Click += delegate { Close(); }; footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        matrix.SelectionChanged += delegate { Controls(); };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; timer.Tick += delegate { RefreshResults(); }; timer.Start();
        Closing += async (sender, e) => {
            if (allowClose || !runner.IsRunning && !runner.HasActive) return;
            e.Cancel = true; if (closePending) return; closePending = true; Controls();
            try { await runner.StopAsync(); allowClose = true; Close(); } catch { status.Text = "Не все процессы проверки остановились. Повторите остановку."; } finally { closePending = false; Controls(); }
        };
        Closed += delegate { timer.Stop(); runner.Dispose(); }; Controls();
    }
    private void Controls()
    {
        start.IsEnabled = !preview && !busy && !stopping && !closePending && !runner.HasActive && choices.Count > 0;
        stop.IsEnabled = !stopping && !closePending && (busy || runner.HasActive);
        options.IsEnabled = !busy && !stopping && !closePending;
        use.IsEnabled = !preview && !busy && !stopping && !closePending && !runner.HasActive && matrix.SelectedItem is ServiceMatrixRow;
    }
    private void BuildMatrix(IEnumerable<VpnProfile> selected, IReadOnlyList<ServiceCheckTarget> checks)
    {
        targets = checks.ToArray(); rows = selected.Select(p => new ServiceMatrixRow { Id = p.Id, Name = p.Name }).ToList();
        matrix.Columns.Clear(); matrix.Columns.Add(new DataGridTextColumn { Header = "Профиль", Binding = new Binding("Name"), Width = 210, ElementStyle = cellTextStyle });
        for (int i = 0; i < targets.Length; i++) matrix.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = targets[i].Name, TextWrapping = TextWrapping.Wrap, ToolTip = targets[i].Address.GetLeftPart(UriPartial.Path) }, Binding = new Binding("Cells[" + i + "]"), Width = 170, ElementStyle = cellTextStyle });
        foreach (var row in rows) foreach (var target in targets) row.Cells.Add("Ожидает проверки");
        matrix.ItemsSource = rows;
    }
    private async Task StartAsync()
    {
        if (preview || busy || stopping || runner.HasActive || closePending) return;
        try
        {
            var ids = choices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray(); var current = currentProfiles();
            var selected = ids.Select(id => current.SingleOrDefault(p => p.Id == id)).ToArray();
            if (ids.Length < 1 || ids.Length > ServiceMatrixRunner.MaximumProfiles || selected.Any(p => p == null || p.RetiredFromSubscription)) throw new InvalidOperationException("Выберите от 1 до 32 профилей, доступных в текущей коллекции.");
            var checks = ServiceCheckTarget.Parse(addresses.Text); BuildMatrix(selected, checks); canceled = false; busy = true; options.IsExpanded = false; Controls();
            status.Text = "Проверяем HTTPS через выбранные профили…";
            long round = ++generation;
            await runner.RunAsync(selected, checks, corePath, runtime, result => Dispatcher.BeginInvoke((Action)(() => { if (round == generation) Accept(result); })), lifetime);
            status.Text = "Проверка завершена. Коды 3xx, 401, 403 и 429 требуют отдельной проверки в сервисе. Ответы сохраняются в окне до его закрытия.";
        }
        catch (OperationCanceledException) { canceled = true; status.Text = "Проверка отменена. Уже полученные результаты оставлены в таблице."; }
        catch (ArgumentException error) { status.Text = error.Message; }
        catch (InvalidOperationException error) { status.Text = error.Message; }
        catch { status.Text = "Проверка не завершена. Остановите оставшиеся процессы и повторите попытку."; }
        finally { busy = false; RefreshResults(); Controls(); }
    }
    private void Accept(ServiceCheckResult result)
    {
        if (!IsLoaded) return;
        var row = rows.FirstOrDefault(r => r.Id == result.ProfileId); if (row == null || !targets.Any(t => t.Address == result.Target.Address)) return;
        row.Results.RemoveAll(r => r.Target.Address == result.Target.Address); row.Results.Add(result); RefreshResults();
        if (busy) status.Text = "Получено " + rows.Sum(r => r.Results.Count) + " из " + rows.Count * targets.Length + " ответов; остальные адреса проверяются…";
    }
    private void RefreshResults()
    {
        var current = currentProfiles(); string network = ProfileHealthEvidence.NetworkKey(), core = ProfileHealthEvidence.CoreKey(corePath);
        foreach (var row in rows)
            for (int i = 0; i < targets.Length; i++)
            {
                var result = row.Results.FirstOrDefault(r => r.Target.Address == targets[i].Address); var profile = current.FirstOrDefault(p => p.Id == row.Id);
                row.Cells[i] = result == null ? canceled ? "Отменено" : busy ? "Ожидает проверки" : "Не проверено" : result.Kind == ServiceCheckKind.Unsupported ? result.Message :
                    !result.IsCurrent(profile, DateTime.UtcNow, network, core) ? "Устарело · " + result.Label : result.Label;
            }
    }
    private async Task StopAsync()
    {
        if (stopping) return; stopping = true; canceled = true; Controls();
        try { await runner.StopAsync(); status.Text = "Проверка остановлена. Полученные результаты оставлены в таблице."; }
        catch { status.Text = "Остановка не подтверждена. Повторите попытку."; }
        finally { stopping = false; RefreshResults(); Controls(); }
    }
    private void ChooseProfile()
    {
        var row = matrix.SelectedItem as ServiceMatrixRow; if (row == null || preview || busy || runner.HasActive) return;
        var profile = currentProfiles().FirstOrDefault(p => p.Id == row.Id); string network = ProfileHealthEvidence.NetworkKey(), core = ProfileHealthEvidence.CoreKey(corePath);
        if (!row.Results.Any(r => r.Kind == ServiceCheckKind.Response && r.IsCurrent(profile, DateTime.UtcNow, network, core))) { status.Text = "У профиля нет свежего HTTPS-ответа 2xx. Повторите проверку или выберите профиль в общем списке серверов."; return; }
        ChosenProfileId = row.Id; DialogResult = true;
    }
}

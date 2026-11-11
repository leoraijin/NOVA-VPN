using System;
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

public sealed class CoreBenchmarkWindow : Window
{
    private readonly CoreBenchmarkRunner runner = new CoreBenchmarkRunner();
    private readonly ObservableCollection<CoreBenchmarkResult> results = new ObservableCollection<CoreBenchmarkResult>();
    private readonly Func<VpnProfile> currentProfile;
    private readonly Func<bool> mayRun;
    private readonly string singbox, reality, runtime;
    private readonly bool preview;
    private readonly CancellationToken lifetime;
    private readonly TextBox address;
    private readonly CheckBox eco;
    private readonly TextBlock status;
    private readonly Button start, stop;
    private readonly DispatcherTimer timer;
    private bool busy, stopping, closing, allowClose;
    private string activeProfileStamp;
    private int contextAllowed = 1;

    public CoreBenchmarkWindow(Func<VpnProfile> currentProfile, Func<bool> mayRun, string singbox, string reality, string runtime, VisualPreferences visual, bool preview, CancellationToken lifetime)
    {
        this.currentProfile = currentProfile; this.mayRun = mayRun; this.singbox = singbox; this.reality = reality; this.runtime = runtime; this.preview = preview; this.lifetime = lifetime;
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Сравнение VPN-ядер"; Width = Math.Min(1100, SystemParameters.WorkArea.Width); Height = Math.Min(760, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(600, Width); MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 12 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(18) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Одинаковые запросы через два ядра", FontSize = FontSize * 1.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = "Профиль: " + (currentProfile()?.Name ?? "не выбран") + ". Нужен общий профиль VLESS или Trojan с TLS / Reality. Сначала отключите основной VPN и игровой режим.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        header.Children.Add(new TextBlock { Text = "HTTPS-адрес для серии запросов", TextWrapping = TextWrapping.Wrap });
        address = new TextBox { Text = "https://www.gstatic.com/generate_204", Background = new SolidColorBrush(palette.Input), Foreground = Foreground, CaretBrush = Foreground, Padding = new Thickness(7), Margin = new Thickness(0, 5, 0, 8), MaxLength = 2048 };
        header.Children.Add(address);
        eco = new CheckBox { Content = new TextBlock { Text = "Повторить оба прогона с EcoQoS", Foreground = Foreground, TextWrapping = TextWrapping.Wrap }, Foreground = Foreground, IsChecked = false, Margin = new Thickness(0, 0, 0, 8) }; header.Children.Add(eco);
        var detail = new TextBlock { Text = "Каждый прогон: 1 запрос прогрева, затем 10 новых HTTPS-соединений с GET, без переходов и чтения тела ответа. До 22 запросов, с EcoQoS — до 44. Сначала sing-box и Reality с исходной политикой Windows, затем Reality и sing-box с EcoQoS. Каждый процесс останавливается перед следующим.\n\nЗадержка — медиана ответов 2xx, включая соединение через прокси, TLS и заголовки HTTPS. CPU измеряется за всю серию, включая ожидание сети и не менее 2 секунд наблюдения; память — максимум выборок раз в 0,5 секунды. Состояние сервера и сети может меняться. Это не измерение пропускной способности или энергопотребления; автоматического выбора победителя нет.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) };
        header.Children.Add(new Expander { Header = "Что именно сравнивается", Foreground = Foreground, Content = detail, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new ScrollViewer { Content = header, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var table = new DataGrid { ItemsSource = results, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            Background = new SolidColorBrush(palette.Input), Foreground = Foreground, RowBackground = new SolidColorBrush(palette.Input), AlternatingRowBackground = new SolidColorBrush(palette.Surface), HeadersVisibility = DataGridHeadersVisibility.Column, FrozenColumnCount = 1 };
        var text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); text.Setters.Add(new Setter(TextBlock.ForegroundProperty, Foreground)); text.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(6)));
        foreach (var column in new[] { new[] { "Ядро", "EngineLabel", "190" }, new[] { "Windows", "ModeLabel", "150" }, new[] { "Ответы 2xx", "RepliesLabel", "100" }, new[] { "Медиана", "LatencyLabel", "110" }, new[] { "Серия", "DurationLabel", "90" }, new[] { "CPU", "CpuLabel", "85" }, new[] { "Время CPU", "CpuTimeLabel", "110" }, new[] { "Память: рабочая / приватная", "MemoryLabel", "180" }, new[] { "Результат", "Message", "270" } })
            table.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = column[0], TextWrapping = TextWrapping.Wrap }, Binding = new Binding(column[1]), Width = int.Parse(column[2]), ElementStyle = text });
        var headers = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)); headers.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Surface))); headers.Setters.Add(new Setter(Control.ForegroundProperty, Foreground)); headers.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7))); table.ColumnHeaderStyle = headers;
        Grid.SetRow(table, 1); root.Children.Add(table);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        status = new TextBlock { Text = preview ? "Предпросмотр: запуск процессов отключён." : "Адрес получит запросы только после нажатия «Сравнить». Результаты хранятся до закрытия этого окна.", TextWrapping = TextWrapping.Wrap }; footer.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        start = Button("Сравнить", async () => await StartAsync()); stop = Button("Остановить", async () => await StopAsync());
        actions.Children.Add(start); actions.Children.Add(stop); actions.Children.Add(Button("Закрыть", () => { Close(); return Task.CompletedTask; })); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += async delegate {
            Refresh();
            if (busy && !stopping && !closing && (!mayRun() || Volatile.Read(ref contextAllowed) == 0))
            {
                await StopAsync();
                if (!runner.HasActive) status.Text = "Профиль, разрешение коллекции или состояние VPN изменились. Сравнение остановлено.";
            }
        }; timer.Start();
        Closing += async (sender, e) => {
            if (allowClose || !runner.IsRunning && !runner.HasActive) return;
            e.Cancel = true; if (closing) return; closing = true; Controls();
            try { await runner.StopAsync(); allowClose = true; Close(); } catch { status.Text = "Не все процессы остановились. Повторите остановку."; } finally { closing = false; Controls(); }
        };
        Closed += delegate { timer.Stop(); runner.Dispose(); }; Controls();
    }
    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 6) };
        button.Click += async delegate { await action(); }; return button;
    }
    private void Controls()
    {
        start.IsEnabled = !preview && !busy && !stopping && !closing && !runner.HasActive && currentProfile() != null && File.Exists(singbox) && File.Exists(reality) && mayRun();
        stop.IsEnabled = !preview && !stopping && !closing && (runner.IsRunning || runner.HasActive);
        address.IsEnabled = eco.IsEnabled = !preview && !busy && !runner.HasActive;
    }
    private void Refresh()
    {
        if (busy && (currentProfile() == null || ServiceMatrixRunner.ProfileStamp(currentProfile()) != activeProfileStamp))
            Interlocked.Exchange(ref contextAllowed, 0);
        Controls();
        if (!busy && results.Any(r => !r.IsCurrent(currentProfile(), ProfileHealthEvidence.NetworkKey(), ProfileHealthEvidence.CoreKey(r.Engine == VpnEngine.SingBox ? singbox : reality), DateTime.UtcNow)))
            status.Text = "Результаты устарели: прошло 3 минуты либо изменились профиль, сеть или файл ядра. Для нового сравнения повторите серию.";
    }
    private async Task StartAsync()
    {
        if (preview || busy || runner.HasActive || closing) return;
        busy = true; results.Clear(); Controls();
        try
        {
            var target = new ServiceCheckTarget("Сравнение", address.Text);
            var profile = currentProfile(); activeProfileStamp = ServiceMatrixRunner.ProfileStamp(profile); Interlocked.Exchange(ref contextAllowed, 1);
            status.Text = "Выполняется серия. Следующий процесс запустится после подтверждённой остановки предыдущего.";
            await runner.RunAsync(profile, target, singbox, reality, runtime, eco.IsChecked == true, () => mayRun() && Volatile.Read(ref contextAllowed) != 0,
                result => Dispatcher.BeginInvoke(new Action(() => results.Add(result))), lifetime);
            status.Text = "Серия завершена. Строки с ошибками не доказывают равную успешную нагрузку; подробности — в таблице.";
        }
        catch (OperationCanceledException) { status.Text = "Сравнение отменено."; }
        catch (Exception error) { status.Text = error is ArgumentException || error is NotSupportedException || error is InvalidOperationException || error is FileNotFoundException ? error.Message : "Не удалось завершить сравнение. Повторите остановку перед новым запуском."; }
        finally { busy = false; Controls(); Refresh(); }
    }
    private async Task StopAsync()
    {
        if (preview || stopping || closing) return; stopping = true; Controls();
        try { await runner.StopAsync(); status.Text = "Собственные процессы сравнения остановлены."; }
        catch { status.Text = "Не все процессы подтвердили остановку. Нажмите «Остановить» ещё раз."; }
        finally { stopping = false; Controls(); }
    }
}

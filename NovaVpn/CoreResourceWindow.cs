using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace NovaVpn;

public sealed class CoreUsageRecord
{
    public string Session { get; set; }
    public DateTime CheckedUtc { get; set; }
    public double DurationSeconds { get; set; }
    public double CpuPercent { get; set; }
    public long PeakWorkingSet { get; set; }
    public long PeakPrivateBytes { get; set; }
    public CorePowerMode Mode { get; set; }
    public string TimeLabel => CheckedUtc.ToLocalTime().ToString("HH:mm:ss");
    public string CpuLabel => CpuPercent.ToString("0.00") + "%";
    public string MemoryLabel => (PeakWorkingSet / 1048576.0).ToString("0.0") + " / " + (PeakPrivateBytes / 1048576.0).ToString("0.0") + " МиБ";
    public string ModeLabel => CoreResourceWindow.PowerLabel(Mode);
    public static CoreUsageRecord From(IReadOnlyList<CoreResourceSnapshot> samples, string session)
        => FromSamples(samples, session, 64);
    internal static CoreUsageRecord FromBenchmark(IReadOnlyList<CoreResourceSnapshot> samples, string session)
        => FromSamples(samples, session, 512);
    private static CoreUsageRecord FromSamples(IReadOnlyList<CoreResourceSnapshot> samples, string session, int maximum)
    {
        if (samples == null || samples.Count < 3 || samples.Count > maximum) throw new ArgumentException("Недостаточно согласованных измерений.");
        var sampler = new CoreResourceSampler(); sampler.Observe(samples[0]);
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].PowerMode != samples[0].PowerMode || !sampler.Observe(samples[i]).HasValue) throw new InvalidOperationException("Ядро, режим или счётчики изменились во время наблюдения.");
        var first = samples[0]; var last = samples[samples.Count - 1]; double seconds = (last.Timestamp - first.Timestamp) / (double)first.Frequency;
        return new CoreUsageRecord { Session = session, CheckedUtc = DateTime.UtcNow, DurationSeconds = seconds, Mode = first.PowerMode,
            CpuPercent = Math.Min(100, 100.0 * ((last.CpuTicks - first.CpuTicks) / (double)TimeSpan.TicksPerSecond) / seconds / first.LogicalProcessors),
            PeakWorkingSet = samples.Max(s => s.WorkingSetBytes), PeakPrivateBytes = samples.Max(s => s.PrivateBytes) };
    }
}

public sealed class CoreResourceWindow : Window
{
    private readonly CoreManager core;
    private readonly Action<bool> save;
    private readonly Func<string> description;
    private readonly ObservableCollection<CoreUsageRecord> records;
    private readonly bool preview;
    private readonly CancellationTokenSource lifetime;
    private readonly CoreResourceSampler sampler = new CoreResourceSampler();
    private readonly DispatcherTimer timer;
    private readonly TextBlock cpu, memory, nativeMode, status;
    private readonly CheckBox eco;
    private readonly Button record;
    private bool reading, recording, changing, closed, setting, statusOwned;
    private Exception lastRecordError;
    public CoreResourceWindow(CoreManager core, ObservableCollection<CoreUsageRecord> records, Func<string> description, Action<bool> save, VisualPreferences visual, bool preview, CancellationToken token)
    {
        this.core = core; this.records = records; this.description = description; this.save = save; this.preview = preview;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Нагрузка VPN-ядра"; Width = Math.Min(900, SystemParameters.WorkArea.Width); Height = Math.Min(700, SystemParameters.WorkArea.Height); MinWidth = Math.Min(600, Width); MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 13 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(20) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "CPU, память и EcoQoS", FontSize = FontSize * 1.5, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Основной процесс VPN-ядра. CPU и память клиента и служебного хоста сюда не входят.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 9) });
        cpu = new TextBlock { Text = "CPU: —", Margin = new Thickness(0, 3, 0, 3) }; memory = new TextBlock { Text = "Память: —", TextWrapping = TextWrapping.Wrap }; nativeMode = new TextBlock { Text = "Режим Windows: не проверен", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 9) };
        header.Children.Add(cpu); header.Children.Add(memory); header.Children.Add(nativeMode);
        eco = new CheckBox { Content = new TextBlock { Text = "Запрашивать EcoQoS для основного ядра", Foreground = Foreground, TextWrapping = TextWrapping.Wrap }, Foreground = Foreground, IsChecked = core.EcoModeRequested, Margin = new Thickness(0, 5, 0, 5) };
        eco.Click += async delegate { await ChangeEcoAsync(); }; header.Children.Add(eco);
        header.Children.Add(new TextBlock { Text = "EcoQoS может повысить задержки.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 5) });
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = "CPU — доля мощности всех логических процессоров. Память: рабочий набор / приватные байты. При выключении EcoQoS восстанавливается исходная политика процесса. Фактическое состояние показано выше; энергосбережение не измеряется в ваттах.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        details.Children.Add(new TextBlock { Text = "Наблюдения за 10 секунд сохраняются до закрытия клиента. Для сравнения запишите нагрузку, отключите VPN, выберите другое ядро и повторите с той же задачей. Это наблюдения при текущем трафике, не автоматический бенчмарк.", TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new Expander { Header = "Как измерять и сравнивать", Foreground = Foreground, Margin = new Thickness(0, 0, 0, 10), Content = new ScrollViewer { Content = details, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }); root.Children.Add(header);
        var grid = new DataGrid { ItemsSource = records, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, Background = new SolidColorBrush(palette.Input), Foreground = Foreground, RowBackground = new SolidColorBrush(palette.Input), AlternatingRowBackground = new SolidColorBrush(palette.Surface), HeadersVisibility = DataGridHeadersVisibility.Column, FrozenColumnCount = 1 };
        var text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); text.Setters.Add(new Setter(TextBlock.ForegroundProperty, Foreground)); text.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(6)));
        foreach (var column in new[] { new[] { "Ядро / профиль", "Session", "220" }, new[] { "Время", "TimeLabel", "90" }, new[] { "CPU", "CpuLabel", "90" }, new[] { "Память: рабочая / приватная", "MemoryLabel", "190" }, new[] { "EcoQoS", "ModeLabel", "140" } }) grid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = column[0], TextWrapping = TextWrapping.Wrap }, Binding = new Binding(column[1]), Width = double.Parse(column[2], CultureInfo.InvariantCulture), ElementStyle = text });
        var headers = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)); headers.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Surface))); headers.Setters.Add(new Setter(Control.ForegroundProperty, Foreground)); headers.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7))); grid.ColumnHeaderStyle = headers;
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; status = new TextBlock { Text = preview ? "Предпросмотр: управление процессами отключено." : "Подключите VPN для чтения метрик. Настройка EcoQoS сохраняется для следующих подключений.", TextWrapping = TextWrapping.Wrap }; footer.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; record = new Button { Content = "Записать 10 секунд", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 6) }; record.Click += async delegate { await RecordAsync(); }; actions.Children.Add(record);
        var clear = new Button { Content = "Очистить наблюдения", IsEnabled = !preview, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 6) }; clear.Click += delegate { if (!recording && !preview) { records.Clear(); statusOwned = true; status.Text = "Наблюдения очищены."; } }; actions.Children.Add(clear);
        var close = new Button { Content = "Закрыть", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 6) }; close.Click += delegate { Close(); }; actions.Children.Add(close); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += async delegate { await ReadAsync(); };
        Loaded += async delegate { if (!preview) { timer.Start(); await ReadAsync(); } };
        Closed += delegate { closed = true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); }; Controls();
    }
    internal static string PowerLabel(CorePowerMode mode) => mode == CorePowerMode.Eco ? "EcoQoS включён" : mode == CorePowerMode.Automatic ? "Политика Windows" : mode == CorePowerMode.ExplicitNormal ? "EcoQoS выключен" : "Не подтверждён";
    private void Controls() { eco.IsEnabled = !preview && !reading && !recording && !changing; record.IsEnabled = !preview && !reading && !recording && !changing && core.Status == CoreStatus.Connected; }
    private void Show(CoreResourceSnapshot snapshot)
    {
        double? value = sampler.Observe(snapshot); cpu.Text = "CPU: " + (value.HasValue ? value.Value.ToString("0.00") + "%" : "нужен следующий замер");
        memory.Text = "Память: " + (snapshot.WorkingSetBytes / 1048576.0).ToString("0.0") + " МиБ рабочая · " + (snapshot.PrivateBytes / 1048576.0).ToString("0.0") + " МиБ приватная";
        nativeMode.Text = "Режим Windows: " + PowerLabel(snapshot.PowerMode) + (snapshot.PowerMode == CorePowerMode.Unavailable ? " · код " + snapshot.PowerError : "");
    }
    private async Task ReadAsync()
    {
        if (preview || closed || reading || recording || changing) return; reading = true; Controls();
        try { Show(await core.ReadResourcesAsync(lifetime.Token)); if (!statusOwned) status.Text = "Измеряется: " + description() + ". Наблюдения зависят от текущего трафика."; }
        catch { sampler.Reset(); cpu.Text = "CPU: —"; memory.Text = "Память: —"; nativeMode.Text = "Режим Windows: нет подтверждённых данных"; }
        finally { reading = false; Controls(); }
    }
    private async Task ChangeEcoAsync()
    {
        if (preview || closed || recording || changing || reading || setting) { setting = true; eco.IsChecked = core.EcoModeRequested; setting = false; return; }
        bool desired = eco.IsChecked == true, accepted = false; changing = true; statusOwned = true; Controls();
        try { await core.SetEcoModeAsync(desired, lifetime.Token); accepted = true; save(desired); sampler.Reset(); status.Text = "Настройка сохранена. Фактический режим Windows отображается отдельно."; }
        catch { setting = true; eco.IsChecked = core.EcoModeRequested; setting = false; status.Text = accepted ? "Настройка принята, но не сохранена на диск. После перезапуска могут вернуться прежние параметры." : "Windows не подтвердила изменение. Настройка не сохранена."; }
        finally { changing = false; Controls(); if (!closed) await ReadAsync(); }
    }
    private async Task RecordAsync()
    {
        if (preview || closed || recording || reading || changing) return; recording = true; statusOwned = true; lastRecordError = null; Controls();
        try
        {
            var samples = new List<CoreResourceSnapshot>(); string session = description();
            for (int i = 0; i <= 10; i++) { var sample = await core.ReadResourcesAsync(lifetime.Token); samples.Add(sample); Show(sample); status.Text = "Наблюдение: " + i + " из 10 секунд…"; if (i < 10) await Task.Delay(1000, lifetime.Token); }
            var result = CoreUsageRecord.From(samples, session); if (closed) return;
            if (records.Count >= 50) records.RemoveAt(0); records.Add(result); status.Text = "Наблюдение сохранено: " + result.CpuLabel + " · " + result.MemoryLabel + ". Нагрузка зависит от выполняемой задачи.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { lastRecordError = error; if (!closed) status.Text = "Наблюдение не записано: ядро, режим или измерение изменились."; }
        finally { recording = false; Controls(); }
    }
}

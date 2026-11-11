using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace NovaVpn;

public sealed class CorporatePacWindow : Window
{
    private readonly bool preview;
    private readonly ObservableCollection<CorporatePacEntry> rows = new ObservableCollection<CorporatePacEntry>();
    private readonly TextBox script, target;
    private readonly Button inspect, cancel;
    private readonly TextBlock status;
    private readonly DispatcherTimer timer;
    private CancellationTokenSource cancellation;
    private Task running;
    private CorporatePacInspection result;
    private bool busy, closePending, allowClose;
    public CorporatePacWindow(string targetUrl, VisualPreferences visual, bool preview) : this(targetUrl, visual, preview, "") { }
    public CorporatePacWindow(string targetUrl, VisualPreferences visual, bool preview, string scriptUrl)
    {
        this.preview = preview; var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Проверка PAC"; Width = Math.Min(940, SystemParameters.WorkArea.Width); Height = Math.Min(740, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(580, Width); MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 13 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(18) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "Какой прокси вернёт PAC", FontSize = FontSize * 1.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = "Скрипт получит указанный ниже адрес. Ответ Windows показывается без перестановки.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        Func<string, string, string, TextBox> input = (label, name, text) => {
            header.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); var box = new TextBox { Name = name, Text = text, MaxLength = 4096, Padding = new Thickness(7), Margin = new Thickness(0, 4, 0, 8), Background = new SolidColorBrush(palette.Input), Foreground = Foreground, CaretBrush = Foreground }; header.Children.Add(box); return box;
        };
        script = input("HTTP(S)-адрес PAC", "PacScript", scriptUrl ?? ""); target = input("Проверяемый адрес", "PacTarget", targetUrl ?? "");
        header.Children.Add(new Expander { Header = "Какие данные получит PAC", Foreground = Foreground, Margin = new Thickness(0, 0, 0, 8), Content = new TextBlock { Text = "Выбирайте скрипт, выданный вашей организацией. Он может выполнять DNS-запросы. Для VPN адрес описывает его TCP-сервер: HTTPS для TLS, HTTP для остальных транспортов. Пароли, UUID и путь профиля не передаются. Проверка не подключается к этому адресу. Вход Windows и поиск PAC в сети не выполняются.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) } });
        root.Children.Add(new ScrollViewer { Content = header, MaxHeight = 270, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var table = new DataGrid { Name = "PacResults", ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, FrozenColumnCount = 1,
            Background = new SolidColorBrush(palette.Input), Foreground = Foreground, RowBackground = new SolidColorBrush(palette.Input), AlternatingRowBackground = new SolidColorBrush(palette.Surface), HeadersVisibility = DataGridHeadersVisibility.Column };
        var textStyle = new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Foreground)); textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(6)));
        foreach (var column in new[] { new[] { "№", "Position", "45" }, new[] { "Тип", "KindLabel", "190" }, new[] { "Сервер", "Server", "210" }, new[] { "Порт", "PortLabel", "75" }, new[] { "Значение", "PolicyLabel", "310" } })
            table.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = column[0], TextWrapping = TextWrapping.Wrap }, Binding = new Binding(column[1]), Width = int.Parse(column[2]), ElementStyle = textStyle });
        var headers = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)); headers.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Surface))); headers.Setters.Add(new Setter(Control.ForegroundProperty, Foreground)); headers.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7))); table.ColumnHeaderStyle = headers;
        Grid.SetRow(table, 1); root.Children.Add(table);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "Только проверка ответа Windows. Настройки подключения не меняются. DIRECT в таблице не разрешает прямой выход.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        status = new TextBlock { Text = preview ? "Предпросмотр: запросы отключены." : "Скрипт загружается только после нажатия «Проверить».", TextWrapping = TextWrapping.Wrap }; footer.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        inspect = new Button { Name = "InspectPac", Content = "Проверить", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 5) }; inspect.Click += async delegate { if (preview || busy) return; running = InspectAsync(); await running; }; actions.Children.Add(inspect);
        cancel = new Button { Name = "CancelPac", Content = "Отменить запрос", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 5) }; cancel.Click += delegate { cancellation?.Cancel(); }; actions.Children.Add(cancel);
        var close = new Button { Content = "Закрыть", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 5) }; close.Click += delegate { Close(); }; actions.Children.Add(close); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; timer.Tick += delegate { Refresh(); }; timer.Start();
        Closing += async (sender, args) => { if (allowClose || !busy) return; args.Cancel = true; if (closePending) return; closePending = true; cancellation?.Cancel(); if (running != null) await running; allowClose = true; Close(); };
        Closed += delegate { timer.Stop(); cancellation?.Dispose(); }; Controls();
    }
    private void Controls() { inspect.IsEnabled = !preview && !busy && !closePending; cancel.IsEnabled = !preview && busy && !closePending; script.IsEnabled = target.IsEnabled = !busy; }
    private void Refresh()
    {
        if (busy || result == null) return;
        bool fresh = false; try { fresh = result.IsCurrent(new CorporatePacQuery(script.Text, target.Text), ProfileHealthEvidence.NetworkKey(), DateTime.UtcNow); } catch (ArgumentException) { }
        if (!fresh) status.Text = "Ответ устарел: изменились адреса, сеть либо прошло три минуты. Повторите проверку.";
    }
    private async Task InspectAsync()
    {
        busy = true; result = null; rows.Clear(); cancellation?.Dispose(); cancellation = new CancellationTokenSource(); Controls();
        try
        {
            var query = new CorporatePacQuery(script.Text, target.Text); status.Text = "Загружаем и проверяем PAC…";
            result = await CorporatePacResolver.InspectAsync(query, cancellation.Token);
            foreach (var entry in result.Entries) rows.Add(entry);
            status.Text = "Получен ответ Windows. Прокси не проверялись подключением, настройки VPN не изменены.";
        }
        catch (OperationCanceledException) { status.Text = "Запрос отменён."; }
        catch (Exception error) { status.Text = error is Win32Exception native ? "Проверка PAC не завершилась. Код Windows: " + native.NativeErrorCode : error is ArgumentException || error is TimeoutException || error is InvalidOperationException ? error.Message : "Не удалось прочитать ответ PAC. Настройки VPN не изменены."; }
        finally { busy = false; Controls(); Refresh(); }
    }
}

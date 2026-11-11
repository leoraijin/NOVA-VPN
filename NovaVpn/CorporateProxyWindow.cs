using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed class CorporateProxyWindow : Window
{
    public CorporateProxySettings Accepted { get; private set; }
    public CorporateProxyWindow(VpnProfile profile, VisualPreferences visual, bool preview)
    {
        var original = profile?.CorporateProxy?.Copy() ?? new CorporateProxySettings();
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Корпоративный прокси"; Width = Math.Min(650, SystemParameters.WorkArea.Width); Height = Math.Min(760, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(540, Width); MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 13 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(20) }; root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var fields = new StackPanel(); fields.Children.Add(new TextBlock { Text = profile?.Name ?? "Профиль не выбран", FontSize = FontSize * 1.4, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(new TextBlock { Text = "Компьютер → корпоративный прокси → VPN-сервер. Настройка относится к этому профилю, его проверкам и локальным прокси. Нужен sing-box. Прямые исключения маршрутизации сохраняются.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        var enabled = new CheckBox { Name = "EnabledProxy", Content = "Подключаться через корпоративный прокси", Foreground = Foreground, IsChecked = original.Enabled, Margin = new Thickness(0, 5, 0, 8) }; fields.Children.Add(enabled);
        Action<string> label = text => fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
        Func<string, string, string, TextBox> input = (caption, name, value) => { label(caption); var box = new TextBox { Name = name, Text = value ?? "", Padding = new Thickness(7), Background = new SolidColorBrush(palette.Input), Foreground = Foreground, CaretBrush = Foreground }; fields.Children.Add(box); return box; };
        label("Протокол прокси"); var kind = new ComboBox { Name = "ProxyKind", Style = Application.Current?.Resources["NovaSelectorStyle"] as Style, Foreground = Foreground, Background = new SolidColorBrush(palette.Input), Padding = new Thickness(5) };
        foreach (var option in new[] { new[] { "HTTP CONNECT", "http" }, new[] { "HTTPS CONNECT", "https" }, new[] { "SOCKS5", "socks5" }, new[] { "PAC · локальные прокси и проверки", "pac" } }) kind.Items.Add(new ComboBoxItem { Content = option[0], Tag = option[1] });
        kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, original.Kind)); fields.Children.Add(kind);
        var manual = new StackPanel { Name = "ManualProxyFields" }; fields.Children.Add(manual);
        var fieldRoot = fields; fields = manual;
        var server = input("Имя или IP прокси, без URL", "ProxyServer", original.Server); var port = input("Порт", "ProxyPort", original.Port.ToString());
        var username = input("Логин — необязательно", "ProxyUsername", original.Username);
        label("Пароль"); var password = new PasswordBox { Name = "ProxyPassword", Password = original.Password ?? "", MaxLength = 255, Padding = new Thickness(7), Background = new SolidColorBrush(palette.Input), Foreground = Foreground, CaretBrush = Foreground }; fields.Children.Add(password);
        var tlsName = input("Имя проверки TLS для HTTPS — по умолчанию имя прокси", "ProxyTlsName", original.TlsServerName);
        var ca = input("PEM-файл доверенных CA для HTTPS — необязательно", "ProxyCa", original.CertificatePath);
        fields = fieldRoot;
        var pacFields = new StackPanel { Name = "PacProxyFields" }; fields.Children.Add(pacFields); fields = pacFields;
        var script = input("Адрес PAC-файла — HTTP или HTTPS", "PacScript", original.PacScript); script.MaxLength = 4096;
        var direct = new CheckBox { Name = "PacAllowDirect", Content = new TextBlock { Text = "Разрешить DIRECT из PAC: соединение с VPN-сервером без корпоративного прокси", TextWrapping = TextWrapping.Wrap }, IsChecked = original.Kind == "pac" && original.PacAllowDirect, Foreground = Foreground, Margin = new Thickness(0, 10, 0, 8) }; fields.Children.Add(direct);
        fields.Children.Add(new TextBlock { Text = "По умолчанию DIRECT запрещён. Прокси проверяются в порядке PAC. Сейчас доступны анонимные HTTP/HTTPS-прокси; вход Windows и SOCKS из PAC не поддерживаются. PAC работает в локальных прокси и проверках профиля. Основное VPN-подключение по PAC пока недоступно.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) });
        fields = fieldRoot;
        Action controls = () => {
            string protocol = (kind.SelectedItem as ComboBoxItem)?.Tag as string;
            bool active = enabled.IsChecked == true && !preview, isPac = protocol == "pac";
            manual.Visibility = isPac ? Visibility.Collapsed : Visibility.Visible;
            pacFields.Visibility = isPac ? Visibility.Visible : Visibility.Collapsed;
            manual.IsEnabled = active; pacFields.IsEnabled = active; kind.IsEnabled = active;
            tlsName.IsEnabled = ca.IsEnabled = protocol == "https" && active;
        };
        kind.SelectionChanged += delegate { controls(); }; enabled.Checked += delegate { controls(); }; enabled.Unchecked += delegate { controls(); }; controls();
        var message = new TextBlock { Name = "ProxyMessage", Text = preview ? "Предпросмотр: сохранение отключено." : "HTTP и SOCKS5 не шифруют соединение до прокси. HTTPS проверяет сертификат; отключение проверки не предусмотрено. Пароль сохраняется в защищённом состоянии Windows NOVA. PAC доступен для локальных прокси и проверок; основной туннель и вход Windows ещё в работе.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8) }; fields.Children.Add(message);
        var pac = new Button { Name = "InspectPac", Content = "Проверить ответ PAC", IsEnabled = !preview, Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        pac.Click += delegate {
            if (preview) return;
            try { new CorporatePacWindow(CorporatePacQuery.ProfileTarget(profile), visual, false, Equals((kind.SelectedItem as ComboBoxItem)?.Tag, "pac") ? script.Text : "") { Owner = this }.ShowDialog(); }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException) { message.Text = error.Message; }
        }; fields.Children.Add(pac);
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; root.Children.Add(scroll);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 8, 5) }; cancel.Click += delegate { DialogResult = false; }; actions.Children.Add(cancel);
        var save = new Button { Name = "SaveProxy", Content = "Сохранить", IsEnabled = !preview, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 0, 5) };
        save.Click += delegate {
            if (preview) return;
            try
            {
                bool active = enabled.IsChecked == true;
                string protocol = (kind.SelectedItem as ComboBoxItem)?.Tag as string; bool isPac = protocol == "pac";
                int selectedPort = 8080;
                if (!isPac && !int.TryParse(port.Text, out selectedPort) && active) throw new ArgumentException("Укажите целый номер порта.");
                var result = new CorporateProxySettings { Enabled = active, Kind = protocol, Server = isPac ? "" : server.Text.Trim(), Port = selectedPort,
                    Username = isPac ? "" : username.Text, Password = isPac ? "" : password.Password,
                    TlsServerName = protocol == "https" ? tlsName.Text.Trim() : "", CertificatePath = protocol == "https" ? ca.Text.Trim() : "",
                    PacScript = isPac ? script.Text.Trim() : "", PacAllowDirect = isPac && direct.IsChecked == true };
                result.Validate(); Accepted = result; DialogResult = true;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException) { message.Text = error.Message; message.Foreground = new SolidColorBrush(palette.Warning); scroll.ScrollToEnd(); }
        };
        actions.Children.Add(save); Grid.SetRow(actions, 1); root.Children.Add(actions); Content = root;
    }
}

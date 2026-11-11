using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed class LocalProxyEditorWindow : Window
{
    public LocalProxyEndpoint Accepted { get; private set; }
    public LocalProxyEditorWindow(LocalProxyEndpoint original, IEnumerable<VpnProfile> profiles, VisualPreferences visual)
    {
        original = original ?? new LocalProxyEndpoint();
        var available = profiles.Where(p => p != null && !p.RetiredFromSubscription && p.Protocol != "sing-box").GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var palette = ThemePalette.Create(visual ?? new VisualPreferences());
        Title = "Локальный прокси для задачи"; Width = Math.Min(620, SystemParameters.WorkArea.Width); Height = Math.Min(680, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(480, Width); MinHeight = Math.Min(500, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(palette.Bg); Foreground = new SolidColorBrush(palette.Text); FontSize = 13 * (visual?.TextScale ?? 1);
        var root = new Grid { Margin = new Thickness(20) }; root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var fields = new StackPanel();
        Action<string> label = text => fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 4) });
        Func<string, string, TextBox> input = (caption, value) => {
            label(caption); var box = new TextBox { Text = value, MaxLength = 512, Padding = new Thickness(7), Background = new SolidColorBrush(palette.Input), Foreground = Foreground }; fields.Children.Add(box); return box;
        };
        var name = input("Название задачи", original.Name);
        label("VPN-профиль"); var profile = new ComboBox { MinHeight = 32, Style = Application.Current?.Resources["NovaSelectorStyle"] as Style };
        foreach (var p in available) profile.Items.Add(new ComboBoxItem { Content = p.Name + " · " + p.ProtocolLabel, Tag = p.Id, ToolTip = p.Endpoint });
        profile.SelectedItem = profile.Items.OfType<ComboBoxItem>().FirstOrDefault(p => (string)p.Tag == original.ProfileId);
        if (profile.SelectedIndex < 0 && original.ProfileId == null) profile.SelectedIndex = 0;
        fields.Children.Add(profile);
        label("Тип локального входа"); var kind = new ComboBox { MinHeight = 32, Style = Application.Current?.Resources["NovaSelectorStyle"] as Style };
        foreach (var item in new[] { new[] { "mixed", "HTTP и SOCKS5 на одном порту" }, new[] { "http", "Только HTTP-прокси" }, new[] { "socks", "Только SOCKS5" } }) kind.Items.Add(new ComboBoxItem { Content = item[1], Tag = item[0] });
        kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().FirstOrDefault(p => (string)p.Tag == original.InboundType); fields.Children.Add(kind);
        var port = input("Порт на 127.0.0.1", original.Port.ToString());
        var username = input("Логин локального входа", original.Username);
        label("Пароль локального входа"); var password = new PasswordBox { Password = original.Password ?? "", MaxLength = 512, Padding = new Thickness(7) }; fields.Children.Add(password);
        var generate = new Button { Content = "Новый случайный пароль", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 4) };
        generate.Click += delegate { password.Password = Guid.NewGuid().ToString("N"); }; fields.Children.Add(generate);
        var message = new TextBlock { Text = "Укажите этот адрес, логин и пароль в нужном приложении. Прокси слушает только этот компьютер, работает отдельно от TUN и останавливается при закрытии NOVA. После импорта настроек он сам не запускается.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
        fields.Children.Add(message);
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        Action<string> showError = text => { message.Text = text; message.Foreground = new SolidColorBrush(palette.Warning); scroll.ScrollToEnd(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) }; cancel.Click += delegate { DialogResult = false; };
        var save = new Button { Content = "Сохранить", Padding = new Thickness(18, 9, 18, 9) };
        save.Click += delegate {
            if (!int.TryParse(port.Text, out int selectedPort)) { showError("Укажите целый номер порта от 1 до 65535."); return; }
            var result = new LocalProxyEndpoint { Id = original.Id, Name = name.Text.Trim(), ProfileId = (profile.SelectedItem as ComboBoxItem)?.Tag as string,
                InboundType = (kind.SelectedItem as ComboBoxItem)?.Tag as string, Port = selectedPort, Username = username.Text, Password = password.Password };
            string error = LocalProxyConfiguration.Validate(result, available.FirstOrDefault(p => p.Id == result.ProfileId));
            if (error != null) { showError(error); return; }
            Accepted = result; DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetRow(actions, 1); root.Children.Add(actions); Content = root;
    }
}

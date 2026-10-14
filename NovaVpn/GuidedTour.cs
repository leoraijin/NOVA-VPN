using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private sealed class TourStep
    {
        public string Page, Anchor, Title, Copy;
        public TourStep(string page, string anchor, string title, string copy) { Page = page; Anchor = anchor; Title = title; Copy = copy; }
    }
    private static readonly TourStep[] tourSteps = {
        new TourStep("servers", "servers.import", "Добавьте ваш сервер", "Нажмите «Добавить ключ», чтобы вставить VPN-ключ или ссылку на подписку от провайдера. Во время обучения ничего вводить не нужно."),
        new TourStep("servers", "servers.best", "Выберите доступное подключение", "«Выбрать лучший» проверяет серверы и выбирает подходящий. Имя и страну выбранного сервера видно на главной странице."),
        new TourStep("home", "home.mode", "Не перепутайте режимы", "Для обычного подключения выбирайте VPN. VPN + Zapret включает оба инструмента, а Zapret сам по себе не создаёт VPN-туннель. Выбранный режим отмечен галочкой."),
        new TourStep("home", "home.connect", "Одна кнопка для подключения", "Эта кнопка включает VPN выбранного сервера. Повторное нажатие отключает его. Здесь мы только показываем кнопку — ваше соединение не изменится."),
        new TourStep("home", "home.status", "Проверьте, что интернет работает", "Смотрите не только на включённый туннель: индикаторы показывают состояние DNS и проверку интернета. Если сайты не открываются, попробуйте другой сервер."),
        new TourStep("routing", "routing.domains", "Разделите сайты", "В «Через VPN» добавляйте нужные домены, в «Напрямую» — сайты для доступа без VPN. Для точного разделения отключите защищённый DNS браузера; сторонние CDN-домены добавляйте отдельно."),
        new TourStep("routing", "routing.apps", "Выберите маршрут приложений", "У каждого приложения есть три положения: по умолчанию, через VPN и напрямую. Домены из «Напрямую» имеют приоритет над маршрутом браузера. Изменение настроек может потребовать применения маршрутов."),
        new TourStep("settings", "settings.appearance", "Выберите оформление", "Персонализация меняет стиль, размер и эффекты интерфейса. Она не меняет сервер или маршрут подключения."),
        new TourStep("settings", "settings.startup", "Настройте запуск с Windows", "Первый тумблер только открывает NOVA. Второй запускает её скрыто и подключает VPN: сначала последний профиль, а при его отказе — лучший из доступных."),
        new TourStep("diagnostics", "diagnostics.run", "Если что-то не работает", "«Запустить проверку» помогает найти проблему с сервером, ядром, DNS или правилами. Не отправляйте другим людям VPN-ключи и полные конфиги."),
        new TourStep("settings", "settings.update", "Обновления всегда под рукой", "Здесь можно получить новую версию с GitHub. Обучение завершено! Вы сможете повторить его кнопкой «Начать обучение» в боковом меню.")
    };
    private readonly Dictionary<string, FrameworkElement> tourAnchors = new Dictionary<string, FrameworkElement>();
    private Dictionary<string, double> tourSavedScroll;
    private Grid tourPageRoot;
    private Canvas tourOverlay;
    private bool tourActive, tourRenderQueued, tourHooksInstalled, tourRevealTarget;
    private int tourIndex;
    private string tourReturnPage;
    private IInputElement tourReturnFocus;

    private T TourAnchor<T>(string key, T element) where T : UIElement
    {
        if (element is FrameworkElement anchor) tourAnchors[key] = anchor;
        return element;
    }

    private void StartGuidedTour()
    {
        if (tourActive) return;
        tourReturnPage = currentPage;
        tourReturnFocus = Keyboard.FocusedElement;
        tourSavedScroll = new Dictionary<string, double>(scrollPositions);
        tourActive = true;
        tourIndex = 0;
        if (!tourHooksInstalled) { PreviewKeyDown += TourKeyDown; tourHooksInstalled = true; }
        ShowTourStep();
    }

    private void ShowTourStep()
    {
        if (!tourActive) return;
        tourRevealTarget = true;
        Navigate(tourSteps[tourIndex].Page, false);
        if (tourPageRoot != null) { tourPageRoot.IsHitTestVisible = false; KeyboardNavigation.SetTabNavigation(tourPageRoot, KeyboardNavigationMode.None); }
    }

    private void EndGuidedTour()
    {
        tourActive = false;
        if (tourOverlay != null) shellRoot?.Children.Remove(tourOverlay);
        tourOverlay = null;
        if (tourPageRoot != null) { tourPageRoot.IsHitTestVisible = true; KeyboardNavigation.SetTabNavigation(tourPageRoot, KeyboardNavigationMode.Continue); }
        Navigate(tourReturnPage ?? "home", false);
        scrollPositions.Clear();
        if (tourSavedScroll != null) foreach (var pair in tourSavedScroll) scrollPositions[pair.Key] = pair.Value;
        double offset = scrollPositions.TryGetValue(currentPage, out var saved) ? saved : 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { FindVisualChild<ScrollViewer>(contentHost)?.ScrollToVerticalOffset(offset); });
        if (tourReturnFocus is FrameworkElement focus && focus.IsVisible && focus.IsEnabled) focus.Focus();
        else navItems["tour"].Button.Focus();
    }

    private void TourNext() { if (tourIndex == tourSteps.Length - 1) EndGuidedTour(); else { tourIndex++; ShowTourStep(); } }
    private void TourKeyDown(object sender, KeyEventArgs e)
    {
        if (!tourActive) return;
        if (e.Key == Key.Escape) { EndGuidedTour(); e.Handled = true; }
        else if (e.Key == Key.Right) { TourNext(); e.Handled = true; }
        else if (e.Key == Key.Left) { if (tourIndex > 0) { tourIndex--; ShowTourStep(); } e.Handled = true; }
    }

    private void QueueTourRender()
    {
        if (!tourActive || tourRenderQueued || closing) return;
        tourRenderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate {
            tourRenderQueued = false;
            if (tourActive && !closing) RenderTour();
        });
    }

    private void RenderTour()
    {
        if (shellRoot == null || shellRoot.ActualWidth < 100 || shellRoot.ActualHeight < 100) return;
        var step = tourSteps[tourIndex];
        tourAnchors.TryGetValue(step.Anchor, out var anchor);
        if (anchor == null && navItems.TryGetValue(step.Page, out var nav)) anchor = nav.Button;
        if (anchor == null) return;
        if (tourRevealTarget) { anchor.BringIntoView(); tourRevealTarget = false; }
        shellRoot.UpdateLayout();
        var bounds = new Rect(anchor.TranslatePoint(new Point(), shellRoot), new Size(anchor.ActualWidth, anchor.ActualHeight));
        var viewport = new Rect(contentHost.TranslatePoint(new Point(), shellRoot), new Size(contentHost.ActualWidth, contentHost.ActualHeight));
        if (tourAnchors.ContainsKey(step.Anchor)) bounds.Intersect(viewport);
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) {
            anchor = navItems[step.Page].Button;
            bounds = new Rect(anchor.TranslatePoint(new Point(), shellRoot), new Size(anchor.ActualWidth, anchor.ActualHeight));
        }
        bounds.Inflate(6, 6);
        var screen = new Rect(0, 0, shellRoot.ActualWidth, shellRoot.ActualHeight);
        bounds.Intersect(screen);
        if (tourOverlay != null) shellRoot.Children.Remove(tourOverlay);
        tourOverlay = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        Panel.SetZIndex(tourOverlay, 1000);
        KeyboardNavigation.SetTabNavigation(tourOverlay, KeyboardNavigationMode.Cycle);
        shellRoot.Children.Add(tourOverlay);
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(screen));
        mask.Children.Add(new RectangleGeometry(bounds, 12, 12));
        tourOverlay.Children.Add(new Path { Data = mask, Fill = new SolidColorBrush(Color.FromArgb(150, 8, 12, 24)), IsHitTestVisible = false });
        var highlight = new Border { Width = bounds.Width, Height = bounds.Height, CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Accent), BorderThickness = new Thickness(2.5), IsHitTestVisible = false };
        Canvas.SetLeft(highlight, bounds.Left); Canvas.SetTop(highlight, bounds.Top); tourOverlay.Children.Add(highlight);
        var copy = new StackPanel();
        copy.Children.Add(T("БЫСТРЫЙ СТАРТ  ·  " + (tourIndex + 1) + " / " + tourSteps.Length, 11, Accent, FontWeights.SemiBold));
        var progress = new Grid { Height = 4, Margin = new Thickness(0, 12, 0, 14) };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tourIndex + 1, GridUnitType.Star) });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tourSteps.Length - tourIndex - 1, GridUnitType.Star) });
        var track = new Border { Background = new SolidColorBrush(BorderColor), CornerRadius = new CornerRadius(2) }; Grid.SetColumnSpan(track, 2); progress.Children.Add(track);
        progress.Children.Add(new Border { Background = new SolidColorBrush(Accent), CornerRadius = new CornerRadius(2) });
        copy.Children.Add(progress);
        var title = T(step.Title, 21, TextColor, FontWeights.SemiBold); title.TextWrapping = TextWrapping.Wrap; copy.Children.Add(title);
        var description = T(step.Copy, 13, TextColor); description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(0, 10, 0, 16); copy.Children.Add(description);
        var actions = new WrapPanel();
        var skip = SmallButton("Пропустить"); skip.MinHeight = 44; skip.Click += delegate { EndGuidedTour(); }; actions.Children.Add(skip);
        var back = SmallButton("Назад"); back.MinHeight = 44; back.Margin = new Thickness(8, 0, 8, 0); back.IsEnabled = tourIndex > 0; back.Click += delegate { tourIndex--; ShowTourStep(); }; actions.Children.Add(back);
        var next = PrimaryButton(tourIndex == tourSteps.Length - 1 ? "Завершить" : "Далее →", 104); next.MinHeight = 44; next.Click += delegate { TourNext(); }; actions.Children.Add(next);
        copy.Children.Add(actions);
        var hint = T("Esc — выйти · ← / → — шаги", 10, Muted); hint.Margin = new Thickness(0, 10, 0, 0); copy.Children.Add(hint);
        var card = new Border { Width = Math.Min(370, screen.Width - 32), Padding = new Thickness(20), CornerRadius = Radius(18), Background = new SolidColorBrush(Color.FromRgb(Surface.R, Surface.G, Surface.B)), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = copy, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(100, screen.Height - 90) }, Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Opacity = 0.25 } };
        AutomationProperties.SetName(card, "Обучение. Шаг " + (tourIndex + 1) + ". " + step.Title);
        card.Measure(new Size(card.Width, screen.Height - 32));
        var size = card.DesiredSize;
        var candidates = new[] { new Point(bounds.Right + 22, bounds.Top), new Point(bounds.Left - size.Width - 22, bounds.Top), new Point(bounds.Left, bounds.Bottom + 22), new Point(bounds.Left, bounds.Top - size.Height - 22) };
        var position = candidates.Select(p => new Point(Math.Max(16, Math.Min(screen.Width - size.Width - 16, p.X)), Math.Max(16, Math.Min(screen.Height - size.Height - 16, p.Y))))
            .OrderBy(p => { var overlap = bounds; overlap.Intersect(new Rect(p, size)); return overlap.IsEmpty ? 0 : overlap.Width * overlap.Height; }).First();
        var target = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var from = new Point(Math.Max(position.X, Math.Min(position.X + size.Width, target.X)), Math.Max(position.Y, Math.Min(position.Y + size.Height, target.Y)));
        var direction = target - from;
        if (direction.Length > 16) {
            direction.Normalize();
            double edgeDistance = Math.Min(Math.Abs(direction.X) < 0.001 ? double.PositiveInfinity : bounds.Width / (2 * Math.Abs(direction.X)), Math.Abs(direction.Y) < 0.001 ? double.PositiveInfinity : bounds.Height / (2 * Math.Abs(direction.Y)));
            var tip = target - direction * (edgeDistance + 6);
            var normal = new Vector(-direction.Y, direction.X);
            tourOverlay.Children.Add(new Line { X1 = from.X, Y1 = from.Y, X2 = tip.X, Y2 = tip.Y, Stroke = new SolidColorBrush(Accent), StrokeThickness = 3, IsHitTestVisible = false });
            tourOverlay.Children.Add(new Polygon { Points = new PointCollection { tip, tip - direction * 12 + normal * 6, tip - direction * 12 - normal * 6 }, Fill = new SolidColorBrush(Accent), IsHitTestVisible = false });
        }
        Canvas.SetLeft(card, position.X); Canvas.SetTop(card, position.Y); tourOverlay.Children.Add(card);
        next.Focus();
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace NovaVpn;

/// <summary>Short, render-clock driven wheel motion; no idle timer or global rendering subscription.</summary>
public static class SmoothScrollBehavior
{
    public const int RequestedFrameRate = 120;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScrollBehavior));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScrollBehavior), new PropertyMetadata(0d, OffsetChanged));

    public static void Attach(ScrollViewer viewer, Func<bool> motionAllowed)
    {
        if (viewer.GetValue(StateProperty) != null) return;
        viewer.SetValue(StateProperty, new State(viewer, motionAllowed));
    }
    public static bool IsAnimating(ScrollViewer viewer) => (viewer.GetValue(StateProperty) as State)?.Active == true;
    public static void ForwardToPage(FrameworkElement source, MouseWheelEventArgs args)
    {
        if (args.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        for (var node = Parent(source); node != null; node = Parent(node)) {
            if (!(node is ScrollViewer page)) continue;
            args.Handled = true;
            var preview = new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = page };
            page.RaiseEvent(preview);
            if (!preview.Handled) page.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = Mouse.MouseWheelEvent, Source = page });
            return;
        }
    }
    public static double Target(double current, double previous, bool active, double delta, double maximum)
    {
        // Reverse immediately rather than waiting for the preceding direction's tail.
        double start = active && Math.Sign(previous - current) == Math.Sign(delta) ? previous : current;
        return Math.Max(0, Math.Min(maximum, start + delta));
    }
    private static void OffsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var viewer = (ScrollViewer)sender;
        if (viewer.IsLoaded) viewer.ScrollToVerticalOffset((double)args.NewValue);
    }
    private static DependencyObject Parent(DependencyObject value) =>
        value is Visual || value is Visual3D ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);

    private sealed class State
    {
        private readonly ScrollViewer viewer;
        private readonly Func<bool> motionAllowed;
        private double target;
        private int generation;
        internal bool Active;
        internal State(ScrollViewer viewer, Func<bool> motionAllowed)
        {
            this.viewer = viewer; this.motionAllowed = motionAllowed;
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += delegate { Stop(); };
            viewer.PreviewKeyDown += delegate { Stop(); };
            viewer.PreviewTouchDown += delegate { Stop(); };
            viewer.Unloaded += delegate { Stop(); };
            viewer.RequestBringIntoView += delegate { Stop(); };
        }
        private void Stop()
        {
            generation++;
            if (!Active) return;
            viewer.SetValue(OffsetProperty, viewer.VerticalOffset);
            viewer.BeginAnimation(OffsetProperty, null);
            Active = false;
        }
        private void Wheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || !viewer.IsLoaded || Keyboard.Modifiers != ModifierKeys.None) return;
            if (motionAllowed == null || !motionAllowed()) { Stop(); return; }
            // Logical/virtualized lists retain their native scrolling and virtualization.
            if (viewer.CanContentScroll || viewer.ScrollableHeight <= 0 || args.Delta == 0) return;
            for (var node = args.OriginalSource as DependencyObject; node != null && node != viewer; node = Parent(node))
                if (node is ScrollViewer nested && nested.ScrollableHeight > 0) return;
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            double step = lines < 0 ? viewer.ViewportHeight * .9 : Math.Max(16, viewer.FontSize * 1.35) * lines;
            double current = viewer.VerticalOffset;
            double next = Target(current, target, Active, -args.Delta / 120d * step, viewer.ScrollableHeight);
            if (Math.Abs(next - current) < .1) { Stop(); return; }
            Stop();
            target = next;
            viewer.SetValue(OffsetProperty, current);
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(180)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(animation, RequestedFrameRate);
            int token = ++generation;
            animation.Completed += delegate {
                if (token != generation) return;
                viewer.SetValue(OffsetProperty, Math.Min(target, viewer.ScrollableHeight));
                viewer.BeginAnimation(OffsetProperty, null);
                Active = false;
            };
            Active = true;
            viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            args.Handled = true;
        }
    }
}

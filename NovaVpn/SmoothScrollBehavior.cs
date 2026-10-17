using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Media.Media3D;

namespace NovaVpn;

/// <summary>Continuous render-paced wheel motion. Rendering is subscribed only while moving.</summary>
public static class SmoothScrollBehavior
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScrollBehavior));

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
    private static DependencyObject Parent(DependencyObject value) =>
        value is Visual || value is Visual3D ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);

    private sealed class State
    {
        private readonly ScrollViewer viewer;
        private readonly Func<bool> motionAllowed;
        private double target;
        private readonly Stopwatch clock = new Stopwatch();
        private double position, lastSeconds;
        private TimeSpan lastRenderTime = TimeSpan.MinValue;
        internal bool Active;
        internal State(ScrollViewer viewer, Func<bool> motionAllowed)
        {
            this.viewer = viewer; this.motionAllowed = motionAllowed;
            var descriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ScrollViewer));
            viewer.Loaded += delegate { descriptor.RemoveValueChanged(viewer, ContentChanged); descriptor.AddValueChanged(viewer, ContentChanged); PrepareSurface(); };
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += delegate { Stop(); };
            viewer.PreviewKeyDown += delegate { Stop(); };
            viewer.PreviewTouchDown += delegate { Stop(); };
            viewer.Unloaded += delegate { Stop(); descriptor.RemoveValueChanged(viewer, ContentChanged); };
            viewer.RequestBringIntoView += delegate { Stop(); };
        }
        private bool wrapping, ownsPhysicalSurface;
        private void ContentChanged(object sender, EventArgs args) { if (!wrapping && viewer.IsLoaded) { Stop(); PrepareSurface(); } }
        private void PrepareSurface()
        {
            if (viewer.Content is PixelScrollSurface || (!ownsPhysicalSurface && viewer.CanContentScroll && viewer.Content is System.Windows.Controls.Primitives.IScrollInfo)) return;
            if (!(viewer.Content is UIElement content)) return;
            wrapping = true;
            try {
                viewer.Content = null;
                viewer.Content = new PixelScrollSurface(content);
                viewer.CanContentScroll = true;
                ownsPhysicalSurface = true;
            } finally { wrapping = false; }
        }
        private void Stop()
        {
            if (!Active) return;
            CompositionTarget.Rendering -= Render;
            clock.Reset();
            Active = false;
        }
        private void Render(object sender, EventArgs args)
        {
            if (!viewer.IsLoaded || !motionAllowed()) { Stop(); return; }
            // WPF may raise Rendering twice for the same composition frame.
            var rendering = args as RenderingEventArgs;
            if (rendering != null && rendering.RenderingTime == lastRenderTime) return;
            if (rendering != null) lastRenderTime = rendering.RenderingTime;
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = Math.Max(0, now - lastSeconds);
            lastSeconds = now;
            target = Math.Max(0, Math.Min(target, viewer.ScrollableHeight));
            // Time based damping: wheel events update the target, never restart the motion.
            position += (target - position) * (1 - Math.Exp(-32 * elapsed));
            bool settled = Math.Abs(target - position) < .15;
            if (settled) position = target;
            if (viewer.Content is PixelScrollSurface surface) surface.SetVerticalOffset(position);
            else viewer.ScrollToVerticalOffset(position);
            if (settled) Stop();
        }
        private void Wheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || !viewer.IsLoaded || Keyboard.Modifiers != ModifierKeys.None) return;
            if (motionAllowed == null || !motionAllowed()) { Stop(); return; }
            // Logical/virtualized lists retain their native scrolling and virtualization.
            if ((viewer.CanContentScroll && !(viewer.Content is PixelScrollSurface)) || viewer.ScrollableHeight <= 0 || args.Delta == 0) return;
            for (var node = args.OriginalSource as DependencyObject; node != null && node != viewer; node = Parent(node))
                if (node is ScrollViewer nested && nested.ScrollableHeight > 0) return;
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            double step = lines < 0 ? viewer.ViewportHeight * .9 : Math.Max(16, viewer.FontSize * 1.35) * lines;
            double current = Active ? position : viewer.VerticalOffset;
            double next = Target(current, target, Active, -args.Delta / 120d * step, viewer.ScrollableHeight);
            if (Math.Abs(next - current) < .1) { Stop(); return; }
            target = next;
            if (!Active) {
                position = current;
                lastSeconds = 0;
                lastRenderTime = TimeSpan.MinValue;
                clock.Restart();
                Active = true;
                CompositionTarget.Rendering += Render;
            }
            args.Handled = true;
        }
    }
}

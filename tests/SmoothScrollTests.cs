using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NovaVpn;

internal static class SmoothScrollTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(int ms) {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static MouseWheelEventArgs Wheel(ScrollViewer viewer, int delta) {
        // Shared desktop: never synthesize key releases while the user is typing.
        for (int wait = 0; Keyboard.Modifiers != ModifierKeys.None && wait < 5000; wait += 25) Pump(25);
        Check(Keyboard.Modifiers == ModifierKeys.None, "Release modifier keys before synthetic plain-wheel test");
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer };
        viewer.RaiseEvent(args); return args;
    }
    [STAThread] public static int Main() {
        Window window = null;
        try {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            bool motion = true;
            var viewer = new ScrollViewer { Content = new Border { Height = 4000 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SmoothScrollBehavior.Attach(viewer, () => motion);
            SmoothScrollBehavior.Attach(viewer, () => motion); // idempotent
            window = new Window { Content = viewer, Width = 360, Height = 280, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); window.UpdateLayout(); Pump(30);
            Console.WriteLine("Input probe: loaded=" + viewer.IsLoaded + " modifiers=" + Keyboard.Modifiers + " lines=" + SystemParameters.WheelScrollLines);
            Check(viewer.ScrollableHeight > 1000, "Fixture has no scroll extent");
            Check(SmoothScrollBehavior.RequestedFrameRate == 120, "120 Hz target missing");
            int changes = 0;
            var descriptor = DependencyPropertyDescriptor.FromProperty(ScrollViewer.VerticalOffsetProperty, typeof(ScrollViewer));
            EventHandler changed = delegate { changes++; }; descriptor.AddValueChanged(viewer, changed);
            Check(Wheel(viewer, -120).Handled, "Wheel not intercepted");
            Check(SmoothScrollBehavior.IsAnimating(viewer), "No active animation");
            Check(viewer.VerticalOffset < 1, "Wheel jumped immediately");
            Pump(70);
            double middle = viewer.VerticalOffset;
            Check(middle > 0, "No intermediate animation movement");
            Pump(240);
            double end = viewer.VerticalOffset;
            Console.WriteLine("Scroll probe: middle=" + middle + " end=" + end + " active=" + SmoothScrollBehavior.IsAnimating(viewer) + " changes=" + changes);
            Check(end > middle && !SmoothScrollBehavior.IsAnimating(viewer), "Animation did not settle");
            Check(!viewer.HasAnimatedProperties, "Animation clock retained at idle");
            int settled = changes; Pump(200); Check(changes == settled, "Idle scrolling still running");
            Wheel(viewer, -240); Pump(35);
            double beforeReverse = viewer.VerticalOffset;
            Wheel(viewer, 120); Pump(240);
            Check(viewer.VerticalOffset < beforeReverse, "Direction reversal retained old momentum");
            Wheel(viewer, -120); Pump(30);
            viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(!SmoothScrollBehavior.IsAnimating(viewer), "Scrollbar/manual input did not cancel animation");
            motion = false; Check(!Wheel(viewer, -120).Handled, "Reduced motion ignored"); motion = true;
            viewer.CanContentScroll = true; Check(!Wheel(viewer, -120).Handled, "Logical scrolling/virtualization intercepted"); viewer.CanContentScroll = false;
            viewer.ScrollToVerticalOffset(0); window.UpdateLayout(); Pump(30);
            Check(!Wheel(viewer, 120).Handled, "Top edge should bubble");
            Check(SmoothScrollBehavior.Target(50, 100, true, 25, 200) == 125, "Rapid wheel target lost");
            Check(SmoothScrollBehavior.Target(50, 100, true, -25, 200) == 25, "Reverse target incorrect");
            Check(SmoothScrollBehavior.Target(190, 190, false, 99, 200) == 200, "Bottom clamp failed");
            for (int i = 0; i < 8; i++) Wheel(viewer, -15);
            Pump(250); Check(Math.Abs(viewer.VerticalOffset - end) < 1, "High-resolution wheel deltas lost");
            var inner = new ScrollViewer { Height = 100, Content = new Border { Height = 1000 } };
            var stack = new StackPanel(); stack.Children.Add(inner); stack.Children.Add(new Border { Height = 1800 });
            viewer.Content = stack; window.UpdateLayout(); Pump(30);
            var nested = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = inner };
            viewer.RaiseEvent(nested); Check(!nested.Handled, "Nested scroll viewer hijacked by page");
            Wheel(viewer, -120); window.Close(); Pump(30);
            Check(!SmoothScrollBehavior.IsAnimating(viewer), "Unloaded page animation retained");
            descriptor.RemoveValueChanged(viewer, changed);
            Console.WriteLine("PASS: real WPF wheel/intermediate movement/settle/reversal/manual cancel/reduced motion/logical scrolling/edge/unload; 120 Hz requested, no idle animation clock. Offset updates=" + settled + ". Physical display FPS not asserted.");
            Application.Current.Shutdown(); return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error); if(window != null) window.Close(); if(Application.Current != null) Application.Current.Shutdown(); return 1;
        }
    }
}

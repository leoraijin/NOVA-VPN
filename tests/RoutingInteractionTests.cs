using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NovaVpn;

internal static class RoutingInteractionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump(int ms) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static void Call(object control, string method, params object[] args) { control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, args); }
    private static ScrollViewer Find(DependencyObject node) { if (node is ScrollViewer) return (ScrollViewer)node; for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var found=Find(VisualTreeHelper.GetChild(node,i));if(found!=null)return found;}return null; }
    [STAThread] public static int Main()
    {
        Window window = null;
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var knob = new Ellipse { Width = 17, Height = 17, Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Center };
            var inner = new Grid(); inner.Children.Add(knob);
            var track = new Border { Width = 50, Height = 26, BorderThickness = new Thickness(1), Child = inner };
            int commits=0; string route=ApplicationRoutingService.Automatic;
            var toggle = new DragRouteSwitch(track, route, next => { route=next; commits++; });
            var list = new ListBox { Height=250 }; for(int i=0;i<60;i++) list.Items.Add(new ListBoxItem { Content="Synthetic application "+i, Height=50 });
            list.PreviewMouseWheel += (s,e) => SmoothScrollBehavior.ForwardToPage(list,e);
            var content = new StackPanel();content.Children.Add(toggle);content.Children.Add(list);content.Children.Add(new Border { Height=2000 });
            var page = new ScrollViewer { Content=content, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            bool motion=true;SmoothScrollBehavior.Attach(page,()=>motion);
            window = new Window { Content=page, Width=500, Height=420, Left=-32000, Top=-32000, ShowInTaskbar=false, ShowActivated=false };
            window.Show();window.UpdateLayout();Pump(30);
            Call(toggle,"Begin",10d);Call(toggle,"Finish",10d);Check(commits==0,"Click changed route");
            Call(toggle,"Begin",10d);Call(toggle,"Move",35d);Check(commits==0,"Drag committed before release");Call(toggle,"Finish",35d);Check(route==ApplicationRoutingService.Proxy&&commits==1,"Right drag failed");
            Call(toggle,"Begin",30d);Call(toggle,"Finish",7d);Check(route==ApplicationRoutingService.Direct&&commits==2,"Left drag failed");
            Call(toggle,"Begin",0d);Call(toggle,"Finish",11.5d);Check(route==ApplicationRoutingService.Automatic&&commits==3,"Center snap failed");
            Call(toggle,"Begin",0d);Call(toggle,"Move",30d);Call(toggle,"Cancel");Check(commits==3&&Math.Abs(((TranslateTransform)knob.RenderTransform).X)<.01,"Cancelled drag changed route");
            toggle.IsEnabled=false;Call(toggle,"Begin",0d);Call(toggle,"Finish",30d);Check(commits==3,"Disabled/Zapret-locked switch changed route");toggle.IsEnabled=true;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(toggle),Environment.TickCount,Key.Right) { RoutedEvent=Keyboard.PreviewKeyDownEvent };
            toggle.RaiseEvent(key);Check(key.Handled&&route==ApplicationRoutingService.Proxy&&commits==4,"Keyboard route selection failed");
            for(int wait=0;Keyboard.Modifiers!=ModifierKeys.None&&wait<5000;wait+=25)Pump(25);
            Check(Keyboard.Modifiers==ModifierKeys.None,"Release modifiers before plain-wheel fixture");
            var nested=Find(list);Check(nested!=null&&nested.ScrollableHeight>0,"Nested fixture not scrollable");
            foreach(bool enabled in new[]{true,false}) {
                motion=enabled;page.ScrollToVerticalOffset(0);nested.ScrollToVerticalOffset(0);window.UpdateLayout();Pump(30);
                var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) { RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=nested };
                nested.RaiseEvent(wheel);Pump(250);
                Check(wheel.Handled&&page.VerticalOffset>0&&nested.VerticalOffset==0,"Wheel trapped by app list; motion="+enabled);
            }
            double pageOffset=page.VerticalOffset;nested.LineDown();window.UpdateLayout();Pump(30);Check(nested.VerticalOffset>0&&page.VerticalOffset==pageOffset,"Native list navigation broken");
            window.Close();app.Shutdown();Console.WriteLine("PASS: click no-op, drag-only commit and three positions, cancelled/disabled drag, keyboard; nested-list wheel scrolls page with and without motion, list navigation retained. No real routes saved or VPN touched.");return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);if(window!=null)window.Close();if(Application.Current!=null)Application.Current.Shutdown();return 1;}
    }
}

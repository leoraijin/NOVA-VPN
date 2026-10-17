using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NovaVpn;

internal static class PageScrollPerformanceTests
{
    private sealed class Counter : Decorator {
        internal int Measures, Arranges;
        protected override Size MeasureOverride(Size s) { Measures++; return base.MeasureOverride(s); }
        protected override Size ArrangeOverride(Size s) { Arranges++; return base.ArrangeOverride(s); }
    }
    private static void Pump(int ms) { var f=new DispatcherFrame();var t=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) };t.Tick+=delegate {t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f); }
    private static ScrollViewer Find(DependencyObject o) { if(o is ScrollViewer s)return s;for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++){var r=Find(VisualTreeHelper.GetChild(o,i));if(r!=null)return r;}return null; }
    [STAThread] public static int Main(string[] args) {
        Application.ResourceAssembly=typeof(MainWindow).Assembly;
        new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var window=new MainWindow(true) { Left=-32000,Top=-32000,ShowActivated=false,ShowInTaskbar=false,Height=540 };
        try {
            window.Show();
            foreach(var page in new[]{"settings","routing","home"}) {
                window.ShowPageForSnapshot(page);window.UpdateLayout();Pump(100);
                var content=(DependencyObject)typeof(MainWindow).GetField("contentHost",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
                var viewer=Find(content);
                var stateProperty=(DependencyProperty)typeof(SmoothScrollBehavior).GetField("StateProperty",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
                var state=viewer.GetValue(stateProperty);
                state.GetType().GetField("motionAllowed",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(state,(Func<bool>)(()=>true));
                var original=(UIElement)viewer.Content;
                var counter=new Counter();
                bool transform=original.GetType().Name=="PixelScrollSurface";
                if(transform) { var host=(Decorator)((Decorator)original).Child;var child=host.Child;host.Child=null;counter.Child=child;host.Child=counter; }
                else {viewer.Content=null;counter.Child=original;viewer.Content=counter;}
                window.UpdateLayout();Pump(80);counter.Measures=counter.Arranges=0;
                var gaps=new List<double>();var clock=Stopwatch.StartNew();double previous=0;TimeSpan last=TimeSpan.MinValue;
                EventHandler render=(s,e)=>{var frame=(RenderingEventArgs)e;if(frame.RenderingTime==last)return;last=frame.RenderingTime;double now=clock.Elapsed.TotalMilliseconds;if(previous>0)gaps.Add(now-previous);previous=now;};
                CompositionTarget.Rendering+=render;
                try {
                    for(int i=0;i<50;i++) {
                        viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,i<25?-30:30){RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=viewer});
                        Pump(16);
                    }
                    Pump(300);
                } finally {CompositionTarget.Rendering-=render;}
                var sorted=gaps.OrderBy(x=>x).ToArray();
                Console.WriteLine(page+": surface="+transform+" pageMeasures="+counter.Measures+" pageArranges="+counter.Arranges+" frames="+gaps.Count+" p95CallbackMs="+(sorted.Length==0?0:sorted[(int)((sorted.Length-1)*.95)]).ToString("F1"));
                if(args.Contains("--require-transform")&&(!transform||counter.Arranges>3))throw new Exception("Page still relaid out during scrolling: "+page);
            }
            Console.WriteLine("PASS: real pages profiled with synthetic wheel input. Offscreen callback timings are not physical display FPS.");return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
        finally {window.Close();Application.Current.Shutdown();}
    }
}

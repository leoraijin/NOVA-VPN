using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NovaVpn;

public static class ConnectionMotionProbe
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int size, out int needed);
    [STAThread] public static void Main(string[] args)
    {
        bool precise=args.Contains("--timer"); if(precise) timeBeginPeriod(1);
        if(args.Contains("--software")) RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        int needed; var desktop = new StringBuilder(256);
        GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,desktop,512,out needed);
        Console.WriteLine("Desktop: " + desktop);
        var app = new Application();
        var button = new ConnectionControl();
        var window = new Window { Title="NOVA - connection animation probe", Width=600, Height=420, Left=80, Top=80, Topmost=false, ShowActivated=true, ShowInTaskbar=true, Content=button };
        window.Topmost = args.Contains("--topmost");
        var settings = new VisualPreferences { FollowSystemMotion=false, AnimationFrameRate=120 };
        settings.LoadThemeSettings(ThemePalette.IosLiquidGlass); settings.FollowSystemMotion=false;
        button.Apply(settings,CoreStatus.Connecting,true);
        if(args.Contains("--baseline")) {
            var transform=new RotateTransform();
            var rectangle=new System.Windows.Shapes.Rectangle {Width=80,Height=80,Fill=Brushes.Blue,RenderTransformOrigin=new Point(.5,.5),RenderTransform=transform};
            var animation=new System.Windows.Media.Animation.DoubleAnimation(0,360,TimeSpan.FromSeconds(1)) {RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever};
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animation,120);
            transform.BeginAnimation(RotateTransform.AngleProperty,animation);
            window.Content=rectangle;
        }
        var intervals = new List<double>(); var watch = new Stopwatch(); double last=0;
        TimeSpan lastFrame = TimeSpan.MinValue;
        EventHandler render = delegate(object sender, EventArgs eventArgs) {
            var frame=(RenderingEventArgs)eventArgs;
            if(frame.RenderingTime==lastFrame) return;
            lastFrame=frame.RenderingTime;
            double now=watch.Elapsed.TotalMilliseconds;
            if(now>300 && last>300) intervals.Add(now-last);
            last=now;
        };
        var timer = new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) };
        timer.Tick += delegate {
            timer.Stop(); CompositionTarget.Rendering-=render;
            var media=typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext");
            var context=media.GetProperty("CurrentMediaContext",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null,null);
            foreach(var name in new[] {"_animationRenderRate","_lastPresentationResults","_interlockState"}) {
                var field=media.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
                if(field!=null) Console.WriteLine(name+": "+field.GetValue(context));
            }
            Console.WriteLine("Visible: "+window.IsVisible+" Loaded: "+button.IsLoaded+" Active: "+window.IsActive+" Position: "+window.Left+","+window.Top);
            if(intervals.Count>0) {
                var ordered=intervals.OrderBy(x=>x).ToArray();
                Console.WriteLine("Samples: " + intervals.Count);
                Console.WriteLine("CadenceFPS: " + (1000/intervals.Average()).ToString("F2"));
                Console.WriteLine("P95ms: " + ordered[(int)((ordered.Length-1)*.95)].ToString("F2"));
            }
            app.Shutdown();
        };
        window.Loaded += delegate { watch.Start(); CompositionTarget.Rendering+=render; timer.Start(); };
        try { app.Run(window); } finally { if(precise) timeEndPeriod(1); }
    }
}

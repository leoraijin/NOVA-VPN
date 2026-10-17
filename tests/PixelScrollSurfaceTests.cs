using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NovaVpn;

internal static class PixelScrollSurfaceTests
{
    static void Check(bool b,string m){if(!b)throw new Exception(m);}
    static void Pump(){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};t.Tick+=delegate{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
    [STAThread] static int Main(){
        new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var page=new StackPanel();var top=new Button{Content="top",Height=40};var bottom=new TextBox{Text="bottom",Height=40};
        page.Children.Add(top);page.Children.Add(new Border{Height=1500});page.Children.Add(bottom);
        var viewer=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        SmoothScrollBehavior.Attach(viewer,()=>true);
        var window=new Window{Content=viewer,Width=400,Height=300,Left=-32000,Top=-32000,ShowInTaskbar=false,ShowActivated=false};
        try{
            window.Show();window.UpdateLayout();Pump();
            var surface=viewer.Content as PixelScrollSurface;Check(surface!=null,"No surface");
            Check(viewer.ScrollableHeight>1000,"Extent lost");
            viewer.ScrollToVerticalOffset(200);Pump();
            Check(Math.Abs(viewer.VerticalOffset-200)<1,"Scrollbar offset not applied");
            Check(Math.Abs(top.TransformToAncestor(surface).Transform(new Point()).Y+200)<1,"Visual offset mismatch");
            surface.SetVerticalOffset(double.PositiveInfinity);Pump();Check(Math.Abs(surface.VerticalOffset-viewer.ScrollableHeight)<1,"Bottom clamp");
            var bounds=surface.MakeVisible(top,new Rect(top.RenderSize));Pump();Check(surface.VerticalOffset==0&&!bounds.IsEmpty,"Keyboard bring top into view");
            bounds=surface.MakeVisible(bottom,new Rect(bottom.RenderSize));Pump();Check(bounds.Bottom<=surface.ViewportHeight+.1&&!bounds.IsEmpty,"Bring bottom into view");
            window.Height=600;window.UpdateLayout();Pump();Check(surface.VerticalOffset<=viewer.ScrollableHeight+.1,"Resize clamp");
            page.Children.RemoveAt(1);window.UpdateLayout();Pump();Check(surface.VerticalOffset==0,"Content shrink clamp");
            viewer.Content=new Border{Height=2000};window.UpdateLayout();Pump();Check(viewer.Content is PixelScrollSurface&&viewer.ScrollableHeight>1000,"Content replacement");
            window.Content=null;Pump();window.Content=viewer;window.UpdateLayout();Pump();Check(viewer.Content is PixelScrollSurface,"Reload");
            Console.WriteLine("PASS: transform position, scrollbar, bounds, bring-into-view, resize, shrink, content replacement, unload/reload.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{window.Close();Application.Current.Shutdown();}
    }
}

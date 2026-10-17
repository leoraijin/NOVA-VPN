using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NovaVpn;

/// <summary>Mouse clicks do not change routes. Commit only a completed horizontal drag.</summary>
public sealed class DragRouteSwitch : Button
{
    private readonly Border track;
    private readonly FrameworkElement knob;
    private readonly Action<string> changed;
    private readonly TranslateTransform translation = new TranslateTransform();
    private string route;
    private bool pressed, moved;
    private double origin, fraction;
    public DragRouteSwitch(Border track, string route, Action<string> changed)
    {
        this.track = track; this.route = route; this.changed = changed;
        knob = (FrameworkElement)((Panel)track.Child).Children[0];
        knob.RenderTransform = translation;
        Content = track;
        Unloaded += delegate { Cancel(); };
        IsEnabledChanged += delegate { if (!IsEnabled) Cancel(); };
    }
    private static double Fraction(string route) => route == ApplicationRoutingService.Direct ? 0 : route == ApplicationRoutingService.Proxy ? 1 : .5;
    public static string RouteAt(double value) => value < .25 ? ApplicationRoutingService.Direct : value > .75 ? ApplicationRoutingService.Proxy : ApplicationRoutingService.Automatic;
    private double Travel => Math.Max(1, track.Width - track.BorderThickness.Left - track.BorderThickness.Right - knob.Width - knob.Margin.Left - knob.Margin.Right);
    private void Begin(double x) { if (!IsEnabled) return; origin = x; fraction = Fraction(route); pressed = true; moved = false; }
    private void Move(double x)
    {
        if (!pressed) return;
        if (Math.Abs(x - origin) < SystemParameters.MinimumHorizontalDragDistance && !moved) return;
        moved = true;
        fraction = Math.Max(0, Math.Min(1, Fraction(route) + (x - origin) / Travel));
        translation.X = (fraction - Fraction(route)) * Travel;
    }
    private void Finish(double x)
    {
        if (!pressed) return;
        Move(x); bool commit = moved; string next = RouteAt(fraction);
        Cancel(); if (commit) Commit(next);
    }
    private void Cancel() { pressed = moved = false; translation.X = 0; if (IsMouseCaptured) ReleaseMouseCapture(); }
    private void Commit(string next)
    {
        if (!IsEnabled || next == route) return;
        route = next;
        knob.HorizontalAlignment = next == ApplicationRoutingService.Direct ? HorizontalAlignment.Left : next == ApplicationRoutingService.Proxy ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        changed?.Invoke(next);
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsEnabled) { Focus(); Begin(e.GetPosition(this).X); if (!CaptureMouse()) Cancel(); e.Handled = true; }
        base.OnPreviewMouseLeftButtonDown(e);
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (pressed) { if (e.LeftButton == MouseButtonState.Pressed) Move(e.GetPosition(this).X); else Cancel(); e.Handled = true; }
        base.OnPreviewMouseMove(e);
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (pressed) { Finish(e.GetPosition(this).X); e.Handled = true; }
        base.OnPreviewMouseLeftButtonUp(e);
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { Cancel(); base.OnLostMouseCapture(e); }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Home || e.Key == Key.End || e.Key == Key.Space) {
            Cancel(); double next = e.Key == Key.Home ? 0 : e.Key == Key.End ? 1 : e.Key == Key.Space ? (Fraction(route) == 1 ? 0 : Fraction(route) + .5) : Math.Max(0, Math.Min(1, Fraction(route) + (e.Key == Key.Left ? -.5 : .5)));
            Commit(RouteAt(next)); e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
    // Accessibility Invoke remains available; mouse down/up are consumed above.
    protected override void OnClick() { Commit(RouteAt(Fraction(route) == 1 ? 0 : Fraction(route) + .5)); }
}

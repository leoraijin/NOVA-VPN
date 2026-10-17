using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NovaVpn;

// Physical scrolling without arranging the entire page for every pixel offset.
// The inner decorator owns the translation so page/hover transforms are preserved.
public sealed class PixelScrollSurface : Decorator, IScrollInfo
{
    private readonly TranslateTransform translation = new TranslateTransform();
    private Size extent, viewport;
    private double x, y;
    public PixelScrollSurface(UIElement content)
    {
        ClipToBounds = true;
        Child = new Decorator { Child = content, RenderTransform = translation };
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => x;
    public double VerticalOffset => y;
    public ScrollViewer ScrollOwner { get; set; }
    protected override Size MeasureOverride(Size available)
    {
        Child.Measure(new Size(CanHorizontallyScroll ? double.PositiveInfinity : available.Width,
            CanVerticallyScroll ? double.PositiveInfinity : available.Height));
        extent = Child.DesiredSize;
        viewport = new Size(double.IsInfinity(available.Width) ? extent.Width : available.Width,
            double.IsInfinity(available.Height) ? extent.Height : available.Height);
        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();
        return new Size(Math.Min(extent.Width, viewport.Width), Math.Min(extent.Height, viewport.Height));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        viewport = finalSize;
        Child.Arrange(new Rect(0, 0, Math.Max(extent.Width, finalSize.Width), Math.Max(extent.Height, finalSize.Height)));
        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();
        return finalSize;
    }
    private void CoerceOffsets()
    {
        x = Math.Max(0, Math.Min(x, Math.Max(0, extent.Width - viewport.Width)));
        y = Math.Max(0, Math.Min(y, Math.Max(0, extent.Height - viewport.Height)));
        translation.X = -x; translation.Y = -y;
    }
    public void SetHorizontalOffset(double value) { if (double.IsNaN(value)) return; x = value; CoerceOffsets(); ScrollOwner?.InvalidateScrollInfo(); }
    public void SetVerticalOffset(double value) { if (double.IsNaN(value)) return; y = value; CoerceOffsets(); ScrollOwner?.InvalidateScrollInfo(); }
    public void LineUp() => SetVerticalOffset(y - 16);
    public void LineDown() => SetVerticalOffset(y + 16);
    public void LineLeft() => SetHorizontalOffset(x - 16);
    public void LineRight() => SetHorizontalOffset(x + 16);
    public void MouseWheelUp() => SetVerticalOffset(y - WheelStep());
    public void MouseWheelDown() => SetVerticalOffset(y + WheelStep());
    public void MouseWheelLeft() => SetHorizontalOffset(x - 48);
    public void MouseWheelRight() => SetHorizontalOffset(x + 48);
    private double WheelStep() => SystemParameters.WheelScrollLines < 0 ? viewport.Height : SystemParameters.WheelScrollLines * 16;
    public void PageUp() => SetVerticalOffset(y - viewport.Height);
    public void PageDown() => SetVerticalOffset(y + viewport.Height);
    public void PageLeft() => SetHorizontalOffset(x - viewport.Width);
    public void PageRight() => SetHorizontalOffset(x + viewport.Width);
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual == null || rectangle.IsEmpty || !IsAncestorOf(visual)) return Rect.Empty;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        if (bounds.Top < 0) SetVerticalOffset(y + bounds.Top);
        else if (bounds.Bottom > viewport.Height) SetVerticalOffset(y + Math.Min(bounds.Top, bounds.Bottom - viewport.Height));
        if (bounds.Left < 0) SetHorizontalOffset(x + bounds.Left);
        else if (bounds.Right > viewport.Width) SetHorizontalOffset(x + Math.Min(bounds.Left, bounds.Right - viewport.Width));
        bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        bounds.Intersect(new Rect(viewport));
        return bounds;
    }
}

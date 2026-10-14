using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NovaVpn;

// Transform the existing vector map instead of rebuilding its geometry on mouse movement.
public sealed class ServerMapViewport : Border
{
	private readonly MatrixTransform transform = new MatrixTransform();
	private Point press, originalOffset;
	private bool pressed, dragging;
	public double Zoom { get; private set; } = 1;
	public Point Offset { get; private set; }
	public bool IsDragging => dragging;

	public ServerMapViewport(InteractiveServerMap map)
	{
		Width = map.Width; Height = map.Height; ClipToBounds = true;
		Background = Brushes.Transparent; Child = map; map.RenderTransform = transform;
		Tag = "server-map-viewport";
		Unloaded += (s, e) => EndDrag();
	}

	public void ZoomAt(Point anchor, double factor)
	{
		if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return;
		double next = Math.Max(1, Math.Min(12, Zoom * factor));
		double ratio = next / Zoom;
		Offset = new Point(anchor.X - (anchor.X - Offset.X) * ratio, anchor.Y - (anchor.Y - Offset.Y) * ratio);
		Zoom = next; ApplyTransform();
	}

	public void PanBy(Vector delta)
	{
		Offset = new Point(Offset.X + delta.X, Offset.Y + delta.Y); ApplyTransform();
	}

	public void ResetView() { EndDrag(); Zoom = 1; Offset = new Point(); ApplyTransform(); }
	private void ApplyTransform()
	{
		Offset = new Point(Math.Max(Width * (1 - Zoom), Math.Min(0, Offset.X)), Math.Max(Height * (1 - Zoom), Math.Min(0, Offset.Y)));
		transform.Matrix = new Matrix(Zoom, 0, 0, Zoom, Offset.X, Offset.Y);
		((InteractiveServerMap)Child).SetMarkerScale(Zoom);
	}

	protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) {
			EndDrag(); ZoomAt(e.GetPosition(this), Math.Pow(1.2, e.Delta / 120.0)); e.Handled = true;
		}
		base.OnPreviewMouseWheel(e);
	}
	protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		press = e.GetPosition(this); originalOffset = Offset; pressed = true;
		base.OnPreviewMouseLeftButtonDown(e);
	}
	protected override void OnPreviewMouseMove(MouseEventArgs e)
	{
		if (pressed && e.LeftButton == MouseButtonState.Pressed) {
			Vector delta = e.GetPosition(this) - press;
			if (!dragging && (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)) {
				dragging = CaptureMouse(); if (dragging) Cursor = Cursors.SizeAll;
			}
			if (dragging) { Offset = originalOffset; PanBy(delta); e.Handled = true; }
		} else if (pressed) EndDrag();
		base.OnPreviewMouseMove(e);
	}
	protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		if (dragging) e.Handled = true; // Never select a country/server after panning.
		EndDrag(); base.OnPreviewMouseLeftButtonUp(e);
	}
	protected override void OnLostMouseCapture(MouseEventArgs e) { EndDrag(); base.OnLostMouseCapture(e); }
	private void EndDrag()
	{
		pressed = dragging = false; ClearValue(CursorProperty);
		if (IsMouseCaptured) ReleaseMouseCapture();
	}
}

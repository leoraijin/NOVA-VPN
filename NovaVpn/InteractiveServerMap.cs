using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NovaVpn;

public sealed class InteractiveServerMap : Canvas
{
	private sealed class Node
    {
        public VpnProfile[] Profiles;
        public Ellipse Dot, Halo;
        public int MarkerStatus = -1;
    }
	private readonly List<Node> nodes = new List<Node>();
	private readonly Dictionary<string, ServerMapHealth> health;
	private readonly ThemePalette palette;
	private readonly Action<VpnProfile> select;
	private readonly string selectedId;
	private double markerZoom = 1;

	public InteractiveServerMap(ThemePalette palette, Dictionary<string, ServerMapHealth> health, string selectedId, Action<VpnProfile> select)
	{
		this.palette = palette; this.health = health; this.selectedId = selectedId; this.select = select;
		ClipToBounds = true; Background = Brushes.Transparent; Tag = "interactive-server-map";
	}

	public void Choose(FrameworkElement anchor, VpnProfile[] profiles)
	{
		if (profiles.Length == 0) return;
		if (profiles.Length == 1) { select(profiles[0]); return; }
		var menu = new ContextMenu { Background = new SolidColorBrush(palette.Surface), Foreground = new SolidColorBrush(palette.Text) };
		foreach (var profile in profiles.OrderBy(p => Score(p)))
		{
			var captured = profile;
			var item = new MenuItem { Header = (profile.Id == selectedId ? "✓ " : "") + profile.Name + " · " + Status(profile) };
			item.Click += (s, e) => select(captured);
			menu.Items.Add(item);
		}
		menu.PlacementTarget = anchor; menu.IsOpen = true;
	}

	public string Summary(VpnProfile[] profiles)
	{
		return profiles.Length == 0 ? "Нет профилей в этой стране" : string.Join("\n", profiles.OrderBy(p => Score(p)).Take(6).Select(p => p.Name + " · " + Status(p)))
			+ (profiles.Length > 6 ? "\nЕщё " + (profiles.Length - 6) + " · нажмите для выбора" : "\nНажмите для выбора");
	}
	private string Status(VpnProfile p) => health.TryGetValue(p.Id, out var value) && value.IsFreshFor(p) ? value.Label : "Нужна свежая проверка";
	private int Score(VpnProfile p) => health.TryGetValue(p.Id, out var value) && value.Supported && value.IsFreshFor(p) ? value.Success ? value.LatencyMs : int.MaxValue : int.MaxValue - 1;

	public void AddServerNode(Point point, string country, VpnProfile[] profiles, Action<bool> highlight)
	{
		if (point.X < 0 || point.Y < 0 || point.X > Width || point.Y > Height) return;
		var halo = new Ellipse { Width = 26, Height = 26, IsHitTestVisible = false };
		var dot = new Ellipse { Width = 16, Height = 16, StrokeThickness = 2, Cursor = Cursors.Hand, Tag = "server-node" };
		SetLeft(halo, point.X - 13); SetTop(halo, point.Y - 13); SetZIndex(halo, 20);
		SetLeft(dot, point.X - 8); SetTop(dot, point.Y - 8); SetZIndex(dot, 21);
		System.Windows.Automation.AutomationProperties.SetName(dot, country + " · выбор VPN-сервера");
        var node = new Node { Profiles = profiles, Dot = dot, Halo = halo };
        dot.Stroke = new SolidColorBrush(profiles.Any(p => p.Id == selectedId) ? palette.Accent : palette.Surface);
        dot.ToolTip = country;
        dot.ToolTipOpening += (s, e) => dot.ToolTip = country +
            (node.MarkerStatus == 3 ? " · самый быстрый из проверенных" : "") + "\n" +
            Summary(profiles) + "\nКоординаты по названию профиля";
        dot.MouseEnter += (s, e) => highlight(true);
		dot.MouseLeave += (s, e) => highlight(false);
		dot.MouseLeftButtonUp += (s, e) => { e.Handled = true; Choose(dot, profiles); };
		Children.Add(halo); Children.Add(dot);
		nodes.Add(node);
	}

	public void RefreshStatus()
	{
        var status = new Dictionary<string, ServerMapHealth>(StringComparer.Ordinal);
        var rows = nodes.Select(node =>
        {
            int latency = int.MaxValue;
            bool failed = node.Profiles.Length != 0;
            foreach (var profile in node.Profiles)
            {
                if (!status.TryGetValue(profile.Id, out var result))
                {
                    result = health.TryGetValue(profile.Id, out var found) && found.Supported && found.IsFreshFor(profile)
                        ? found : null;
                    status.Add(profile.Id, result);
                }
                if (result?.Success == true) latency = Math.Min(latency, result.LatencyMs);
                if (result?.Success != false) failed = false;
            }
            return new { node, latency, failed };
        }).ToArray();
        int best = rows.Select(row => row.latency).DefaultIfEmpty(int.MaxValue).Min();
        foreach (var row in rows)
        {
            bool verified = row.latency < int.MaxValue;
            bool fastest = verified && row.latency == best;
            int markerStatus = fastest ? 3 : verified ? 2 : row.failed ? 1 : 0;
            if (row.node.MarkerStatus == markerStatus) continue;
            row.node.MarkerStatus = markerStatus;
            Color color = verified ? Color.FromRgb(45, 174, 112) : row.failed ? Color.FromRgb(224, 76, 83) : palette.Muted;
            row.node.Dot.Fill = new SolidColorBrush(color);
            row.node.Dot.Opacity = verified || row.failed ? 1 : 0.55;
            row.node.Halo.Fill = new SolidColorBrush(Color.FromArgb(
                (byte)(fastest ? 105 : verified ? 55 : row.failed ? 45 : 0), color.R, color.G, color.B));
        }
    }

	public void SetMarkerScale(double zoom)
	{
		if (markerZoom == zoom) return;
		markerZoom = zoom;
		foreach (var node in nodes) {
			var dot = node.Dot.RenderTransform as ScaleTransform;
			if (dot == null) node.Dot.RenderTransform = dot = new ScaleTransform(1, 1, 8, 8);
			dot.ScaleX = dot.ScaleY = 1 / zoom;
			var halo = node.Halo.RenderTransform as ScaleTransform;
			if (halo == null) node.Halo.RenderTransform = halo = new ScaleTransform(1, 1, 13, 13);
			halo.ScaleX = halo.ScaleY = 1 / zoom;
		}
	}
}

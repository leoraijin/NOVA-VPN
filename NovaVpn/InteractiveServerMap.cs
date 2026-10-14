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
	private sealed class Node { public VpnProfile[] Profiles; public Ellipse Dot, Halo; public string Country; }
	private readonly List<Node> nodes = new List<Node>();
	private readonly Dictionary<string, ServerMapHealth> health;
	private readonly ThemePalette palette;
	private readonly Action<VpnProfile> select;
	private readonly string selectedId;

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
	private string Status(VpnProfile p) => health.TryGetValue(p.Id, out var value) ? value.Label : "Не проверен";
	private int Score(VpnProfile p) => health.TryGetValue(p.Id, out var value) && value.Supported && value.IsFresh ? value.Success ? value.LatencyMs : int.MaxValue : int.MaxValue - 1;

	public void AddServerNode(Point point, string country, VpnProfile[] profiles, Action<bool> highlight)
	{
		if (point.X < 0 || point.Y < 0 || point.X > Width || point.Y > Height) return;
		var halo = new Ellipse { Width = 26, Height = 26, IsHitTestVisible = false };
		var dot = new Ellipse { Width = 16, Height = 16, StrokeThickness = 2, Cursor = Cursors.Hand, Tag = "server-node" };
		SetLeft(halo, point.X - 13); SetTop(halo, point.Y - 13); SetZIndex(halo, 20);
		SetLeft(dot, point.X - 8); SetTop(dot, point.Y - 8); SetZIndex(dot, 21);
		System.Windows.Automation.AutomationProperties.SetName(dot, country + " · выбор VPN-сервера");
		dot.MouseEnter += (s, e) => { highlight(true); dot.ToolTip = Summary(profiles) + "\nКоординаты по названию профиля"; };
		dot.MouseLeave += (s, e) => highlight(false);
		dot.MouseLeftButtonUp += (s, e) => { e.Handled = true; Choose(dot, profiles); };
		Children.Add(halo); Children.Add(dot);
		nodes.Add(new Node { Profiles = profiles, Dot = dot, Halo = halo, Country = country });
	}

	public void RefreshStatus()
	{
		int best = nodes.SelectMany(n => n.Profiles).Where(p => health.TryGetValue(p.Id, out var h) && h.Supported && h.IsFresh && h.Success).Select(Score).DefaultIfEmpty(int.MaxValue).Min();
		foreach (var node in nodes)
		{
			int latency = node.Profiles.Where(p => health.TryGetValue(p.Id, out var h) && h.Supported && h.IsFresh && h.Success).Select(Score).DefaultIfEmpty(int.MaxValue).Min();
			bool verified = latency < int.MaxValue;
			bool active = node.Profiles.Any(p => p.Id == selectedId);
			bool failed = node.Profiles.All(p => health.TryGetValue(p.Id, out var value) && value.Supported && value.IsFresh && !value.Success);
			Color color = verified ? Color.FromRgb(45, 174, 112) : failed ? Color.FromRgb(224, 76, 83) : palette.Muted;
			node.Dot.Fill = new SolidColorBrush(color);
			node.Dot.Stroke = new SolidColorBrush(active ? palette.Accent : palette.Surface);
			node.Dot.Opacity = verified || failed ? 1 : 0.55;
			node.Halo.Fill = new SolidColorBrush(Color.FromArgb((byte)(verified ? (latency == best ? 105 : 55) : failed ? 45 : 0), color.R, color.G, color.B));
			node.Dot.ToolTip = node.Country + (verified && latency == best ? " · самый быстрый из проверенных" : "") + "\n" + Summary(node.Profiles) + "\nКоординаты по названию профиля";
		}
	}
}

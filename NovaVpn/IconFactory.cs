using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NovaVpn;

public static class IconFactory
{
	private static readonly Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ "home", "M3,10.5 L12,3 L21,10.5 M5.5,9.5 L5.5,21 L18.5,21 L18.5,9.5 M9.5,21 L9.5,14 L14.5,14 L14.5,21" },
		{ "servers", "M12,2 L21,7 L12,12 L3,7 Z M3,12 L12,17 L21,12 M3,17 L12,22 L21,17" },
		{ "routing", "M5,4 L5,20 M5,8 C11,8 13,6 13,3 M5,15 C11,15 15,18 20,18 M17,15 L20,18 L17,21" },
		{ "diagnostics", "M3,12 L7,12 L10,19 L15,5 L18,12 L22,12" },
		{ "settings", "M12,8.3 A3.7,3.7 0 1 0 12,15.7 A3.7,3.7 0 1 0 12,8.3 M12,2 L13.2,5.1 L16.2,6.2 L19.2,4.8 L21.2,8.2 L18.7,10.4 L18.7,13.6 L21.2,15.8 L19.2,19.2 L16.2,17.8 L13.2,18.9 L12,22 L10.8,18.9 L7.8,17.8 L4.8,19.2 L2.8,15.8 L5.3,13.6 L5.3,10.4 L2.8,8.2 L4.8,4.8 L7.8,6.2 L10.8,5.1 Z" },
		{ "power", "M12,2 L12,12 M6.2,5.5 A8,8 0 1 0 17.8,5.5" },
		{ "star", "M12,2.5 L15,8.5 L21.7,9.5 L16.8,14.1 L18,20.7 L12,17.5 L6,20.7 L7.2,14.1 L2.3,9.5 L9,8.5 Z" },
		{ "edit", "M4,20 L8,19 L19,8 L16,5 L5,16 Z M14.5,6.5 L17.5,9.5" },
		{ "trash", "M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,21 L17,21 L18,6 M10,10 L10,17 M14,10 L14,17" },
		{ "plus", "M12,4 L12,20 M4,12 L20,12" },
		{ "check", "M3,12 L9,18 L21,5" },
		{ "shield", "M12,2 L20,5 L19,13 C18,18 15,21 12,22 C9,21 6,18 5,13 L4,5 Z M8,12 L11,15 L16,9" },
		{ "globe", "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M2,12 L22,12 M12,2 C8,7 8,17 12,22 M12,2 C16,7 16,17 12,22" },
		{ "speed", "M4,18 A9,9 0 1 1 20,18 M12,15 L17,9" },
		{ "palette", "M12,3 A9,9 0 1 0 12,21 C14,21 14,18 12,18 L10,18 C8,18 8,15 10,15 L15,15 C19,15 21,12 21,9 C21,5 17,3 12,3 Z" },
		{ "grid", "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z" },
		{ "list", "M4,6 L7,6 M10,6 L21,6 M4,12 L7,12 M10,12 L21,12 M4,18 L7,18 M10,18 L21,18" }
	};

	public static Viewbox Create(string name, double size, Color color, bool filled = false)
	{
		Path path = new Path { Data = Geometry.Parse(Data.ContainsKey(name) ? Data[name] : Data["shield"]), Stretch = Stretch.Uniform };
		if (filled) path.Fill = new SolidColorBrush(color);
		else { path.Stroke = new SolidColorBrush(color); path.StrokeThickness = 1.7; path.StrokeStartLineCap = PenLineCap.Round; path.StrokeEndLineCap = PenLineCap.Round; path.StrokeLineJoin = PenLineJoin.Round; }
		return new Viewbox { Width = size, Height = size, Child = path, Stretch = Stretch.Uniform };
	}
}

using System;
using System.Windows.Media;

namespace NovaVpn;

public sealed class ThemePalette
{
	public const string IosLiquidGlass = "iOS 27 · Liquid Glass";
	public const string AmberGlass = "Amber Glass";
	public const string OneUi = "One UI 9";
	public const string MaterialYou = "Material You";
	public const string Windows = "Windows 11";
	public const string WindowsLight = "Windows 11 Light";
	public const string WindowsDark = "Windows 11 Dark";

	public string Name;
	public bool IsLight;
	public bool IsGlass;
	public Color Bg;
	public Color Sidebar;
	public Color Surface;
	public Color Surface2;
	public Color Border;
	public Color Text;
	public Color Muted;
	public Color Accent;
	public Color AccentLight;
	public Color AccentForeground;
	public Color Teal;
	public Color Success;
	public Color Danger;
	public Color Warning;
	public Color Input;
	public Color SoftSelection;
	public Color BackdropStart;
	public Color BackdropMiddle;
	public Color BackdropEnd;

	public static readonly string[] ThemeNames = { IosLiquidGlass, MaterialYou, OneUi, Windows };

	public static string NormalizeName(string name)
	{
		string value = (name ?? "").Trim();
		if (value.Equals(MaterialYou, StringComparison.OrdinalIgnoreCase)) return MaterialYou;
		if (value.Equals(IosLiquidGlass, StringComparison.OrdinalIgnoreCase) || value.Equals("iOS 27", StringComparison.OrdinalIgnoreCase) || value.Equals("Liquid Glass", StringComparison.OrdinalIgnoreCase) || value.Equals("iOS Liquid Glass", StringComparison.OrdinalIgnoreCase)) return IosLiquidGlass;
		if (value.Equals(AmberGlass, StringComparison.OrdinalIgnoreCase) || value.Equals("Warm Glass", StringComparison.OrdinalIgnoreCase) || value.Equals("Тёплое стекло", StringComparison.OrdinalIgnoreCase) || value.Equals("Янтарное стекло", StringComparison.OrdinalIgnoreCase)) return IosLiquidGlass;
		if (value.Equals(OneUi, StringComparison.OrdinalIgnoreCase) || value.Equals("One UI", StringComparison.OrdinalIgnoreCase) || value.Equals("One UI 8.0", StringComparison.OrdinalIgnoreCase) || value.Equals("One UI 8", StringComparison.OrdinalIgnoreCase)) return OneUi;
		if (value.Equals(WindowsDark, StringComparison.OrdinalIgnoreCase) || value.Equals("Dark", StringComparison.OrdinalIgnoreCase) || value.Equals("NOVA Dark", StringComparison.OrdinalIgnoreCase) || value.Equals("OLED Black", StringComparison.OrdinalIgnoreCase)) return WindowsDark;
		return Windows;
	}

	public static ThemePalette Create(VisualPreferences preferences)
	{
		string name = NormalizeName(preferences?.ThemeName);
		bool dark = preferences?.DarkMode == true || name == WindowsDark;
        ThemePalette palette = name == IosLiquidGlass ? LiquidGlass() : name == OneUi ? SamsungOneUi() : dark ? Dark() : Light();
        palette.Name = name == WindowsDark ? Windows : name;
        if (dark && name != Windows && name != WindowsDark) {
            ThemePalette d = Dark();
            palette.IsLight = false; palette.Bg = d.Bg; palette.Sidebar = d.Sidebar;
            palette.Surface = d.Surface; palette.Surface2 = d.Surface2; palette.Border = d.Border;
            palette.Text = d.Text; palette.Muted = d.Muted; palette.Input = d.Input;
            palette.Success = d.Success; palette.Danger = d.Danger; palette.Warning = d.Warning; palette.Teal = d.Teal;
            palette.BackdropStart = Parse("#102538"); palette.BackdropMiddle = Parse("#171C2B"); palette.BackdropEnd = Parse("#202537");
            palette.Accent = Parse("#80B8FF"); palette.AccentLight = palette.Accent;
            palette.SoftSelection = Parse("#263B53");
        }
        if (name == OneUi) {
            palette.Bg = Parse(dark ? "#080808" : "#F2F2F2");
            palette.Sidebar = palette.Bg; palette.Surface = Parse(dark ? "#191919" : "#FFFFFF");
            palette.Surface2 = Parse(dark ? "#252525" : "#F7F7F7");
            palette.Text = Parse(dark ? "#F5F5F5" : "#171717");
            palette.Muted = Parse(dark ? "#B5B5B5" : "#666666");
            palette.Accent = Parse(dark ? "#90B8FF" : "#1769E0");
            palette.SoftSelection = Parse(dark ? "#223452" : "#E3EDFF");
            palette.AccentLight = palette.Accent;
            palette.Border = Parse(dark ? "#333333" : "#E2E2E2");
            palette.Input = palette.Surface; palette.BackdropStart = palette.BackdropMiddle = palette.BackdropEnd = palette.Bg;
        }
        if (name == MaterialYou) ApplyMaterial(palette, preferences?.MaterialHue ?? 275, dark);
		if (preferences != null)
		{
			// Colors are owned by the selected stock theme; legacy/imported overrides are ignored.
			double opacity = Clamp(preferences.SurfaceOpacity, 0.78, 1.0);
			if (palette.IsGlass) opacity *= 0.06 + 0.90 * Clamp(preferences.GlassFrost, 0, 1);
			palette.Sidebar = WithOpacity(palette.Sidebar, opacity);
			palette.Surface = WithOpacity(palette.Surface, opacity);
			palette.Surface2 = WithOpacity(palette.Surface2, opacity);
			palette.Input = WithOpacity(palette.Input, opacity);
		}
		palette.AccentForeground = ReadableForeground(palette.Accent);
		double softContrast = Clamp(preferences?.SoftContrast ?? 0, 0, 1);
		palette.Muted = Blend(palette.Muted, palette.Text, softContrast * .55);
		palette.Border = Blend(palette.Border, palette.Text, softContrast * .28);
		if (preferences?.HighContrast == true)
		{
			palette.Border = palette.Text;
			palette.Muted = palette.Text;
			palette.Surface = WithOpacity(palette.Surface, 1);
			palette.Surface2 = WithOpacity(palette.Surface2, 1);
			palette.Sidebar = WithOpacity(palette.Sidebar, 1);
		}
		return palette;
	}

	public static Color ReadableForeground(Color background) => ContrastRatio(background, Colors.White) >= 4.5 ? Colors.White : Colors.Black;

	public Color Map(string hex)
	{
		switch ((hex ?? "").ToUpperInvariant())
		{
			case "#0A0C11": case "#0E1117": case "#0F1219": case "#10131A": return Input;
			case "#151923": case "#171B27": case "#121620": return Surface;
			case "#1A1F2B": case "#1C212C": case "#212632": case "#1F3044": case "#211E37": case "#29243D": case "#1C2030": return Surface2;
			case "#262C3A": case "#242A37": case "#242A36": return Border;
			case "#F5F7FB": case "#CBD1DC": case "#BBC2CF": case "#9CA6B7": return Text;
			case "#929BAD": case "#8E96A7": case "#687184": case "#6F788A": case "#626A7A": return Muted;
			case "#7C5CFC": case "#6952E5": case "#6B5AE0": case "#514A80": return Accent;
			case "#9B84FF": return AccentLight;
			case "#35D5C5": return Teal;
			case "#4CE0A6": return Success;
			case "#FF687A": case "#C83B4D": return Danger;
			case "#FFC766": return Warning;
			case "#1B1D31": case "#1C1D30": case "#2D2850": case "#202233": case "#2A2350": return SoftSelection;
			default: return Parse(hex);
		}
	}

	public static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

	public static bool TryParseColor(string value, out Color color)
	{
		color = Colors.Transparent;
		if (string.IsNullOrWhiteSpace(value)) return false;
		try
		{
			color = Parse(value.Trim());
			return true;
		}
		catch { return false; }
	}

	public static Color Blend(Color first, Color second, double amount)
	{
		amount = Clamp(amount, 0.0, 1.0);
		return Color.FromArgb(
			(byte)(first.A + (second.A - first.A) * amount),
			(byte)(first.R + (second.R - first.R) * amount),
			(byte)(first.G + (second.G - first.G) * amount),
			(byte)(first.B + (second.B - first.B) * amount));
	}

	private static ThemePalette Light() => new ThemePalette
	{
		Name = WindowsLight, IsLight = true, IsGlass = false,
		Bg = Parse("#F0F0F0"), Sidebar = Parse("#E8E8E8"), Surface = Parse("#FFFFFF"), Surface2 = Parse("#F5F5F5"),
		Border = Parse("#CCCCCC"), Text = Parse("#202020"), Muted = Parse("#5C5C5C"), Accent = Parse("#0067C0"), AccentLight = Parse("#1975C5"),
		Teal = Parse("#16827D"), Success = Parse("#167B5E"), Danger = Parse("#B23B52"), Warning = Parse("#8A6015"), Input = Parse("#F8FAFD"), SoftSelection = Parse("#D5E8F7"),
		BackdropStart = Parse("#F3F3F3"), BackdropMiddle = Parse("#EEEEEE"), BackdropEnd = Parse("#F1F1F1")
	};

	private static ThemePalette SamsungOneUi() => new ThemePalette
	{
		Name = OneUi, IsLight = true, IsGlass = false,
		Bg = Parse("#F1EEF8"), Sidebar = Parse("#E8E1F3"), Surface = Parse("#FFFFFF"), Surface2 = Parse("#F0EAF8"),
		Border = Parse("#D6CBE5"), Text = Parse("#241B35"), Muted = Parse("#635670"), Accent = Parse("#6750A4"), AccentLight = Parse("#8065BE"),
		Teal = Parse("#437F68"), Success = Parse("#176F55"), Danger = Parse("#A9344B"), Warning = Parse("#835B13"), Input = Parse("#FFFFFF"), SoftSelection = Parse("#DDD1EF"),
		BackdropStart = Parse("#F6F1FC"), BackdropMiddle = Parse("#EEE6F7"), BackdropEnd = Parse("#F6F1FC")
	};

	private static ThemePalette LiquidGlass() => new ThemePalette
	{
		Name = IosLiquidGlass, IsLight = true, IsGlass = true,
		Bg = Parse("#E9EBF3"), Sidebar = Parse("#E8ECF4"), Surface = Parse("#FCFDFF"), Surface2 = Parse("#F4F5FB"),
		Border = Parse("#B3C2D3"), Text = Parse("#162334"), Muted = Parse("#405369"), Accent = Parse("#007AFF"), AccentLight = Parse("#2087EF"),
		Teal = Parse("#208783"), Success = Parse("#16834F"), Danger = Parse("#C63C49"), Warning = Parse("#805D18"), Input = Parse("#FDFDFF"), SoftSelection = Parse("#D6E9FD"),
		BackdropStart = Parse("#D7ECFC"), BackdropMiddle = Parse("#E7F1F7"), BackdropEnd = Parse("#D5E6F4")
	};

	private static ThemePalette Dark() => new ThemePalette
	{
		Name = WindowsDark, IsLight = false, IsGlass = false,
		Bg = Parse("#202020"), Sidebar = Parse("#191919"), Surface = Parse("#2C2C2C"), Surface2 = Parse("#333333"),
		Border = Parse("#4C4C4C"), Text = Parse("#F5F5F5"), Muted = Parse("#BFBFBF"), Accent = Parse("#0078D4"), AccentLight = Parse("#60CDFF"),
		Teal = Parse("#52C9C0"), Success = Parse("#5BC59A"), Danger = Parse("#F0808C"), Warning = Parse("#F1C36E"), Input = Parse("#20262E"), SoftSelection = Parse("#33465C"),
		BackdropStart = Parse("#242424"), BackdropMiddle = Parse("#202020"), BackdropEnd = Parse("#262626")
	};

    private static void ApplyMaterial(ThemePalette p, double hue, bool dark) {
        p.IsLight = !dark; p.IsGlass = false;
        p.Bg = Tone(hue, .16, dark ? 6 : 98); p.Sidebar = Tone(hue, .19, dark ? 10 : 95);
        p.Surface = Tone(hue, .18, dark ? 12 : 96); p.Surface2 = Tone(hue, .22, dark ? 18 : 92);
        p.Text = Tone(hue, .12, dark ? 94 : 10); p.Muted = Tone(hue, .12, dark ? 78 : 35);
        p.Accent = Tone(hue, .62, dark ? 80 : 40); p.AccentLight = Tone(hue, .55, dark ? 85 : 45);
        p.SoftSelection = Tone(hue, .44, dark ? 30 : 90); p.Border = Tone(hue, .15, dark ? 35 : 80);
        p.Input = p.Surface2; p.BackdropStart = p.BackdropMiddle = p.BackdropEnd = p.Bg;
    }
    // Same hue / relative-luminance tone approach as the Android Expressive palette.
    private static Color Tone(double hue, double saturation, double tone) {
        hue = double.IsNaN(hue) || double.IsInfinity(hue) ? 275 : ((hue % 360) + 360) % 360;
        double target = tone > 8 ? Math.Pow((tone + 16) / 116, 3) : tone / 903.3;
        double low = 0, high = 1; Color c = Colors.Black;
        for (int i = 0; i < 20; i++) {
            double l = (low + high) / 2, a = saturation * Math.Min(l, 1-l);
            Func<double, byte> channel = offset => { double k = (offset + hue / 30) % 12; return (byte)Math.Round(255 * (l - a * Math.Max(-1, Math.Min(Math.Min(k-3, 9-k), 1)))); };
            c = Color.FromRgb(channel(0), channel(8), channel(4));
            if (RelativeLuminance(c) < target) low = l; else high = l;
        }
        return c;
    }

	private static Color WithOpacity(Color color, double opacity) => Color.FromArgb((byte)Math.Round(255.0 * Clamp(opacity, 0.0, 1.0)), color.R, color.G, color.B);
	private static Color Lighten(Color color, double amount) => Blend(color, Colors.White, amount);
	private static double Clamp(double value, double min, double max) => double.IsNaN(value) || double.IsInfinity(value) ? min : Math.Max(min, Math.Min(max, value));
	private static double ContrastRatio(Color first, Color second)
	{
		double a = RelativeLuminance(first);
		double b = RelativeLuminance(second);
		return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
	}
	private static double RelativeLuminance(Color color)
	{
		double r = Linear(color.R / 255.0), g = Linear(color.G / 255.0), b = Linear(color.B / 255.0);
		return 0.2126 * r + 0.7152 * g + 0.0722 * b;
	}
	private static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
}

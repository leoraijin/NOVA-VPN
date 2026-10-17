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
	public static readonly string[] ThemeNames = { Windows };
    // Legacy names are accepted only to migrate stored preferences.
    public static bool IsLegacyDark(string name) =>
        string.Equals(name, WindowsDark, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "NOVA Dark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "OLED Black", StringComparison.OrdinalIgnoreCase);
    public static string NormalizeName(string name) => Windows;
    public static ThemePalette Create(VisualPreferences preferences)
    {
        bool dark = preferences?.DarkMode == true || IsLegacyDark(preferences?.ThemeName);
        ThemePalette palette = dark ? Dark() : Light();
        palette.Name = Windows;
        palette.AccentForeground = ReadableForeground(palette.Accent);
        if (preferences?.HighContrast == true || System.Windows.SystemParameters.HighContrast)
        {
            palette.Bg = palette.Sidebar = System.Windows.SystemColors.WindowColor;
            palette.Surface = palette.Surface2 = palette.Input = palette.Bg;
            palette.Text = palette.Muted = palette.Border = System.Windows.SystemColors.WindowTextColor;
            palette.Accent = palette.AccentLight = System.Windows.SystemColors.HighlightColor;
            palette.AccentForeground = System.Windows.SystemColors.HighlightTextColor;
            palette.SoftSelection = palette.Accent;
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
		Bg = Parse("#F0F0F0"), Sidebar = Parse("#F3F3F3"), Surface = Parse("#FFFFFF"), Surface2 = Parse("#F5F5F5"),
		Border = Parse("#E0E0E0"), Text = Parse("#202020"), Muted = Parse("#5C5C5C"), Accent = Parse("#0067C0"), AccentLight = Parse("#1975C5"),
		Teal = Parse("#16827D"), Success = Parse("#167B5E"), Danger = Parse("#B23B52"), Warning = Parse("#8A6015"), Input = Parse("#FFFFFF"), SoftSelection = Parse("#D5E8F7"),
		BackdropStart = Parse("#F3F3F3"), BackdropMiddle = Parse("#EEEEEE"), BackdropEnd = Parse("#F1F1F1")
	};
	private static ThemePalette Dark() => new ThemePalette
	{
		Name = WindowsDark, IsLight = false, IsGlass = false,
		Bg = Parse("#202020"), Sidebar = Parse("#202020"), Surface = Parse("#2C2C2C"), Surface2 = Parse("#333333"),
		Border = Parse("#454545"), Text = Parse("#F5F5F5"), Muted = Parse("#BFBFBF"), Accent = Parse("#60CDFF"), AccentLight = Parse("#60CDFF"),
		Teal = Parse("#52C9C0"), Success = Parse("#5BC59A"), Danger = Parse("#F0808C"), Warning = Parse("#F1C36E"), Input = Parse("#242424"), SoftSelection = Parse("#33465C"),
		BackdropStart = Parse("#242424"), BackdropMiddle = Parse("#202020"), BackdropEnd = Parse("#262626")
	};
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

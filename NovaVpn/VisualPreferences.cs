using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class VisualPreferences
{
	[DataMember] public string ThemeName = ThemePalette.WindowsLight;
	[DataMember] public string AccentHex = "";
	[DataMember] public string SuccessHex = "";
	[DataMember] public double CornerRadius = 18.0;
	[DataMember] public double SurfaceOpacity = 1.0;
	[DataMember] public double GlassFrost = 0.45;
	[DataMember] public double GlassBlur = 10.0;
	[DataMember] public double GlassShadow = 1.0;
	[DataMember] public double GlassHighlight = 1.0;
	[DataMember] public double GlowIntensity = 0.18;
	[DataMember] public double TextScale = 1.0;
	[DataMember] public double AnimationSpeed = 1.0;
	[DataMember] public int AnimationFrameRate = 120;
	[DataMember] public string Density = "Standard";
	[DataMember] public string ConnectButtonStyle = "Orb";
	[DataMember] public string BackgroundMode = "Gradient";
	[DataMember] public string CustomBackgroundPath = "";
	[DataMember] public double BackgroundBlur = 0.0;
	[DataMember] public double BackgroundDim = 0.32;
	[DataMember] public bool HighContrast;
	[DataMember] public double SoftContrast;
	[DataMember] public bool ReduceMotion;
	[DataMember] public bool FollowSystemMotion = true;
	[DataMember] public string ServerViewMode = "Detailed";
	[DataMember] public bool ShowConnectionMap = true;
	[DataMember] public bool ShowLiveGraph = true;
	[DataMember] public List<string> HomeCardOrder = new List<string> { "Speed", "Protocol", "Latency", "Session", "Privacy" };
	[DataMember] public List<string> HiddenHomeCards = new List<string>();
	[DataMember] public Dictionary<string, VisualThemeOverrides> ThemeOverrides = new Dictionary<string, VisualThemeOverrides>(StringComparer.OrdinalIgnoreCase);

	public VisualPreferences Clone()
	{
		VisualPreferences copy = new VisualPreferences
		{
			ThemeName = ThemeName,
			AccentHex = AccentHex,
			SuccessHex = SuccessHex,
			CornerRadius = CornerRadius,
			SurfaceOpacity = SurfaceOpacity,
			GlassFrost = GlassFrost,
			GlassBlur = GlassBlur,
			GlassShadow = GlassShadow,
			GlassHighlight = GlassHighlight,
			GlowIntensity = GlowIntensity,
			TextScale = TextScale,
			AnimationSpeed = AnimationSpeed,
			AnimationFrameRate = AnimationFrameRate,
			Density = Density,
			ConnectButtonStyle = ConnectButtonStyle,
			BackgroundMode = BackgroundMode,
			CustomBackgroundPath = CustomBackgroundPath,
			BackgroundBlur = BackgroundBlur,
			BackgroundDim = BackgroundDim,
			HighContrast = HighContrast,
			SoftContrast = SoftContrast,
			ReduceMotion = ReduceMotion,
			FollowSystemMotion = FollowSystemMotion,
			ServerViewMode = ServerViewMode,
			ShowConnectionMap = ShowConnectionMap,
			ShowLiveGraph = ShowLiveGraph,
			HomeCardOrder = new List<string>(HomeCardOrder ?? new List<string>()),
			HiddenHomeCards = new List<string>(HiddenHomeCards ?? new List<string>()),
			ThemeOverrides = new Dictionary<string, VisualThemeOverrides>(StringComparer.OrdinalIgnoreCase)
		};
		if (ThemeOverrides != null)
			foreach (KeyValuePair<string, VisualThemeOverrides> pair in ThemeOverrides)
				if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
					copy.ThemeOverrides[ThemePalette.NormalizeName(pair.Key)] = pair.Value.Clone();
		return copy;
	}

	public void CopyFrom(VisualPreferences other)
	{
		if (other == null) return;
		VisualPreferences copy = other.Clone();
		ThemeName = copy.ThemeName;
		AccentHex = copy.AccentHex;
		SuccessHex = copy.SuccessHex;
		CornerRadius = copy.CornerRadius;
		SurfaceOpacity = copy.SurfaceOpacity;
		GlassFrost = copy.GlassFrost;
		GlassBlur = copy.GlassBlur;
		GlassShadow = copy.GlassShadow;
		GlassHighlight = copy.GlassHighlight;
		GlowIntensity = copy.GlowIntensity;
		TextScale = copy.TextScale;
		AnimationSpeed = copy.AnimationSpeed;
		AnimationFrameRate = copy.AnimationFrameRate;
		Density = copy.Density;
		ConnectButtonStyle = copy.ConnectButtonStyle;
		BackgroundMode = copy.BackgroundMode;
		CustomBackgroundPath = copy.CustomBackgroundPath;
		BackgroundBlur = copy.BackgroundBlur;
		BackgroundDim = copy.BackgroundDim;
		HighContrast = copy.HighContrast;
		SoftContrast = copy.SoftContrast;
		ReduceMotion = copy.ReduceMotion;
		FollowSystemMotion = copy.FollowSystemMotion;
		ServerViewMode = copy.ServerViewMode;
		ShowConnectionMap = copy.ShowConnectionMap;
		ShowLiveGraph = copy.ShowLiveGraph;
		HomeCardOrder = copy.HomeCardOrder;
		HiddenHomeCards = copy.HiddenHomeCards;
		ThemeOverrides = copy.ThemeOverrides;
	}

	public void StoreActiveThemeSettings()
	{
		if (ThemeOverrides == null) ThemeOverrides = new Dictionary<string, VisualThemeOverrides>(StringComparer.OrdinalIgnoreCase);
		ThemeOverrides[ThemePalette.NormalizeName(ThemeName)] = VisualThemeOverrides.From(this);
	}

	public void LoadThemeSettings(string themeName)
	{
		ThemeName = ThemePalette.NormalizeName(themeName);
		VisualThemeOverrides settings;
		if (ThemeOverrides != null && ThemeOverrides.TryGetValue(ThemeName, out settings) && settings != null)
			settings.ApplyTo(this);
		else
			VisualThemeOverrides.DefaultsFor(ThemeName).ApplyTo(this);
	}
}

[DataContract]
public sealed class VisualThemeOverrides
{
	[DataMember] public string AccentHex = "";
	[DataMember] public string SuccessHex = "";
	[DataMember] public double CornerRadius = 18.0;
	[DataMember] public double SurfaceOpacity = 1.0;
	[DataMember] public double GlassFrost = 0.45;
	[DataMember] public double GlassBlur = 10.0;
	[DataMember] public double GlassShadow = 1.0;
	[DataMember] public double GlassHighlight = 1.0;
	[DataMember] public double GlowIntensity = 0.18;
	[DataMember] public double TextScale = 1.0;
	[DataMember] public double AnimationSpeed = 1.0;
	[DataMember] public int AnimationFrameRate = 120;
	[DataMember] public string Density = "Standard";
	[DataMember] public string BackgroundMode = "Gradient";
	[DataMember] public double BackgroundDim = 0.32;
	[DataMember] public bool HighContrast;
	[DataMember] public double SoftContrast;
	[DataMember] public bool ReduceMotion;
	[DataMember] public bool FollowSystemMotion = true;

	public static VisualThemeOverrides From(VisualPreferences value) => new VisualThemeOverrides
	{
		AccentHex = value.AccentHex,
		SuccessHex = value.SuccessHex,
		CornerRadius = value.CornerRadius,
		SurfaceOpacity = value.SurfaceOpacity,
		GlassFrost = value.GlassFrost,
		GlassBlur = value.GlassBlur,
		GlassShadow = value.GlassShadow,
		GlassHighlight = value.GlassHighlight,
		GlowIntensity = value.GlowIntensity,
		TextScale = value.TextScale,
		AnimationSpeed = value.AnimationSpeed,
		AnimationFrameRate = value.AnimationFrameRate,
		Density = value.Density,
		BackgroundMode = value.BackgroundMode,
		BackgroundDim = value.BackgroundDim,
		HighContrast = value.HighContrast,
		SoftContrast = value.SoftContrast,
		ReduceMotion = value.ReduceMotion,
		FollowSystemMotion = value.FollowSystemMotion
	};

	public static VisualThemeOverrides DefaultsFor(string themeName)
	{
		VisualThemeOverrides result = new VisualThemeOverrides();
			switch (ThemePalette.NormalizeName(themeName))
		{
			case ThemePalette.IosLiquidGlass:
				result.GlassFrost = 0.0; result.CornerRadius = 18.0; result.SurfaceOpacity = 0.90; result.GlowIntensity = 0.22; result.TextScale = 1.02; result.AnimationSpeed = 1.12; result.BackgroundMode = "Glass"; result.BackgroundDim = 0.12; break;
			case ThemePalette.AmberGlass:
				result.GlassFrost = 0.56; result.GlassBlur = 10.0; result.GlassShadow = 0.78; result.GlassHighlight = 0.82; result.CornerRadius = 18.0; result.SurfaceOpacity = 0.96; result.GlowIntensity = 0.30; result.TextScale = 1.02; result.AnimationSpeed = 1.0; result.BackgroundMode = "Glass"; result.BackgroundDim = 0.08; break;
			case ThemePalette.OneUi:
				result.CornerRadius = 22.0; result.SurfaceOpacity = 0.98; result.GlowIntensity = 0.12; result.TextScale = 1.04; result.AnimationSpeed = 1.0; result.BackgroundMode = "Solid"; result.BackgroundDim = 0.0; break;
			case ThemePalette.WindowsDark:
				result.CornerRadius = 18.0; result.SurfaceOpacity = 1.0; result.GlowIntensity = 0.14; result.TextScale = 1.0; result.AnimationSpeed = 1.0; result.BackgroundMode = "Gradient"; result.BackgroundDim = 0.0; break;
			default:
				result.CornerRadius = 18.0; result.SurfaceOpacity = 1.0; result.GlowIntensity = 0.18; result.TextScale = 1.0; result.AnimationSpeed = 1.0; result.BackgroundMode = "Gradient"; result.BackgroundDim = 0.0; break;
		}
		return result;
	}

	public VisualThemeOverrides Clone() => (VisualThemeOverrides)MemberwiseClone();

	public void ApplyTo(VisualPreferences value)
	{
		if (value == null) return;
		value.AccentHex = AccentHex ?? "";
		value.SuccessHex = SuccessHex ?? "";
		value.CornerRadius = CornerRadius;
		value.SurfaceOpacity = SurfaceOpacity;
		value.GlassFrost = GlassFrost;
		value.GlassBlur = GlassBlur;
		value.GlassShadow = GlassShadow;
		value.GlassHighlight = GlassHighlight;
		value.GlowIntensity = GlowIntensity;
		value.TextScale = TextScale;
		value.AnimationSpeed = AnimationSpeed;
		value.AnimationFrameRate = AnimationFrameRate;
		value.Density = Density ?? "Standard";
		value.BackgroundMode = BackgroundMode ?? "Gradient";
		value.BackgroundDim = BackgroundDim;
		value.HighContrast = HighContrast;
		value.SoftContrast = SoftContrast;
		value.ReduceMotion = ReduceMotion;
		value.FollowSystemMotion = FollowSystemMotion;
	}
}

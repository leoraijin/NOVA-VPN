using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace NovaVpn;

public static class StateStore
{
	private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("NOVA-VPN-STATE-v1");
    private static readonly Lazy<EncryptedStateFiles> Files=new Lazy<EncryptedStateFiles>(()=>OpenDirectory(DirectoryPath,null));
    internal static EncryptedStateFiles OpenDirectory(string directory,Action<string> checkpoint)
    { return new EncryptedStateFiles(directory,SerializeForStorage,DeserializeStorage,Normalize,CreateDefault,LegacyEntropy,checkpoint); }
    internal static AppState SnapshotForSave(AppState source)
    {
        var copy=FullTransfer.Copy(source);byte[] serialized=SerializeForStorage(copy);
        try{var stored=DeserializeStorage(serialized);Normalize(stored);return stored;}finally{Array.Clear(serialized,0,serialized.Length);}
    }
    private static AppState DeserializeStorage(byte[] bytes)
    { using(var stream=new MemoryStream(bytes))return (AppState)new DataContractJsonSerializer(typeof(AppState)).ReadObject(stream); }
    private static byte[] SerializeForStorage(AppState state)
    {
        Normalize(state);
        try{return Serialize(state);}
        catch(SerializationException){ResetTemporalMetadata(state);return Serialize(state);}
    }

	public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaVPN");

	public static string StatePath => Path.Combine(DirectoryPath, "state.dat");

    public static AppState Load() => Files.Value.Load();
    public static void Save(AppState state) => Files.Value.Save(state);

	public static AppState CreateEphemeral()
	{
		return CreateDefault();
	}

	public static void NormalizeForSerialization(AppState state)
	{
		Normalize(state);
	}

	private static AppState CreateDefault()
	{
		AppState appState = new AppState();
		appState.LastSeenReleaseNotesVersion = ReleaseNotesService.CurrentVersionText;
		appState.Routing.DirectDomains.AddRange(new string[2] { "localhost", "*.local" });
		return appState;
	}

	private static void Normalize(AppState state)
	{
		if (state != null)
		{
			state.SubscriptionIdentities = state.SubscriptionIdentities ?? new IdentityState();
            // Old/partial documents may omit Routing before schema4 migrations.
            state.Routing=state.Routing??new RoutingSettings();
            if (state.SchemaVersion < 2)
			{
				state.AutoReconnect = true;
				state.ShowAnimations = true;
				state.ShowNotifications = true;
				state.SubscriptionUpdateIntervalHours = 24;
				state.SchemaVersion = 2;
			}
			if (state.SchemaVersion < 3)
			{
				state.AutoSelectBestServer = true;
				state.SchemaVersion = 3;
			}
			if (state.SchemaVersion < 4)
			{
				state.Routing.StrictRoute = true;
				state.Routing.EnableSniffing = true;
				state.Routing.BypassPrivateDns = true;
				state.SchemaVersion = 4;
			}
			if (state.SchemaVersion < 5)
			{
				state.Visual = new VisualPreferences();
				state.SchemaVersion = 5;
			}
			if (state.SchemaVersion < 6)
			{
				state.Zapret = new ZapretSettings();
				state.SchemaVersion = 6;
			}
			if (state.SchemaVersion < 7)
			{
				state.ShowTrainingOnStartup = true;
				state.ShowTrainingHints = true;
				state.SchemaVersion = 7;
			}
			if (state.SchemaVersion < 8)
			{
				// The iOS control is now expressed as transparency (100% means clearest glass).
				// Preserve all other preferences while setting this requested profile value once.
				if (state.Visual != null)
				{
					if (string.Equals(ThemePalette.NormalizeName(state.Visual.ThemeName), ThemePalette.IosLiquidGlass, StringComparison.OrdinalIgnoreCase))
						state.Visual.GlassFrost = 0.0;
					VisualThemeOverrides iosSettings;
					if (state.Visual.ThemeOverrides != null && state.Visual.ThemeOverrides.TryGetValue(ThemePalette.IosLiquidGlass, out iosSettings) && iosSettings != null)
						iosSettings.GlassFrost = 0.0;
				}
				state.SchemaVersion = 8;
			}
			if (state.SchemaVersion < 9)
			{
				if (state.Visual != null)
				{
					if (string.Equals(ThemePalette.NormalizeName(state.Visual.ThemeName), ThemePalette.IosLiquidGlass, StringComparison.OrdinalIgnoreCase)) state.Visual.CornerRadius = 18.0;
					VisualThemeOverrides iosSettings;
					if (state.Visual.ThemeOverrides != null && state.Visual.ThemeOverrides.TryGetValue(ThemePalette.IosLiquidGlass, out iosSettings) && iosSettings != null) iosSettings.CornerRadius = 18.0;
				}
				state.SchemaVersion = 9;
			}
			if (state.SchemaVersion < 10)
			{
				if (state.Visual != null)
				{
					state.Visual.GlassBlur = 10; state.Visual.GlassShadow = state.Visual.GlassHighlight = 1;
					if (state.Visual.ThemeOverrides != null) foreach (var settings in state.Visual.ThemeOverrides.Values) if (settings != null) { settings.GlassBlur = 10; settings.GlassShadow = settings.GlassHighlight = 1; }
				}
				state.SchemaVersion = 10;
			}
			if (state.Visual == null) state.Visual = new VisualPreferences();
			if (state.Zapret == null) state.Zapret = new ZapretSettings();
			if (string.IsNullOrWhiteSpace(state.Zapret.RepositoryOwner)) state.Zapret.RepositoryOwner = "Flowseal";
			if (string.IsNullOrWhiteSpace(state.Zapret.RepositoryName)) state.Zapret.RepositoryName = "zapret-discord-youtube";
			if (string.IsNullOrWhiteSpace(state.Zapret.RepositoryBranch)) state.Zapret.RepositoryBranch = "master";
			if (string.IsNullOrWhiteSpace(state.Zapret.Mode)) state.Zapret.Mode = "VPN";
			if (state.Zapret.StandardDomains == null) state.Zapret.StandardDomains = new List<string>();
			if (state.Zapret.SavedProxyDomains == null) state.Zapret.SavedProxyDomains = new List<string>();
			if (state.Zapret.AddedDirectDomains == null) state.Zapret.AddedDirectDomains = new List<string>();
			if (state.Zapret.SavedProxyProcesses == null) state.Zapret.SavedProxyProcesses = new List<string>();
			if (state.Zapret.AddedDirectProcesses == null) state.Zapret.AddedDirectProcesses = new List<string>();
			if (state.Zapret.SavedProxyProcessPaths == null) state.Zapret.SavedProxyProcessPaths = new List<string>();
			if (state.Zapret.AddedDirectProcessPaths == null) state.Zapret.AddedDirectProcessPaths = new List<string>();
			NormalizeVisual(state.Visual);
			state.Theme = state.Visual.ThemeName;
			if (state.SubscriptionUpdateIntervalHours < 1)
			{
				state.SubscriptionUpdateIntervalHours = 24;
			}
			if (state.Profiles == null)
			{
				state.Profiles = new List<VpnProfile>();
			}
			if (state.Routing == null)
			{
				state.Routing = new RoutingSettings();
			}
			if (state.Routing.DirectDomains == null)
			{
				state.Routing.DirectDomains = new List<string>();
			}
			if (state.Routing.ZapretDirectDomains == null)
			{
				state.Routing.ZapretDirectDomains = new List<string>();
			}
			if (state.Routing.ProxyDomains == null)
			{
				state.Routing.ProxyDomains = new List<string>();
			}
			if (state.Routing.BlockDomains == null)
			{
				state.Routing.BlockDomains = new List<string>();
			}
			if (state.Routing.DirectProcesses == null)
			{
				state.Routing.DirectProcesses = new List<string>();
			}
			if (state.Routing.ProxyProcesses == null)
			{
				state.Routing.ProxyProcesses = new List<string>();
			}
			if (state.ConnectionHistory == null)
			{
				state.ConnectionHistory = new List<ConnectionHistoryItem>();
			}
			if (state.ConnectionHistory.Count > 50)
			{
				state.ConnectionHistory.RemoveRange(0, state.ConnectionHistory.Count - 50);
			}
			state.LastSubscriptionUpdateUtc = SafeDate(state.LastSubscriptionUpdateUtc);
			state.PauseUntilUtc = SafeDate(state.PauseUntilUtc);
			foreach (ConnectionHistoryItem historyItem in state.ConnectionHistory)
			{
				if (historyItem != null)
				{
					historyItem.TimestampUtc = SafeDate(historyItem.TimestampUtc);
				}
			}
			if (state.Routing.DirectProcessPaths == null)
			{
				state.Routing.DirectProcessPaths = new List<string>();
			}
			if (state.Routing.ProxyProcessPaths == null)
			{
				state.Routing.ProxyProcessPaths = new List<string>();
			}
			if (state.Routing.AdvancedRules == null)
			{
				state.Routing.AdvancedRules = new List<string>();
			}
			state.Routing.MultiServer?.Validate();
            state.MultiServerRoutingHistory?.Validate();
            RoutingInspector.NormalizeDomains(state.Routing.DirectDomains);
			RoutingInspector.NormalizeDomains(state.Routing.ProxyDomains);
			RoutingInspector.NormalizeDomains(state.Routing.BlockDomains);
			if (string.IsNullOrWhiteSpace(state.Routing.DnsProvider))
			{
				state.Routing.DnsProvider = "Cloudflare";
			}
			if (string.IsNullOrWhiteSpace(state.Routing.DnsStrategy))
			{
				state.Routing.DnsStrategy = "PreferIPv4";
			}
			foreach (VpnProfile profile in state.Profiles)
			{
				if (profile == null)
				{
					continue;
				}
				if (profile.HealthStatus == null)
				{
					profile.HealthStatus = "Не проверен";
				}
				if (profile.LastTestMessage == null)
				{
					profile.LastTestMessage = "";
				}
				if (profile.SubscriptionUrl == null)
				{
					profile.SubscriptionUrl = "";
				}
				profile.AddedAt = SafeDate(profile.AddedAt, DateTime.UtcNow);
				profile.LastCheckedUtc = SafeDate(profile.LastCheckedUtc);
				profile.SubscriptionUpdatedUtc = SafeDate(profile.SubscriptionUpdatedUtc);
                if (string.IsNullOrWhiteSpace(profile.HealthMeasurement))
                {
                    profile.LastLatency = -1; profile.AverageLatency = -1; profile.LastLossPercent = -1;
                    profile.HealthStatus = "Нужна проверка HTTPS";
                }
			}
            SubscriptionManager.Normalize(state);
            ProfileCollectionService.Normalize(state);
            LocalProxyConfiguration.Normalize(state);
            // Preserve malformed enabled policies for explicit refusal at the
            // capture boundary; silently dropping them would remove limits.
            if(!state.ApplicationLimitsEnabled && state.ApplicationBandwidthRules==null)state.ApplicationBandwidthRules=new List<ApplicationBandwidthRule>();
            state.SchemaVersion = Math.Max(state.SchemaVersion, 17);
            foreach (var profile in state.Profiles)
                profile.FallbackProfileIds = (profile.FallbackProfileIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id) && id != profile.Id).Distinct(StringComparer.Ordinal).Take(ConfiguredFallbackService.MaxAlternatives).ToList();
            foreach (var subscription in state.Subscriptions)
            {
                subscription.LastAttemptUtc = SafeDate(subscription.LastAttemptUtc);
                subscription.LastSuccessUtc = SafeDate(subscription.LastSuccessUtc);
                subscription.NextAttemptUtc = SafeDate(subscription.NextAttemptUtc);
                NormalizeQuotaDates(subscription.Quota);
                if (subscription.Previous != null)
                {
                    subscription.Previous.CreatedUtc = SafeDate(subscription.Previous.CreatedUtc);
                    NormalizeQuotaDates(subscription.Previous.Quota);
                    foreach (var profile in subscription.Previous.Profiles ?? new List<VpnProfile>())
                    {
                        if (profile == null) continue;
                        profile.AddedAt = SafeDate(profile.AddedAt, DateTime.UtcNow);
                        profile.LastCheckedUtc = SafeDate(profile.LastCheckedUtc);
                        profile.SubscriptionUpdatedUtc = SafeDate(profile.SubscriptionUpdatedUtc);
                    }
                }
            }
		}
	}

    private static void NormalizeQuotaDates(SubscriptionQuota quota)
    {
        if (quota == null) return;
        quota.ReceivedUtc = SafeDate(quota.ReceivedUtc);
        if (quota.ExpiresUtc.HasValue)
        {
            DateTime safe = SafeDate(quota.ExpiresUtc.Value);
            quota.ExpiresUtc = safe == DateTime.MinValue ? (DateTime?)null : safe;
        }
    }

	private static byte[] Serialize(AppState state)
	{
		using (MemoryStream memoryStream = new MemoryStream())
		{
			DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppState));
			serializer.WriteObject((Stream)memoryStream, (object)state);
			return memoryStream.ToArray();
		}
	}

	private static DateTime SafeDate(DateTime value, DateTime fallback = default(DateTime))
	{
		// DataContractJsonSerializer applies a timezone offset to DateTime values.
		// MaxValue/MinValue can therefore overflow during JSON serialization.
		if (value == DateTime.MaxValue || value == DateTime.MinValue || value.Year >= 9999 || value.Year <= 1)
		{
			return DateTime.SpecifyKind(fallback, DateTimeKind.Utc);
		}
		try
		{
			DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
			if (utc <= DateTime.MinValue.AddDays(1) || utc >= DateTime.MaxValue.AddDays(-1))
			{
				return DateTime.SpecifyKind(fallback, DateTimeKind.Utc);
			}
			return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
		}
		catch (ArgumentException)
		{
			return DateTime.SpecifyKind(fallback, DateTimeKind.Utc);
		}
	}

	private static void ResetTemporalMetadata(AppState state)
	{
		DateTime emptyUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
		state.LastSubscriptionUpdateUtc = emptyUtc;
		state.PauseUntilUtc = emptyUtc;
		if (state.ConnectionHistory != null)
		{
			foreach (ConnectionHistoryItem item in state.ConnectionHistory)
			{
				if (item != null) item.TimestampUtc = emptyUtc;
			}
		}
		if (state.Profiles != null)
		{
			foreach (VpnProfile profile in state.Profiles)
			{
				if (profile == null) continue;
				profile.AddedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
				profile.LastCheckedUtc = emptyUtc;
				profile.SubscriptionUpdatedUtc = emptyUtc;
			}
		}
	}

	private static void NormalizeVisual(VisualPreferences visual)
	{
		if (visual == null) return;
		if (ThemePalette.IsLegacyDark(visual.ThemeName)) { visual.DarkMode = true; visual.ThemeName = ThemePalette.Windows; }
        visual.ThemeName = ThemePalette.NormalizeName(visual.ThemeName);
        if (double.IsNaN(visual.MaterialHue) || double.IsInfinity(visual.MaterialHue)) visual.MaterialHue = 275;
        visual.MaterialHue = Math.Max(0, Math.Min(360, visual.MaterialHue));
		visual.AccentHex = "";
		visual.SuccessHex = "";
		visual.CornerRadius = ClampVisual(visual.CornerRadius, 8.0, 32.0, 18.0);
		visual.SurfaceOpacity = ClampVisual(visual.SurfaceOpacity, 0.78, 1.0, 1.0);
		visual.GlassFrost = ClampVisual(visual.GlassFrost, 0.0, 1.0, 0.45);
		visual.GlassBlur = ClampVisual(visual.GlassBlur, 0, 32, 10);
		visual.GlassShadow = ClampVisual(visual.GlassShadow, 0, 1, 1);
		visual.GlassHighlight = ClampVisual(visual.GlassHighlight, 0, 1, 1);
		visual.SoftContrast = ClampVisual(visual.SoftContrast, 0, 1, 0);
		visual.GlowIntensity = ClampVisual(visual.GlowIntensity, 0.0, 0.6, 0.18);
		visual.TextScale = ClampVisual(visual.TextScale, 1.0, 1.5, 1.0);
		visual.AnimationSpeed = ClampVisual(visual.AnimationSpeed, 0.5, 2.0, 1.0);
		if (visual.AnimationFrameRate != 0 && visual.AnimationFrameRate != 60 && visual.AnimationFrameRate != 120) visual.AnimationFrameRate = 120;
		visual.Density = NormalizeVisualChoice(visual.Density, "Compact", "Comfortable", "Standard");
		visual.BackgroundMode = NormalizeVisualChoice(visual.BackgroundMode, "Solid", "Glass", "Gradient");
		visual.BackgroundDim = ClampVisual(visual.BackgroundDim, 0.0, 0.7, 0.0);
		visual.BackgroundBlur = ClampVisual(visual.BackgroundBlur, 0.0, 24.0, 0.0);
		visual.CustomBackgroundPath = "";
		visual.ConnectButtonStyle = "Windows";
		if (visual.HomeCardOrder == null || visual.HomeCardOrder.Count == 0) visual.HomeCardOrder = new List<string> { "Speed", "Protocol", "Latency", "Session", "Privacy" };
		if (visual.HiddenHomeCards == null) visual.HiddenHomeCards = new List<string>();
		Dictionary<string, VisualThemeOverrides> normalized = new Dictionary<string, VisualThemeOverrides>(StringComparer.OrdinalIgnoreCase);
		if (visual.ThemeOverrides != null)
			foreach (KeyValuePair<string, VisualThemeOverrides> pair in visual.ThemeOverrides)
			{
				if (pair.Value == null) continue;
				string name = ThemePalette.NormalizeName(pair.Key);
				VisualThemeOverrides settings = pair.Value.Clone();
				VisualThemeOverrides defaults = VisualThemeOverrides.DefaultsFor(name);
				settings.AccentHex = "";
				settings.SuccessHex = "";
				settings.CornerRadius = ClampVisual(settings.CornerRadius, 8.0, 32.0, defaults.CornerRadius);
				settings.SurfaceOpacity = ClampVisual(settings.SurfaceOpacity, 0.78, 1.0, defaults.SurfaceOpacity);
				settings.GlassFrost = ClampVisual(settings.GlassFrost, 0.0, 1.0, defaults.GlassFrost);
				settings.GlassBlur = ClampVisual(settings.GlassBlur, 0, 32, defaults.GlassBlur);
				settings.GlassShadow = ClampVisual(settings.GlassShadow, 0, 1, defaults.GlassShadow);
				settings.GlassHighlight = ClampVisual(settings.GlassHighlight, 0, 1, defaults.GlassHighlight);
				settings.SoftContrast = ClampVisual(settings.SoftContrast, 0, 1, 0);
				settings.GlowIntensity = ClampVisual(settings.GlowIntensity, 0.0, 0.6, defaults.GlowIntensity);
				settings.TextScale = ClampVisual(settings.TextScale, 1.0, 1.5, defaults.TextScale);
				settings.AnimationSpeed = ClampVisual(settings.AnimationSpeed, 0.5, 2.0, defaults.AnimationSpeed);
				if (settings.AnimationFrameRate != 0 && settings.AnimationFrameRate != 60 && settings.AnimationFrameRate != 120) settings.AnimationFrameRate = 120;
				settings.Density = NormalizeVisualChoice(settings.Density, "Compact", "Comfortable", defaults.Density);
				settings.BackgroundMode = NormalizeVisualChoice(settings.BackgroundMode, "Solid", "Glass", defaults.BackgroundMode);
				settings.BackgroundDim = ClampVisual(settings.BackgroundDim, 0.0, 0.7, defaults.BackgroundDim);
				normalized[name] = settings;
			}
		visual.ThemeOverrides = normalized;
	}

	private static string NormalizeVisualHex(string value)
	{
		Color ignored;
		return ThemePalette.TryParseColor(value, out ignored) ? value.Trim() : "";
	}

	private static double ClampVisual(double value, double min, double max, double fallback)
	{
		if (double.IsNaN(value) || double.IsInfinity(value)) value = fallback;
		return Math.Max(min, Math.Min(max, value));
	}

	private static string NormalizeVisualChoice(string value, string first, string second, string fallback)
	{
		if (string.Equals(value, first, StringComparison.OrdinalIgnoreCase)) return first;
		if (string.Equals(value, second, StringComparison.OrdinalIgnoreCase)) return second;
		return fallback;
	}


	public static void ExportEncrypted(AppState state, string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Не указан путь резервной копии.", nameof(path));
		}
		Save(state);
		File.Copy(StatePath, path, true);
	}

	public static AppState ImportEncrypted(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Резервная копия не найдена.", path);
		}
		AppState imported = Decode(File.ReadAllText(path, Encoding.UTF8));
		Normalize(imported);
		return imported;
	}

    private static AppState Decode(string encoded) => Files.Value.Decode(encoded);
}

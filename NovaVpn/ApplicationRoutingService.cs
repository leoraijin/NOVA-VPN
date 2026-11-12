using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaVpn;

public static class ApplicationRoutingService
{
	public const string Automatic = "Auto";
	public const string Proxy = "Proxy";
	public const string Direct = "Direct";

	public static string NormalizePath(string value)
	{
		try
		{
			string path = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"'));
			if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return "";
			return Path.GetFullPath(path);
		}
		catch (Exception)
		{
			return "";
		}
	}

	public static string GetRoute(RoutingSettings settings, string executablePath)
	{
		string path = NormalizePath(executablePath);
		if (settings == null || path.Length == 0) return Automatic;
		if (Contains(settings.DirectProcessPaths, path)) return Direct;
		if (Contains(settings.ProxyProcessPaths, path)) return Proxy;
		return Automatic;
	}

	public static bool SetRoute(RoutingSettings settings, string executablePath, string route)
	{
		if (settings == null) throw new ArgumentNullException(nameof(settings));
		string path = NormalizePath(executablePath);
		if (path.Length == 0) throw new ArgumentException("Выберите полный путь к .exe файлу.", nameof(executablePath));
		if (route != Automatic && route != Proxy && route != Direct) throw new ArgumentException("Неизвестный маршрут приложения.", nameof(route));
		if (settings.DirectProcessPaths == null) settings.DirectProcessPaths = new List<string>();
		if (settings.ProxyProcessPaths == null) settings.ProxyProcessPaths = new List<string>();
		string before = GetRoute(settings, path);
		int removed = settings.DirectProcessPaths.RemoveAll(item => SamePath(item, path));
		removed += settings.ProxyProcessPaths.RemoveAll(item => SamePath(item, path));
		if (route == Direct) settings.DirectProcessPaths.Add(path);
		if (route == Proxy) settings.ProxyProcessPaths.Add(path);
		return before != route || removed > (route == Automatic ? 0 : 1);
	}

	public static bool SamePath(string first, string second)
	{
		string a = NormalizePath(first);
		string b = NormalizePath(second);
		return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	private const string ExactPathPrefix = @"(?i)\A";
	private const string ExactPathSuffix = @"\z";

	internal static string ExactPathRegex(string path)
	{
		var pattern = new StringBuilder(ExactPathPrefix);
		foreach (char character in path)
		{
			if (@"\.+*?()|[]{}^$".IndexOf(character) >= 0) pattern.Append('\\');
			pattern.Append(character);
		}
		return pattern.Append(ExactPathSuffix).ToString();
	}

	internal static string DecodeExactPathRegex(string pattern)
	{
		if (!pattern.StartsWith(ExactPathPrefix, StringComparison.Ordinal) ||
			!pattern.EndsWith(ExactPathSuffix, StringComparison.Ordinal)) return null;

		string path;
		try
		{
			path = Regex.Unescape(pattern.Substring(ExactPathPrefix.Length,
				pattern.Length - ExactPathPrefix.Length - ExactPathSuffix.Length));
		}
		catch (ArgumentException) { return null; }
		return NormalizePath(path).Length > 0 && ExactPathRegex(path) == pattern ? path : null;
	}

    private const string NamePrefix = @"(?i)(?:\A|[\\/])";
    internal static string ExactNameRegex(string name) => NamePrefix + ExactPathRegex(name).Substring(ExactPathPrefix.Length);
    internal static string DecodeExactNameRegex(string pattern)
    {
        if (!pattern.StartsWith(NamePrefix, StringComparison.Ordinal) || !pattern.EndsWith(ExactPathSuffix, StringComparison.Ordinal)) return null;
        try
        {
            string name = Regex.Unescape(pattern.Substring(NamePrefix.Length, pattern.Length - NamePrefix.Length - ExactPathSuffix.Length));
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return ExactNameRegex(name) == pattern ? name : null;
        }
        catch (ArgumentException) { return null; }
    }

	private static bool Contains(IEnumerable<string> values, string path)
	{
		return values != null && values.Any(item => SamePath(item, path));
	}
}

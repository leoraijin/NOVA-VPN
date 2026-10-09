using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

	private static bool Contains(IEnumerable<string> values, string path)
	{
		return values != null && values.Any(item => SamePath(item, path));
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NovaVpn;

public sealed class ApplicationEntry
{
	public string Name;
	public string ExecutablePath;
	public string Source;
}

public static class ApplicationCatalog
{
	public static List<ApplicationEntry> Discover(IEnumerable<string> savedPaths)
	{
		Dictionary<string, ApplicationEntry> found = new Dictionary<string, ApplicationEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in savedPaths ?? Enumerable.Empty<string>()) Add(found, Path.GetFileName(path), path, "Правило NOVA");
		AddFromRegistry(found);
		AddFromShortcuts(found);
		AddRunning(found);
		return found.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public static ApplicationEntry FromFile(string path)
	{
		string normalized = ApplicationRoutingService.NormalizePath(path);
		if (normalized.Length == 0 || !File.Exists(normalized)) throw new FileNotFoundException("Не найден исполняемый файл приложения.", path);
		return new ApplicationEntry { Name = Path.GetFileNameWithoutExtension(normalized), ExecutablePath = normalized, Source = "Выбрано вручную" };
	}

	private static void Add(Dictionary<string, ApplicationEntry> found, string name, string path, string source)
	{
		string normalized = ApplicationRoutingService.NormalizePath(path);
		if (normalized.Length == 0) return;
		if (source != "Правило NOVA" && (normalized.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(normalized))) return;
		if (found.TryGetValue(normalized, out ApplicationEntry existing))
		{
			if (existing.Source == "Правило NOVA" && source != "Правило NOVA")
			{
				existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim();
				existing.Source = source;
			}
			return;
		}
		found.Add(normalized, new ApplicationEntry
		{
			Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(normalized) : name.Trim(),
			ExecutablePath = normalized,
			Source = source
		});
	}

	private static void AddFromRegistry(Dictionary<string, ApplicationEntry> found)
	{
		foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
		foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
		{
			try
			{
				using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
				using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
				{
					if (uninstall == null) continue;
					foreach (string keyName in uninstall.GetSubKeyNames().Take(1000))
					{
						try
						{
							using (RegistryKey item = uninstall.OpenSubKey(keyName))
							{
								string icon = item?.GetValue("DisplayIcon") as string;
								string title = item?.GetValue("DisplayName") as string;
								if (string.IsNullOrWhiteSpace(icon) || string.IsNullOrWhiteSpace(title)) continue;
								string path = icon.Trim().Trim('"');
								int comma = path.LastIndexOf(',');
								if (comma > 0 && int.TryParse(path.Substring(comma + 1), out _)) path = path.Substring(0, comma).Trim().Trim('"');
								Add(found, title, path, "Установлено");
							}
						}
						catch (Exception) { }
					}
				}
			}
			catch (Exception) { }
		}
	}

	private static void AddFromShortcuts(Dictionary<string, ApplicationEntry> found)
	{
		Type shellType = Type.GetTypeFromProgID("WScript.Shell");
		if (shellType == null) return;
		object shell = null;
		try
		{
			shell = Activator.CreateInstance(shellType);
			int count = 0;
			foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
			foreach (string shortcutPath in EnumerateShortcuts(Environment.GetFolderPath(folder)))
			{
				if (++count > 1200) return;
				object shortcut = null;
				try
				{
					shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
					Add(found, Path.GetFileNameWithoutExtension(shortcutPath), (string)((dynamic)shortcut).TargetPath, "Меню Пуск");
				}
				catch (Exception) { }
				finally { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
			}
		}
		catch (Exception) { }
		finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
	}

	private static IEnumerable<string> EnumerateShortcuts(string root)
	{
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
		Stack<string> pending = new Stack<string>();
		HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		pending.Push(root);
		while (pending.Count > 0 && visited.Count < 2000)
		{
			string folder = pending.Pop();
			if (!visited.Add(folder)) continue;
			string[] children;
			string[] shortcuts;
			try { children = Directory.GetDirectories(folder); shortcuts = Directory.GetFiles(folder, "*.lnk"); }
			catch (Exception) { continue; }
			foreach (string child in children)
			{
				try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
				catch (Exception) { }
			}
			foreach (string shortcut in shortcuts) yield return shortcut;
		}
	}

	private static void AddRunning(Dictionary<string, ApplicationEntry> found)
	{
		foreach (Process process in Process.GetProcesses())
		{
			try { Add(found, process.ProcessName, process.MainModule?.FileName, "Запущено"); }
			catch (Exception) { }
			finally { process.Dispose(); }
		}
	}
}

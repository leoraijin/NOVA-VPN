using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NovaVpn;

public enum ZapretLifecycleState
{
	Stopped,
	Starting,
	Running,
	Stopping,
	Error
}

public sealed partial class ZapretManager
{
	private readonly ZapretReleaseService releases = new ZapretReleaseService();
	private Process launcher;
	public ZapretLifecycleState LifecycleState { get; private set; } = ZapretLifecycleState.Stopped;
	public event Action<ZapretLifecycleState> LifecycleChanged;

	public ZapretManager()
	{
		BootstrapBundledVersion();
	}

	public string RootPath => Path.Combine(StateStore.DirectoryPath, "zapret");
	public string CurrentPath => Path.Combine(RootPath, "current");
	public string BackupPath => Path.Combine(RootPath, "backup");
	public bool IsInstalled => FindExecutable() != null;
	public bool IsRunning
	{
		get
		{
			// A child handle is valid across bitness; MainModule is not.
			try { if (launcher != null && !launcher.HasExited) return true; }
			catch (InvalidOperationException) { }
			Process[] found = FindZapretProcesses();
			bool running = found.Length > 0;
			foreach (Process process in found) process.Dispose();
			return running;
		}
	}

	private void BootstrapBundledVersion()
	{
		string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zapret", "current");
		if (!Directory.Exists(bundled) || Directory.Exists(CurrentPath)) return;
		Directory.CreateDirectory(RootPath);
		CopyDirectory(bundled, CurrentPath);
	}

	private static void CopyDirectory(string source, string target)
	{
		Directory.CreateDirectory(target);
		foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
		foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
	}

	public string FindExecutable()
	{
		if (!Directory.Exists(CurrentPath)) return null;
		return Directory.GetFiles(CurrentPath, "winws.exe", SearchOption.AllDirectories).FirstOrDefault()
			?? Directory.GetFiles(CurrentPath, "winws2.exe", SearchOption.AllDirectories).FirstOrDefault();
	}

	public string ReadInstalledVersion()
	{
		string path = Path.Combine(CurrentPath, "nova-version.txt");
		return File.Exists(path) ? File.ReadAllText(path).Trim() : (IsInstalled ? "локальная сборка" : "не установлен");
	}

	public bool IsUpdateAvailable(string latest, string current)
	{
		if (string.IsNullOrWhiteSpace(current) || current == "не установлен" || current == "локальная сборка") return true;
		if (string.Equals(latest, current, StringComparison.OrdinalIgnoreCase)) return false;
		Match latestMatch = Regex.Match(latest ?? "", @"\d+(?:\.\d+)+");
		Match currentMatch = Regex.Match(current ?? "", @"\d+(?:\.\d+)+");
		if (!latestMatch.Success || !currentMatch.Success) return true;
		int[] left = latestMatch.Value.Split('.').Select(int.Parse).ToArray();
		int[] right = currentMatch.Value.Split('.').Select(int.Parse).ToArray();
		for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
		{
			int a = i < left.Length ? left[i] : 0;
			int b = i < right.Length ? right[i] : 0;
			if (a != b) return a > b;
		}
		return false;
	}

	public async Task<ZapretReleaseInfo> CheckAsync(ZapretSettings settings)
	{
		return await releases.GetLatestAsync(settings);
	}

	public async Task<ZapretReleaseInfo> UpdateAsync(ZapretSettings settings)
	{
		SetLifecycle(ZapretLifecycleState.Stopping);
		ZapretReleaseInfo release = await CheckAsync(settings);
		if (IsInstalled && !IsUpdateAvailable(release.Version, ReadInstalledVersion()))
			throw new InvalidOperationException("Установка более старой версии Zapret заблокирована.");
		Directory.CreateDirectory(RootPath);
		string temp = Path.Combine(RootPath, "update-" + Guid.NewGuid().ToString("N"));
		string archive = temp + ".zip";
		Directory.CreateDirectory(temp);
		bool movedCurrentToBackup = false;
		try
		{
			await releases.DownloadAsync(release, archive);
			string unpacked = Path.Combine(temp, "unpacked");
			Directory.CreateDirectory(unpacked);
			ExtractArchiveSafely(archive, unpacked);
			string sourceRoot = FindBundleRoot(unpacked);
			if (sourceRoot == null) throw new InvalidDataException("В архиве не найден winws.exe или winws2.exe.");
			if (IsRunning) StopAndVerify();
			if (Directory.Exists(BackupPath)) Directory.Delete(BackupPath, true);
			if (Directory.Exists(CurrentPath))
			{
				Directory.Move(CurrentPath, BackupPath);
				movedCurrentToBackup = true;
			}
			Directory.Move(sourceRoot, CurrentPath);
			File.WriteAllText(Path.Combine(CurrentPath, "nova-version.txt"), release.Version);
			SetLifecycle(ZapretLifecycleState.Stopped);
			return release;
		}
		catch
		{
			SetLifecycle(ZapretLifecycleState.Error);
			if (movedCurrentToBackup && Directory.Exists(BackupPath))
			{
				try
				{
					if (Directory.Exists(CurrentPath)) Directory.Delete(CurrentPath, true);
					if (!Directory.Exists(CurrentPath)) Directory.Move(BackupPath, CurrentPath);
				}
				catch { }
			}
			throw;
		}
		finally
		{
			try { if (File.Exists(archive)) File.Delete(archive); } catch { }
			try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
		}
	}

	private static void ExtractArchiveSafely(string archive, string destination)
	{
		using (FileStream stream = File.OpenRead(archive))
		using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
		{
			if (zip.Entries.Count > SecurityGuard.MaxArchiveEntries) throw new InvalidDataException("В архиве слишком много файлов.");
			long total = 0;
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				if (!SecurityGuard.IsSafeArchivePath(destination, entry.FullName, out string target)) throw new InvalidDataException("Архив содержит небезопасный путь.");
				if (entry.Length > SecurityGuard.MaxArchiveEntryBytes || (total += entry.Length) > SecurityGuard.MaxArchiveBytes) throw new InvalidDataException("Архив Zapret слишком большой.");
				if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				using (Stream input = entry.Open())
				using (FileStream output = File.Create(target)) input.CopyTo(output);
			}
		}
	}

	public void Start()
	{
		if (IsRunning) return;
		LaunchStrategy(Path.Combine(CurrentPath, "general.bat"));
	}

	public void Stop()
	{
		System.Threading.Interlocked.Increment(ref selectionGeneration);
		SetLifecycle(ZapretLifecycleState.Stopping);
		StopOwnedProcess();
		// Recover a child left by an older NOVA instance, only after verifying
		// that its executable belongs to this installation.
		foreach (Process process in FindZapretProcesses())
		{
			using (process)
			{
				if (!process.HasExited)
				{
					process.Kill();
					if (!process.WaitForExit(3000)) throw new InvalidOperationException("Zapret не подтвердил остановку.");
				}
			}
		}
		SetLifecycle(ZapretLifecycleState.Stopped);
	}

	private void KillZapretProcesses()
	{
		foreach (Process process in FindZapretProcesses())
		{
			try { process.Kill(); process.WaitForExit(1500); } catch { }
			process.Dispose();
		}
		SetLifecycle(FindZapretProcesses().Any() ? ZapretLifecycleState.Error : ZapretLifecycleState.Stopped);
	}

	private static void RunElevated(string fileName, string arguments)
	{
		try
		{
			using (Process process = Process.Start(new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				UseShellExecute = true,
				Verb = "runas",
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			}))
			{
				if (process != null) process.WaitForExit(5000);
			}
		}
		catch
		{
			// StopAndVerify reports the remaining process to the UI if elevation
			// was denied or Windows could not run the cleanup command.
		}
	}

	private void SetLifecycle(ZapretLifecycleState state)
	{
		LifecycleState = state;
		try { LifecycleChanged?.Invoke(state); } catch { }
	}

	public void StopAndVerify()
	{
		Stop();
		for (int attempt = 0; attempt < 12 && IsRunning; attempt++)
		{
			System.Threading.Thread.Sleep(100);
		}
		if (IsRunning) throw new InvalidOperationException("Не удалось полностью остановить Zapret.");
	}

	private Process[] FindZapretProcesses()
	{
		List<Process> found = new List<Process>();
		string root = Path.GetFullPath(CurrentPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		foreach (Process process in Process.GetProcessesByName("winws").Concat(Process.GetProcessesByName("winws2")))
		{
			bool keep = false;
			try
			{
				string path = ReadProcessPath(process.Id);
				keep = path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
				if (keep) found.Add(process);
			}
			catch { }
			finally { if (!keep) process.Dispose(); }
		}
		return found.ToArray();
	}

	private static string ReadProcessPath(int id)
	{
		IntPtr handle = OpenProcess(0x1000, false, id);
		if (handle == IntPtr.Zero) return null;
		try
		{
			StringBuilder path = new StringBuilder(32768);
			int length = path.Capacity;
			return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
		}
		finally { CloseHandle(handle); }
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	private static string FindBundleRoot(string unpacked)
	{
		string executable = Directory.GetFiles(unpacked, "winws.exe", SearchOption.AllDirectories).FirstOrDefault()
			?? Directory.GetFiles(unpacked, "winws2.exe", SearchOption.AllDirectories).FirstOrDefault();
		if (executable == null) return null;
		string parent = Path.GetDirectoryName(executable);
		string best = parent;
		while (!string.IsNullOrWhiteSpace(parent) && parent.StartsWith(unpacked, StringComparison.OrdinalIgnoreCase))
		{
			if (HasLaunchScripts(parent)) best = parent;
			parent = Path.GetDirectoryName(parent);
		}
		return best;
	}

	private static bool HasLaunchScripts(string directory)
	{
		return Directory.GetFiles(directory, "*.bat").Length > 0 || Directory.GetFiles(directory, "*.cmd").Length > 0 || Directory.GetFiles(directory, "preset*.cmd").Length > 0;
	}
}

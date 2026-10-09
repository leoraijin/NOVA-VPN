using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed partial class ZapretManager
{
	private int selectionGeneration;
	private readonly System.Text.StringBuilder launchLog = new System.Text.StringBuilder();
	private void CaptureLaunchOutput(object sender, DataReceivedEventArgs args)
	{
		if (string.IsNullOrWhiteSpace(args.Data)) return;
		lock (launchLog)
		{
			launchLog.AppendLine(LogSanitizer.Sanitize(args.Data));
			if (launchLog.Length > 8000) launchLog.Remove(0, launchLog.Length - 8000);
		}
	}
	public string StrategyStatus { get; private set; } = "Стратегия ещё не проверена";
	public event Action<string> StrategyProgress;
	private void ReportStrategy(string message)
	{
		StrategyStatus = message;
		StrategyProgress?.Invoke(message);
	}

	// Read only the winws command. Batch code is never executed.
	public static string ReadStrategyArguments(string script)
	{
		string text = Regex.Replace(File.ReadAllText(script), @"\^\s*\r?\n", " ");
		Match command = Regex.Match(text, "(?im)^start[^\\r\\n]*?\"%BIN%winws.exe\"[ \\t]+([^\\r\\n]+)\\r?$");
		if (!command.Success) throw new InvalidDataException("Неподдерживаемый формат стратегии: " + Path.GetFileName(script));
		string root = Path.GetDirectoryName(Path.GetFullPath(script));
		string args = command.Groups[1].Value.Trim();
		args = args.Replace("%BIN%", Path.Combine(root, "bin") + "\\")
			.Replace("%LISTS%", Path.Combine(root, "lists") + "\\")
			.Replace("%GameFilterTCP%", "12").Replace("%GameFilterUDP%", "12");
		if (Regex.IsMatch(args, @"%[A-Za-z_][A-Za-z0-9_]*%") || args.Contains("\r") || args.Contains("\n"))
			throw new InvalidDataException("Неизвестные переменные стратегии.");
		if (args.Length > 30000) throw new InvalidDataException("Стратегия слишком длинная.");
		return args;
	}

	private void LaunchStrategy(string script)
	{
		string args = ReadStrategyArguments(script);
		string bin = Path.Combine(Path.GetDirectoryName(script), "bin");
		string lists = Path.Combine(Path.GetDirectoryName(script), "lists");
		Directory.CreateDirectory(lists);
		foreach (string name in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
		{
			string path = Path.Combine(lists, name);
			if (!File.Exists(path)) File.WriteAllText(path, name.StartsWith("ipset") ? "203.0.113.113/32\n" : "domain.example.abc\n");
		}
		SetLifecycle(ZapretLifecycleState.Starting);
		lock (launchLog) launchLog.Clear();
		launcher = new Process { StartInfo = new ProcessStartInfo
		{
			FileName = Path.Combine(bin, "winws.exe"), Arguments = args,
			WorkingDirectory = bin, UseShellExecute = false, CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true, RedirectStandardError = true
		} };
		launcher.OutputDataReceived += CaptureLaunchOutput;
		launcher.ErrorDataReceived += CaptureLaunchOutput;
		if (!launcher.Start()) throw new InvalidOperationException("Не удалось запустить ядро Zapret.");
		launcher.BeginOutputReadLine();
		launcher.BeginErrorReadLine();
	}

	private void StopOwnedProcess()
	{
		if (launcher == null) return;
		if (!launcher.HasExited)
		{
			launcher.Kill();
			if (!launcher.WaitForExit(3000)) throw new InvalidOperationException("Ядро Zapret не подтвердило остановку.");
		}
		launcher.Dispose();
		launcher = null;
	}

	private void CheckSelection(int generation)
	{
		if (generation != Volatile.Read(ref selectionGeneration)) throw new OperationCanceledException("Подбор остановлен.");
	}

	public string[] GetStrategies()
	{
		return Directory.Exists(CurrentPath)
			? Directory.GetFiles(CurrentPath, "general*.bat").Select(Path.GetFileName)
				.OrderBy(name => name == "general.bat" ? 0 : 1).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
			: new string[0];
	}

	public async Task StartSelectedAsync(ZapretSettings settings)
	{
		if (IsRunning) throw new InvalidOperationException("Zapret уже работает. Сначала остановите текущий экземпляр.");
		string[] strategies = GetStrategies();
		string selected = string.IsNullOrWhiteSpace(settings.SelectedStrategy) ? "general.bat" : settings.SelectedStrategy;
		string name = strategies.FirstOrDefault(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
		if (name == null) throw new FileNotFoundException("Выбранная стратегия отсутствует. Выберите конфиг Zapret из списка.");
		int generation = Interlocked.Increment(ref selectionGeneration);
		try
		{
			ReportStrategy("Запуск: " + Path.GetFileNameWithoutExtension(name));
			LaunchStrategy(Path.Combine(CurrentPath, name));
			await Task.Delay(700);
			CheckSelection(generation);
			if (launcher == null || launcher.HasExited)
			{
				string details;
				lock (launchLog) details = launchLog.ToString().Trim();
				throw new InvalidOperationException("Ядро Zapret завершилось при запуске выбранной стратегии. " + details);
			}
			settings.SelectedStrategy = name;
			settings.StrategyCheckMessage = "Выбрано вручную: " + Path.GetFileNameWithoutExtension(name);
			ReportStrategy(settings.StrategyCheckMessage);
			SetLifecycle(ZapretLifecycleState.Running);
		}
		catch (Exception ex)
		{
			if (generation == Volatile.Read(ref selectionGeneration))
			{
				StopOwnedProcess();
				ReportStrategy("Не удалось запустить выбранную стратегию: " + LogSanitizer.Sanitize(ex.Message));
				SetLifecycle(ZapretLifecycleState.Error);
			}
			throw;
		}
	}
}

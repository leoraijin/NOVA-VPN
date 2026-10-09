using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed class CoreManager : IDisposable
{
	private Process process;

	private readonly StringBuilder log = new StringBuilder();

	private string configPath;

	private bool stopping;

	private int startGeneration;

	public DateTime ConnectedAtUtc { get; private set; }

	public string LastError { get; private set; } = "";

	public ConnectionVerification Verification { get; private set; } = new ConnectionVerification();

	public RoutingSettings LastConnectedRouting { get; private set; }

	public CoreStatus Status { get; private set; }

	public string CorePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core", "sing-box.exe");

	public bool IsCoreInstalled => File.Exists(CorePath);

	public string Logs
	{
		get
		{
			lock (log)
			{
				return LogSanitizer.Sanitize(log.ToString());
			}
		}
	}

	public event Action<CoreStatus, string> StatusChanged;

	public event Action<string> LogReceived;

	public event Action UnexpectedExit;

	public event Action<ConnectionVerification> VerificationChanged;

	public async Task StartAsync(VpnProfile profile, RoutingSettings routing)
	{
		if (Status != CoreStatus.Connected && Status != CoreStatus.Connecting)
		{
			if (!IsCoreInstalled)
			{
				throw new FileNotFoundException("Не найдено VPN-ядро sing-box.", CorePath);
			}
			Stop();
			int generation = System.Threading.Volatile.Read(ref startGeneration);
			stopping = false;
			LastError = "";
			lock (log)
			{
				log.Clear();
			}
			ChangeStatus(CoreStatus.Connecting, "Проверяем конфигурацию");
			string runtimeDir = Path.Combine(StateStore.DirectoryPath, "runtime");
			Directory.CreateDirectory(runtimeDir);
			configPath = Path.Combine(runtimeDir, "active.json");
			File.WriteAllText(contents: ConfigBuilder.Build(profile, routing), path: configPath, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			string validation = await RunCheckAsync(configPath);
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				SafeDeleteConfig();
				return;
			}
			if (!string.IsNullOrWhiteSpace(validation))
			{
				ChangeStatus(CoreStatus.Error, FriendlyError(validation));
				LastError = FriendlyError(validation);
				SafeDeleteConfig();
				throw new InvalidOperationException(FriendlyError(validation));
			}
			ProcessStartInfo start = new ProcessStartInfo
			{
				FileName = CorePath,
				Arguments = "run -c \"" + configPath + "\"",
				WorkingDirectory = Path.GetDirectoryName(CorePath),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				SafeDeleteConfig();
				return;
			}
			process = new Process();
			process.StartInfo = start;
			process.EnableRaisingEvents = true;
			process.OutputDataReceived += OnData;
			process.ErrorDataReceived += OnData;
			process.Exited += OnExited;
			Process startedProcess = process;
			process.Start();
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				try { if (!startedProcess.HasExited) startedProcess.Kill(); } catch { }
				try { startedProcess.Dispose(); } catch { }
				if (ReferenceEquals(process, startedProcess)) process = null;
				SafeDeleteConfig();
				return;
			}
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			await Task.Delay(1100);
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				return;
			}
			if (process == null || process.HasExited)
			{
				string raw = LastMeaningfulLog();
				LastError = FriendlyError(raw);
				ChangeStatus(CoreStatus.Error, LastError);
				throw new InvalidOperationException(LastError);
			}
			ConnectedAtUtc = DateTime.UtcNow;
			LastConnectedRouting = RoutingOverlayService.Clone(routing);
			ChangeStatus(CoreStatus.Connected, "Защищено");
			_ = VerifyConnectionAsync();
		}
	}

	public void Stop()
	{
		System.Threading.Interlocked.Increment(ref startGeneration);
		stopping = true;
		Process process = this.process;
		this.process = null;
		if (process != null)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill();
					process.WaitForExit(3000);
				}
			}
			catch
			{
			}
			try
			{
				process.Dispose();
			}
			catch
			{
			}
		}
		SafeDeleteConfig();
		ConnectedAtUtc = default(DateTime);
		Verification = new ConnectionVerification();
		ChangeStatus(CoreStatus.Disconnected, "Не подключено");
	}

	public async Task<int> TestLatencyAsync(VpnProfile profile)
	{
		return (await HealthCheckService.TestAsync(profile)).LatencyMs;
	}

	public async Task<string> ValidateAsync(VpnProfile profile, RoutingSettings routing)
	{
		if (!IsCoreInstalled)
		{
			return "Не найдено VPN-ядро sing-box.";
		}
		string runtimeDir = Path.Combine(StateStore.DirectoryPath, "runtime");
		Directory.CreateDirectory(runtimeDir);
		string path = Path.Combine(runtimeDir, "check-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			File.WriteAllText(path, ConfigBuilder.Build(profile, routing), new UTF8Encoding(false));
			return await RunCheckAsync(path);
		}
		catch (Exception ex)
		{
			return LogSanitizer.Sanitize(ex.Message);
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
	}

	public async Task<string> GetVersionAsync()
	{
		if (!IsCoreInstalled)
		{
			return "не установлено";
		}
		ProcessStartInfo start = new ProcessStartInfo
		{
			FileName = CorePath,
			Arguments = "version",
			WorkingDirectory = Path.GetDirectoryName(CorePath),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		using (Process version = Process.Start(start))
		{
			string output = await version.StandardOutput.ReadToEndAsync();
			string error = await version.StandardError.ReadToEndAsync();
			await Task.Run(() => version.WaitForExit(5000));
			return LogSanitizer.Sanitize(string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim());
		}
	}

	public async Task<ConnectionVerification> VerifyConnectionAsync()
	{
		ConnectionVerification result = new ConnectionVerification();
		try
		{
			Task<IPAddress[]> dns = Dns.GetHostAddressesAsync("cloudflare.com");
			if (await Task.WhenAny(dns, Task.Delay(5000)) == dns && (await dns).Length > 0)
			{
				result.DnsOk = true;
			}
			Stopwatch watch = Stopwatch.StartNew();
			using (HttpClient client = new HttpClient())
			{
				client.Timeout = TimeSpan.FromSeconds(7.0);
				using (HttpResponseMessage response = await client.GetAsync("https://cp.cloudflare.com/generate_204"))
				{
					watch.Stop();
					result.InternetOk = response.IsSuccessStatusCode;
					result.InternetLatencyMs = (int)Math.Max(1L, watch.ElapsedMilliseconds);
				}
			}
			result.Message = result.DnsOk && result.InternetOk ? "DNS и интернет работают через активный маршрут." : "Туннель запущен, но контрольный запрос не прошёл.";
		}
		catch (Exception ex)
		{
			result.Message = "Контрольный запрос: " + LogSanitizer.Sanitize(ex.Message);
		}
		if (Status == CoreStatus.Connected)
		{
			Verification = result;
			VerificationChanged?.Invoke(result);
		}
		return result;
	}

	private async Task<string> RunCheckAsync(string path)
	{
		ProcessStartInfo start = new ProcessStartInfo
		{
			FileName = CorePath,
			Arguments = "check -c \"" + path + "\"",
			WorkingDirectory = Path.GetDirectoryName(CorePath),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			StandardErrorEncoding = Encoding.UTF8,
			StandardOutputEncoding = Encoding.UTF8
		};
		Process check = Process.Start(start);
		try
		{
			string stdout = await check.StandardOutput.ReadToEndAsync();
			string stderr = await check.StandardError.ReadToEndAsync();
			await Task.Run(delegate
			{
				check.WaitForExit();
			});
			return (check.ExitCode == 0) ? "" : (stderr + Environment.NewLine + stdout).Trim();
		}
		finally
		{
			if (check != null)
			{
				((IDisposable)check).Dispose();
			}
		}
	}

	private void OnData(object sender, DataReceivedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(e.Data))
		{
			return;
		}
		lock (log)
		{
			log.AppendLine(LogSanitizer.Sanitize(e.Data));
			if (log.Length > 120000)
			{
				log.Remove(0, 40000);
			}
		}
		this.LogReceived?.Invoke(LogSanitizer.Sanitize(e.Data));
	}

	private void OnExited(object sender, EventArgs e)
	{
		if (process == sender)
		{
			process = null;
			SafeDeleteConfig();
			ConnectedAtUtc = default(DateTime);
			if (!stopping)
			{
				LastError = FriendlyError(LastMeaningfulLog());
				ChangeStatus(CoreStatus.Error, LastError);
				UnexpectedExit?.Invoke();
			}
		}
	}

	private string LastMeaningfulLog()
	{
		string text = Logs.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return "VPN-ядро завершило работу.";
		}
		string[] array = text.Replace("\r", "").Split('\n');
		return array[array.Length - 1];
	}

	private string FriendlyError(string raw)
	{
		raw = (raw ?? "").Trim();
		string text = raw.ToLowerInvariant();
		if (text.Contains("permission") || text.Contains("access is denied"))
		{
			return "Windows не дала создать TUN-адаптер. Запустите NOVA от имени администратора.";
		}
		if (text.Contains("certificate"))
		{
			return "Не удалось проверить сертификат VPN-сервера. Проверьте SNI и параметры TLS.";
		}
		if (text.Contains("connection refused"))
		{
			return "Сервер отклонил подключение. Проверьте адрес и порт.";
		}
		if (text.Contains("no such host") || text.Contains("name resolution") || text.Contains("lookup"))
		{
			return "Не удалось найти VPN-сервер через DNS. Проверьте адрес и подключение к интернету.";
		}
		if (text.Contains("timeout") || text.Contains("deadline exceeded"))
		{
			return "Сервер не ответил вовремя. Проверьте сеть или выберите другой сервер.";
		}
		if (text.Contains("authentication") || text.Contains("unauthorized"))
		{
			return "Сервер отклонил авторизацию. Проверьте ключ или обновите подписку.";
		}
		if (text.Contains("unknown field") || text.Contains("decode config"))
		{
			return "В ключе есть параметры, которые не поддерживает текущее ядро. Подробности доступны в журнале.";
		}
		if (raw.Length > 190)
		{
			raw = raw.Substring(raw.Length - 190);
		}
		if (!string.IsNullOrWhiteSpace(raw))
		{
			return raw;
		}
		return "Не удалось запустить VPN-ядро.";
	}

	private void ChangeStatus(CoreStatus status, string message)
	{
		Status = status;
		this.StatusChanged?.Invoke(status, message);
	}

	private void SafeDeleteConfig()
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
			{
				File.Delete(configPath);
			}
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		Stop();
	}
}

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Linq;
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
	private VpnProfile connectedProfile;
	private bool verificationBusy;
    private GameConnectionMonitor gameMonitor;
    public Task<System.Collections.Generic.List<GameEndpoint>> GameEndpointsAsync(InstalledGame game, System.Threading.CancellationToken token)
    {
        if (Status != CoreStatus.Connected || gameMonitor == null) throw new InvalidOperationException("Для определения соединений подключите обычный профиль sing-box в NOVA.");
        return gameMonitor.ReadAsync(game, token);
    }

	public DateTime ConnectedAtUtc { get; private set; }

	public string LastError { get; private set; } = "";

	public ConnectionVerification Verification { get; private set; } = new ConnectionVerification();

	public RoutingSettings LastConnectedRouting { get; private set; }
	public Task<ConnectionVerification> InitialVerification { get; private set; }
	public string ActiveProfileId => connectedProfile?.Id;

	public CoreStatus Status { get; private set; }

	public VpnEngine Engine { get; private set; }
    public string ProbeCorePath => VpnEngines.ExecutablePath(VpnEngine.SingBox);
    public string CorePath => VpnEngines.ExecutablePath(Engine);
    public void SelectEngine(VpnEngine engine)
    {
        if (process != null || Status == CoreStatus.Connected || Status == CoreStatus.Connecting)
            throw new InvalidOperationException("Сначала отключите VPN, затем выберите ядро.");
        if (!Enum.IsDefined(typeof(VpnEngine),engine)) throw new ArgumentOutOfRangeException(nameof(engine));
        Engine = engine;
    }
    private string BuildConfig(VpnProfile profile, RoutingSettings routing) => Engine == VpnEngine.Reality
        ? RealityConfigBuilder.Build(profile,routing) : ConfigBuilder.Build(profile,routing);

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
        // Gaming probes own separate SOCKS-only cores and private configs. They must not
        // prevent the normal VPN from reconnecting after an unrelated network outage.
		if (Status != CoreStatus.Connected && Status != CoreStatus.Connecting)
		{
			if (!IsCoreInstalled)
			{
				throw new FileNotFoundException("Не найдено выбранное VPN-ядро: " + VpnEngines.DisplayName(Engine), CorePath);
			}
			string preparedConfig = BuildConfig(profile, routing);
			Stop();
            using (var routeReadiness = Engine == VpnEngine.SingBox && profile.Protocol != "sing-box" ? new TunnelRoutingReadiness() : null) {
            if (routeReadiness != null) preparedConfig = routeReadiness.Apply(preparedConfig, routing.EnableIpv6);
            gameMonitor = Engine == VpnEngine.SingBox && profile.Protocol != "sing-box" ? new GameConnectionMonitor() : null;
            if (gameMonitor != null) preparedConfig = gameMonitor.Apply(preparedConfig);
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
			File.WriteAllText(contents: preparedConfig, path: configPath, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
                Arguments = VpnEngines.RunArguments(Engine, configPath),
                RedirectStandardInput = Engine == VpnEngine.Reality,
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
			process.StartInfo = OwnedCoreProcess.Wrap(start, configPath);
			process.EnableRaisingEvents = true;
			process.OutputDataReceived += OnData;
			process.ErrorDataReceived += OnData;
			process.Exited += OnExited;
			Process startedProcess = process;
			process.Start();
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				StopOwnedProcess(startedProcess);
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
            // A live sing-box process is not proof that Windows has finished publishing
            // the recreated TUN address. Starting a game during address detection can
            // bypass process_path matching because the core still sees a non-local source.
            if (Engine == VpnEngine.SingBox && profile.Protocol != "sing-box") {
                try {
                    if (!await WaitForWindowsTunnelAsync(generation, routing.EnableIpv6)) return;
                    if (!await routeReadiness.WaitAsync(() => generation == System.Threading.Volatile.Read(ref startGeneration) && !stopping && process != null && !process.HasExited, routing.EnableIpv6)) return;
                }
                catch (Exception ex) {
                    Stop(); LastError = ex.Message; ChangeStatus(CoreStatus.Error, LastError); throw;
                }
            }
			ConnectedAtUtc = DateTime.UtcNow;
			LastConnectedRouting = RoutingOverlayService.Clone(routing);
			connectedProfile = profile;
			ChangeStatus(CoreStatus.Connected, "Защищено");
			InitialVerification = VerifyConnectionAsync();
            }
		}
	}


    private async Task<bool> WaitForWindowsTunnelAsync(int generation, bool ipv6)
    {
        var clock = Stopwatch.StartNew();
        var required = ipv6 ? new[] { IPAddress.Parse("172.19.0.1"), IPAddress.Parse("fdfe:dcba:9876::1") } : new[] { IPAddress.Parse("172.19.0.1") };
        long stableSince = -1;
        while (clock.Elapsed < TimeSpan.FromSeconds(15)) {
            if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping) return false;
            if (process == null || process.HasExited) throw new IOException(FriendlyError(LastMeaningfulLog()));
            bool ready = false;
            try {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals("NOVA", StringComparison.OrdinalIgnoreCase));
                if (adapter != null && adapter.OperationalStatus == OperationalStatus.Up) {
                    var addresses = adapter.GetIPProperties().UnicastAddresses;
                    if (addresses.Any(a => required.Contains(a.Address) && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate))
                        throw new IOException("Windows обнаружила конфликт адреса VPN-интерфейса NOVA.");
                    ready = required.All(ip => addresses.Any(a => a.Address.Equals(ip) && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred));
                }
            } catch (NetworkInformationException) { /* Interface can disappear while Windows recreates it. */ }
            if (ready) {
                if (stableSince < 0) stableSince = clock.ElapsedMilliseconds;
                if (clock.ElapsedMilliseconds - stableSince >= 500) return true;
            } else stableSince = -1;
            await Task.Delay(100);
        }
        throw new IOException("Windows не завершила подготовку VPN-интерфейса за 15 секунд. Повторите подключение.");
    }

	public void Stop()
	{
		System.Threading.Interlocked.Increment(ref startGeneration);
		stopping = true;
        gameMonitor = null;
        Process process = this.process;
        if (process != null)
        {
            StopOwnedProcess(process);
            if (ReferenceEquals(this.process,process)) this.process = null;
            process.Dispose();
        }
		SafeDeleteConfig();
		ConnectedAtUtc = default(DateTime);
		connectedProfile = null;
		Verification = new ConnectionVerification();
		ChangeStatus(CoreStatus.Disconnected, "Не подключено");
	}

    private void StopOwnedProcess(Process owned)
    {
        if (owned == null || owned.HasExited) return;
        if (Engine == VpnEngine.Reality)
        {
            // Closing the private stdin pipe triggers native route/TUN cleanup.
            owned.StandardInput.Close();
            if (!owned.WaitForExit(10000))
                throw new InvalidOperationException("Ядро ещё завершает соединение. Повторите отключение; принудительная остановка не выполнена.");
        }
        else
        {
            owned.Kill();
            if (!owned.WaitForExit(3000)) throw new InvalidOperationException("VPN-ядро ещё не завершилось.");
        }
    }

	public async Task<int> TestLatencyAsync(VpnProfile profile)
	{
		return (await HealthCheckService.TestAsync(profile)).LatencyMs;
	}

	public async Task<string> ValidateAsync(VpnProfile profile, RoutingSettings routing)
	{
		if (!IsCoreInstalled)
		{
			return "Не найдено выбранное VPN-ядро: " + VpnEngines.DisplayName(Engine);
		}
		string runtimeDir = Path.Combine(StateStore.DirectoryPath, "runtime");
		Directory.CreateDirectory(runtimeDir);
		string path = Path.Combine(runtimeDir, "check-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			File.WriteAllText(path, BuildConfig(profile, routing), new UTF8Encoding(false));
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
			Arguments = VpnEngines.VersionArguments(Engine),
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
		if (verificationBusy || Status != CoreStatus.Connected) return Verification;
		verificationBusy = true;
		int generation = startGeneration;
		VpnProfile profile = connectedProfile;
		ConnectionVerification result = new ConnectionVerification { ProfileId = profile?.Id };
		try {
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
		if (profile != null && generation == startGeneration && Status == CoreStatus.Connected)
			result.ProfileProbe = await ServerMapHealthService.TestAsync(profile, ProbeCorePath,
				Path.Combine(StateStore.DirectoryPath, "runtime", "map-probes"), System.Threading.CancellationToken.None);
		result.Completed = true;
		result.CheckedUtc = DateTime.UtcNow;
		if (Status == CoreStatus.Connected && generation == startGeneration)
		{
			Verification = result;
			VerificationChanged?.Invoke(result);
		}
		return result;
		} finally { verificationBusy = false; }
	}

	private async Task<string> RunCheckAsync(string path)
	{
		ProcessStartInfo start = new ProcessStartInfo
		{
			FileName = CorePath,
            Arguments = VpnEngines.CheckArguments(Engine, path),
			WorkingDirectory = Path.GetDirectoryName(CorePath),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			StandardErrorEncoding = Encoding.UTF8,
			StandardOutputEncoding = Encoding.UTF8
		};
        using (Process check = Process.Start(start))
        {
            Task<string> stdout = check.StandardOutput.ReadToEndAsync();
            Task<string> stderr = check.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => check.WaitForExit(15000));
            if (!exited)
            {
                // Only this validation child is stopped; it never starts a tunnel.
                try { check.Kill(); } catch (InvalidOperationException) { }
                await Task.Run(() => check.WaitForExit(3000));
                return "Проверка конфигурации ядра не завершилась за 15 секунд.";
            }
            string output = await stdout;
            string error = await stderr;
            return check.ExitCode == 0 ? "" : LogSanitizer.Sanitize((error + Environment.NewLine + output).Trim());
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

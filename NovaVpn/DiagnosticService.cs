using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading.Tasks;

namespace NovaVpn;

public static class DiagnosticService
{
	public static async Task<DiagnosticReport> RunAsync(CoreManager core, VpnProfile profile, RoutingSettings routing)
	{
		DiagnosticReport report = new DiagnosticReport();
		report.Items.Add(Item("Права администратора", IsAdministrator(), IsAdministrator() ? "достаточно для Windows TUN" : "перезапустите клиент от имени администратора"));
		report.Items.Add(Item("VPN-ядро", core != null && core.IsCoreInstalled, core != null && core.IsCoreInstalled ? await ShortVersion(core) : "core\\sing-box.exe не найден"));
		report.Items.Add(CheckStateDirectory());
		report.Items.Add(CheckTunAdapter(core));
		report.Items.Add(CheckAdapters(routing));
		bool dnsProtected = routing != null && routing.StrictRoute && string.Equals(routing.DnsMode, "Secure", StringComparison.OrdinalIgnoreCase);
		report.Items.Add(Item("Защита DNS", dnsProtected, dnsProtected ? "DoH и строгая маршрутизация включены" : "для максимальной защиты включите защищённый DNS и строгий маршрут"));
		report.Items.Add(Item("Выбранный профиль", profile != null, profile == null ? "сервер не выбран" : profile.ProtocolLabel + " · " + SafeEndpoint(profile)));
		if (profile != null && core != null && core.IsCoreInstalled)
		{
			string validation = await core.ValidateAsync(profile, routing);
			report.Items.Add(Item("Конфигурация sing-box", string.IsNullOrWhiteSpace(validation), string.IsNullOrWhiteSpace(validation) ? "проверка пройдена" : LogSanitizer.Sanitize(validation)));
			ServerHealthResult health = await HealthCheckService.TestAsync(profile);
			report.Items.Add(Item("Доступность сервера", health.Success, health.Success ? health.LatencyMs + " мс · " + health.Message : health.Message));
		}
		report.Items.Add(await CheckInternetAsync());
		foreach (string warning in RoutingInspector.Validate(routing))
		{
			report.Items.Add(Item("Конфликт маршрутов", false, warning));
		}
		return report;
	}

	private static bool IsAdministrator()
	{
		try
		{
			WindowsIdentity identity = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	private static DiagnosticItem CheckStateDirectory()
	{
		try
		{
			Directory.CreateDirectory(StateStore.DirectoryPath);
			string path = Path.Combine(StateStore.DirectoryPath, ".write-test-" + Guid.NewGuid().ToString("N"));
			File.WriteAllText(path, "ok");
			File.Delete(path);
			return Item("Хранилище профилей", true, "доступно и защищается Windows DPAPI");
		}
		catch (Exception ex)
		{
			return Item("Хранилище профилей", false, ex.Message);
		}
	}

	private static DiagnosticItem CheckTunAdapter(CoreManager core)
	{
		try
		{
			NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
			NetworkInterface tun = Array.Find(adapters, adapter => adapter.Name.IndexOf("NOVA", StringComparison.OrdinalIgnoreCase) >= 0 || adapter.Description.IndexOf("sing", StringComparison.OrdinalIgnoreCase) >= 0);
			if (core != null && core.Status == CoreStatus.Connected)
			{
				return Item("TUN-адаптер", tun != null && tun.OperationalStatus == OperationalStatus.Up, tun == null ? "активный адаптер NOVA не найден" : tun.Name + " · " + tun.OperationalStatus);
			}
			return Item("TUN-адаптер", true, tun == null ? "создаётся во время подключения" : tun.Name + " · сейчас " + tun.OperationalStatus);
		}
		catch (Exception ex)
		{
			return Item("TUN-адаптер", false, ex.Message);
		}
	}

	private static DiagnosticItem CheckAdapters(RoutingSettings routing)
	{
		try
		{
			NetworkInterface[] active = Array.FindAll(NetworkInterface.GetAllNetworkInterfaces(), adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback);
			bool virtualBox = Array.Exists(active, adapter => adapter.Name.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0 || adapter.Description.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0);
			if (virtualBox && routing != null && routing.StrictRoute)
			{
				return Item("Сетевые адаптеры", false, "обнаружен VirtualBox; строгая маршрутизация может потребовать исключения");
			}
			return Item("Сетевые адаптеры", active.Length > 0, "активных: " + active.Length);
		}
		catch (Exception ex)
		{
			return Item("Сетевые адаптеры", false, ex.Message);
		}
	}

	private static async Task<DiagnosticItem> CheckInternetAsync()
	{
		try
		{
			Task<IPAddress[]> dns = Dns.GetHostAddressesAsync("cloudflare.com");
			if (await Task.WhenAny(dns, Task.Delay(5000)) != dns || (await dns).Length == 0)
			{
				return Item("DNS Windows", false, "домен cloudflare.com не разрешён за 5 секунд");
			}
			using (HttpClient client = new HttpClient())
			{
				client.Timeout = TimeSpan.FromSeconds(7);
				using (HttpResponseMessage response = await client.GetAsync("https://cp.cloudflare.com/generate_204"))
				{
					return Item("Доступ в интернет", response.IsSuccessStatusCode, "HTTPS ответ: " + (int)response.StatusCode);
				}
			}
		}
		catch (Exception ex)
		{
			return Item("Доступ в интернет", false, ex.Message);
		}
	}

	private static async Task<string> ShortVersion(CoreManager core)
	{
		string value = await core.GetVersionAsync();
		int newline = value.IndexOfAny(new char[] { '\r', '\n' });
		return newline > 0 ? value.Substring(0, newline) : value;
	}

	private static DiagnosticItem Item(string name, bool success, string details)
	{
		return new DiagnosticItem { Name = name, Success = success, Details = details ?? "" };
	}

	private static string SafeEndpoint(VpnProfile profile)
	{
		return string.IsNullOrWhiteSpace(profile.Server) ? "пользовательская конфигурация" : profile.Server + ":" + profile.Port;
	}
}

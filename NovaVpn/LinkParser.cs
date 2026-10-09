using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public static class LinkParser
{
	private const int MaxImportBytes = 4 * 1024 * 1024;
	private const int MaxProfiles = 2000;
	public static async Task<List<VpnProfile>> ImportAsync(string input)
	{
		return (await ImportDetailedAsync(input)).Profiles;
	}

	public static async Task<ImportResult> ImportDetailedAsync(string input)
	{
		input = (input ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(input))
		{
			throw new FormatException("Вставьте ссылку или ключ.");
		}
		if (Encoding.UTF8.GetByteCount(input) > MaxImportBytes)
		{
			throw new InvalidOperationException("Импорт слишком большой. Ограничение — 4 МБ.");
		}
		string subscriptionUrl = IsHttpUrl(input) ? input : "";
		if (!string.IsNullOrWhiteSpace(subscriptionUrl))
		{
			Uri requested = new Uri(subscriptionUrl);
			if (!string.IsNullOrWhiteSpace(requested.UserInfo))
			{
				throw new InvalidOperationException("Ссылка подписки не должна содержать логин или пароль в адресе.");
			}
			if (requested.Scheme != Uri.UriSchemeHttps && !requested.IsLoopback)
			{
				throw new InvalidOperationException("Подписка должна использовать HTTPS, чтобы ключ не передавался открытым текстом.");
			}
			using HttpClientHandler handler = new HttpClientHandler
			{
				AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate)
			};
			using HttpClient client = new HttpClient(handler);
			client.Timeout = TimeSpan.FromSeconds(20.0);
			client.DefaultRequestHeaders.UserAgent.ParseAdd("NOVA-VPN/2.0");
			using HttpResponseMessage response = await client.GetAsync(subscriptionUrl, HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();
			Uri finalUri = response.RequestMessage.RequestUri;
			if (finalUri.Scheme != Uri.UriSchemeHttps && !finalUri.IsLoopback)
			{
				throw new InvalidOperationException("Сервер подписки перенаправил запрос на небезопасный HTTP-адрес.");
			}
			if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 10 * 1024 * 1024)
			{
				throw new InvalidOperationException("Ответ подписки слишком большой.");
			}
			input = await ReadLimitedAsync(response.Content, MaxImportBytes);
		}
		input = TryDecodeSubscription(input);
		List<VpnProfile> result = new List<VpnProfile>();
		string[] lines = input.Replace("\r", "").Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		string[] array = lines;
		foreach (string text in array)
		{
			string text2 = text.Trim();
			if (string.IsNullOrWhiteSpace(text2) || text2.StartsWith("#"))
			{
				continue;
			}
			if (result.Count >= MaxProfiles)
			{
				throw new InvalidOperationException("В подписке слишком много профилей.");
			}
			try
			{
				VpnProfile vpnProfile = ParseSingle(text2);
				if (vpnProfile != null)
				{
					result.Add(vpnProfile);
				}
			}
			catch
			{
				if (lines.Length == 1)
				{
					throw;
				}
			}
		}
		if (result.Count == 0 && input.TrimStart().StartsWith("{"))
		{
			result.Add(ParseSingBoxJson(input));
		}
		if (result.Count == 0)
		{
			throw new FormatException("Не удалось распознать ключ. Поддерживаются VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC и sing-box JSON.");
		}
		if (!string.IsNullOrWhiteSpace(subscriptionUrl))
		{
			DateTime updated = DateTime.UtcNow;
			foreach (VpnProfile profile in result)
			{
				profile.SubscriptionUrl = subscriptionUrl;
				profile.SubscriptionUpdatedUtc = updated;
			}
		}
		return new ImportResult(result, subscriptionUrl);
	}

	private static async Task<string> ReadLimitedAsync(HttpContent content, int limit)
	{
		using (Stream stream = await content.ReadAsStreamAsync())
		using (MemoryStream buffer = new MemoryStream())
		{
			byte[] chunk = new byte[81920];
			int total = 0;
			int read;
			while ((read = await stream.ReadAsync(chunk, 0, chunk.Length)) > 0)
			{
				total += read;
				if (total > limit) throw new InvalidOperationException("Ответ подписки слишком большой.");
				buffer.Write(chunk, 0, read);
			}
			return Encoding.UTF8.GetString(buffer.ToArray());
		}
	}

	public static VpnProfile ParseSingle(string link)
	{
		if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
		{
			return ParseVmess(link);
		}
		if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
		{
			return ParseVless(link);
		}
		if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
		{
			return ParseTrojan(link);
		}
		if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
		{
			return ParseShadowsocks(link);
		}
		if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
		{
			return ParseHysteria2(link);
		}
		if (link.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase))
		{
			return ParseTuic(link);
		}
		if (link.TrimStart().StartsWith("{"))
		{
			return ParseSingBoxJson(link);
		}
		throw new FormatException("Неподдерживаемый формат ссылки.");
	}

	private static VpnProfile ParseVless(string link)
	{
		Uri uri = NewUri(link);
		Dictionary<string, string> query = ParseQuery(uri.Query);
		VpnProfile vpnProfile = Basic(uri, "vless");
		vpnProfile.UserId = Uri.UnescapeDataString(uri.UserInfo);
		vpnProfile.Security = Get(query, "security", "none");
		vpnProfile.Transport = Get(query, "type", "tcp");
		if (string.Equals(vpnProfile.Transport, "xhttp", StringComparison.OrdinalIgnoreCase) || string.Equals(vpnProfile.Transport, "splithttp", StringComparison.OrdinalIgnoreCase))
		{
			throw new NotSupportedException("Профили XHTTP пропущены: это транспорт Xray, который не поддерживается ядром sing-box.");
		}
		vpnProfile.Host = Get(query, "host", "");
		vpnProfile.Path = Get(query, "path", "");
		vpnProfile.Sni = Get(query, "sni", Get(query, "serverName", ""));
		vpnProfile.PublicKey = Get(query, "pbk", "");
		vpnProfile.ShortId = Get(query, "sid", "");
		vpnProfile.Fingerprint = Get(query, "fp", "chrome");
		vpnProfile.Flow = Get(query, "flow", "");
		return vpnProfile;
	}

	private static VpnProfile ParseTrojan(string link)
	{
		Uri uri = NewUri(link);
		Dictionary<string, string> query = ParseQuery(uri.Query);
		VpnProfile vpnProfile = Basic(uri, "trojan");
		vpnProfile.Password = Uri.UnescapeDataString(uri.UserInfo);
		vpnProfile.Security = Get(query, "security", "tls");
		vpnProfile.Transport = Get(query, "type", "tcp");
		vpnProfile.Host = Get(query, "host", "");
		vpnProfile.Path = Get(query, "path", "");
		vpnProfile.Sni = Get(query, "sni", "");
		vpnProfile.Fingerprint = Get(query, "fp", "");
		return vpnProfile;
	}

	private static VpnProfile ParseHysteria2(string link)
	{
		if (link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
		{
			link = "hysteria2://" + link.Substring("hy2://".Length);
		}
		Uri uri = NewUri(link);
		Dictionary<string, string> query = ParseQuery(uri.Query);
		VpnProfile vpnProfile = Basic(uri, "hysteria2");
		vpnProfile.Password = Uri.UnescapeDataString(uri.UserInfo);
		vpnProfile.Sni = Get(query, "sni", "");
		vpnProfile.Obfs = Get(query, "obfs", "");
		vpnProfile.ObfsPassword = Get(query, "obfs-password", "");
		vpnProfile.Security = ((Get(query, "insecure", "0") == "1") ? "insecure" : "tls");
		return vpnProfile;
	}

	private static VpnProfile ParseTuic(string link)
	{
		Uri uri = NewUri(link);
		Dictionary<string, string> query = ParseQuery(uri.Query);
		VpnProfile vpnProfile = Basic(uri, "tuic");
		string text = Uri.UnescapeDataString(uri.UserInfo);
		int num = text.IndexOf(':');
		vpnProfile.UserId = ((num >= 0) ? text.Substring(0, num) : text);
		vpnProfile.Password = ((num >= 0) ? text.Substring(num + 1) : "");
		vpnProfile.Sni = Get(query, "sni", "");
		vpnProfile.Security = ((Get(query, "allow_insecure", "0") == "1") ? "insecure" : "tls");
		return vpnProfile;
	}

	private static VpnProfile ParseShadowsocks(string link)
	{
		string text = link.Substring(5);
		string text2 = "";
		int num = text.IndexOf('#');
		if (num >= 0)
		{
			text2 = Uri.UnescapeDataString(text.Substring(num + 1));
			text = text.Substring(0, num);
		}
		int num2 = text.IndexOf('?');
		if (num2 >= 0)
		{
			text = text.Substring(0, num2);
		}
		string text3 = text;
		if (!text.Contains("@"))
		{
			text3 = DecodeBase64(text);
		}
		int num3 = text3.LastIndexOf('@');
		if (num3 < 0)
		{
			throw new FormatException("Некорректная Shadowsocks-ссылка.");
		}
		string text4 = text3.Substring(0, num3);
		if (!text4.Contains(":"))
		{
			text4 = DecodeBase64(text4);
		}
		string text5 = text3.Substring(num3 + 1);
		int num4 = text5.LastIndexOf(':');
		if (num4 < 0)
		{
			throw new FormatException("В Shadowsocks-ссылке не указан порт.");
		}
		int num5 = text4.IndexOf(':');
		VpnProfile vpnProfile = new VpnProfile();
		vpnProfile.Name = (string.IsNullOrWhiteSpace(text2) ? text5.Substring(0, num4) : text2);
		vpnProfile.Protocol = "shadowsocks";
		vpnProfile.Server = text5.Substring(0, num4).Trim('[', ']');
		vpnProfile.Port = int.Parse(text5.Substring(num4 + 1));
		vpnProfile.Method = text4.Substring(0, num5);
		vpnProfile.Password = text4.Substring(num5 + 1);
		vpnProfile.Source = link;
		return vpnProfile;
	}

	private static VpnProfile ParseVmess(string link)
	{
		string input = DecodeBase64(link.Substring("vmess://".Length));
		JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
		if (!(javaScriptSerializer.DeserializeObject(input) is Dictionary<string, object> data))
		{
			throw new FormatException("Некорректная VMess-ссылка.");
		}
		VpnProfile vpnProfile = new VpnProfile();
		vpnProfile.Protocol = "vmess";
		vpnProfile.Name = StringValue(data, "ps", "VMess");
		vpnProfile.Server = StringValue(data, "add", "");
		vpnProfile.Port = IntValue(data, "port", 443);
		vpnProfile.UserId = StringValue(data, "id", "");
		vpnProfile.Security = StringValue(data, "tls", "none");
		vpnProfile.Transport = StringValue(data, "net", "tcp");
		vpnProfile.Host = StringValue(data, "host", "");
		vpnProfile.Path = StringValue(data, "path", "");
		vpnProfile.Sni = StringValue(data, "sni", "");
		vpnProfile.Fingerprint = StringValue(data, "fp", "");
		vpnProfile.Method = StringValue(data, "scy", "auto");
		vpnProfile.Source = link;
		return vpnProfile;
	}

	private static VpnProfile ParseSingBoxJson(string json)
	{
		object parsed;
		try
		{
			parsed = new JavaScriptSerializer().DeserializeObject(json);
		}
		catch (Exception ex)
		{
			throw new FormatException("Некорректный JSON sing-box.", ex);
		}
		if (!(parsed is Dictionary<string, object>))
		{
			throw new FormatException("Конфигурация sing-box должна быть JSON-объектом.");
		}
		VpnProfile vpnProfile = new VpnProfile();
		vpnProfile.Protocol = "sing-box";
		vpnProfile.Name = "Sing-box конфигурация";
		vpnProfile.RawJson = json;
		vpnProfile.Server = "custom";
		using (SHA256 sha = SHA256.Create())
		{
			vpnProfile.Source = "JSON:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "").ToLowerInvariant();
		}
		return vpnProfile;
	}

	private static VpnProfile Basic(Uri uri, string protocol)
	{
		string text = Uri.UnescapeDataString((uri.Fragment ?? "").TrimStart('#'));
		VpnProfile vpnProfile = new VpnProfile();
		vpnProfile.Protocol = protocol;
		vpnProfile.Name = (string.IsNullOrWhiteSpace(text) ? uri.Host : text);
		vpnProfile.Server = uri.Host;
		vpnProfile.Port = (uri.IsDefaultPort ? 443 : uri.Port);
		vpnProfile.Source = uri.AbsoluteUri;
		return vpnProfile;
	}

	private static Uri NewUri(string link)
	{
		if (!Uri.TryCreate(link, UriKind.Absolute, out var result))
		{
			throw new FormatException("Некорректная ссылка.");
		}
		return result;
	}

	private static Dictionary<string, string> ParseQuery(string query)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string[] array = (query ?? "").TrimStart('?').Split('&');
		foreach (string text in array)
		{
			if (!string.IsNullOrEmpty(text))
			{
				int num = text.IndexOf('=');
				string key = Uri.UnescapeDataString((num >= 0) ? text.Substring(0, num) : text);
				string value = Uri.UnescapeDataString((num >= 0) ? text.Substring(num + 1) : "");
				dictionary[key] = value;
			}
		}
		return dictionary;
	}

	private static string Get(Dictionary<string, string> query, string key, string fallback)
	{
		if (!query.TryGetValue(key, out var value))
		{
			return fallback;
		}
		return value;
	}

	private static string TryDecodeSubscription(string input)
	{
		string text = input.Trim();
		if (text.Contains("://") || text.StartsWith("{"))
		{
			return text;
		}
		try
		{
			string text2 = DecodeBase64(text);
			return (text2.Contains("://") || text2.TrimStart().StartsWith("{")) ? text2 : text;
		}
		catch
		{
			return text;
		}
	}

	private static string DecodeBase64(string value)
	{
		value = value.Trim().Replace('-', '+').Replace('_', '/');
		int num = value.Length % 4;
		if (num > 0)
		{
			value = value.PadRight(value.Length + 4 - num, '=');
		}
		return Encoding.UTF8.GetString(Convert.FromBase64String(value));
	}

	private static bool IsHttpUrl(string value)
	{
		if (Uri.TryCreate(value, UriKind.Absolute, out var result))
		{
			if (!(result.Scheme == Uri.UriSchemeHttp))
			{
				return result.Scheme == Uri.UriSchemeHttps;
			}
			return true;
		}
		return false;
	}

	private static string StringValue(Dictionary<string, object> data, string key, string fallback)
	{
		if (!data.TryGetValue(key, out var value) || value == null)
		{
			return fallback;
		}
		return Convert.ToString(value);
	}

	private static int IntValue(Dictionary<string, object> data, string key, int fallback)
	{
		if (!int.TryParse(StringValue(data, key, ""), out var result))
		{
			return fallback;
		}
		return result;
	}
}

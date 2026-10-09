using System.Collections;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class AppReleaseInfo
{
	public Version Version { get; set; }
	public string Tag { get; set; }
	public string AssetName { get; set; }
	public string DownloadUrl { get; set; }
	public string ReleaseUrl { get; set; }
	public long Size { get; set; }
	public string Sha256 { get; set; }
}

public sealed class AppUpdateService : IDisposable
{
	public static string QuoteWindowsArgument(string value)
	{
		if (value == null) throw new ArgumentNullException(nameof(value));
		var result = new System.Text.StringBuilder("\"");
		int slashes = 0;
		foreach (char ch in value)
		{
			if (ch == '\\') { slashes++; continue; }
			if (ch == '"') result.Append('\\', slashes * 2 + 1);
			else result.Append('\\', slashes);
			result.Append(ch);
			slashes = 0;
		}
		result.Append('\\', slashes * 2);
		return result.Append('"').ToString();
	}
	private const string Owner = "leoraijin";
	private const string Repository = "NOVA-VPN";
	private const long MaximumInstallerSize = 200L * 1024L * 1024L;
	public static string LatestReleasePageUrl => "https://github.com/" + Owner + "/" + Repository + "/releases/latest";

	private readonly HttpClient client = new HttpClient();
	private readonly HttpClient downloadClient = new HttpClient();

	public AppUpdateService()
	{
		client.Timeout = TimeSpan.FromSeconds(15);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("NOVA-VPN-Desktop/2.3.15");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		downloadClient.Timeout = TimeSpan.FromMinutes(3);
		downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("NOVA-VPN-Desktop/2.3.15");
	}

	public async Task<AppReleaseInfo> GetLatestAsync()
	{
		string endpoint = "https://api.github.com/repos/" + Uri.EscapeDataString(Owner) + "/" + Uri.EscapeDataString(Repository) + "/releases/latest";
		using (HttpResponseMessage response = await client.GetAsync(endpoint))
		{
			response.EnsureSuccessStatusCode();
			return ParseLatestRelease(await response.Content.ReadAsStringAsync());
		}
	}

	public static bool IsOlderThanInstalled(Version installedVersion, Version releaseVersion)
	{
		return installedVersion != null && releaseVersion != null && releaseVersion.CompareTo(installedVersion) < 0;
	}

	public static bool IsUpdateAvailable(Version installedVersion, Version releaseVersion)
	{
		return installedVersion != null && releaseVersion != null && releaseVersion.CompareTo(installedVersion) > 0;
	}

	public async Task DownloadInstallerAsync(AppReleaseInfo release, string destinationPath)
	{
		if (release == null || !IsInstallerDownloadUrl(release.DownloadUrl, release.Tag, release.AssetName))
			throw new InvalidDataException("Ссылка установщика NOVA VPN не прошла проверку.");
		if (release.Size <= 0 || release.Size > MaximumInstallerSize)
			throw new InvalidDataException("Размер установщика NOVA VPN не прошёл проверку.");
		if (String.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Не указан путь сохранения установщика.", nameof(destinationPath));

		string fullPath = Path.GetFullPath(destinationPath);
		string directory = Path.GetDirectoryName(fullPath);
		if (String.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("Не удалось определить папку временного установщика.");
		Directory.CreateDirectory(directory);
		bool complete = false;
		bool fileCreated = false;
		try
		{
			using (HttpResponseMessage response = await downloadClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
			{
				response.EnsureSuccessStatusCode();
				long? contentLength = response.Content.Headers.ContentLength;
				if (contentLength.HasValue && contentLength.Value != release.Size)
					throw new InvalidDataException("Размер установщика NOVA VPN не совпадает с данными GitHub.");

				long written = 0;
				using (Stream input = await response.Content.ReadAsStreamAsync())
				using (FileStream output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					fileCreated = true;
					byte[] buffer = new byte[65536];
					int read;
					while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
					{
						written += read;
						if (written > MaximumInstallerSize || written > release.Size)
							throw new InvalidDataException("Скачанный установщик NOVA VPN превышает допустимый размер.");
						await output.WriteAsync(buffer, 0, read);
					}
					output.Flush(true);
				}

				if (written != release.Size) throw new InvalidDataException("Установщик NOVA VPN скачан не полностью.");
			}

			using (FileStream installer = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				if (installer.Length < 2 || installer.ReadByte() != 'M' || installer.ReadByte() != 'Z')
					throw new InvalidDataException("Скачанный файл не является Windows-установщиком.");
				if (!String.IsNullOrWhiteSpace(release.Sha256))
				{
					installer.Position = 0;
					using (SHA256 sha256 = SHA256.Create())
					{
						string actualHash = BitConverter.ToString(sha256.ComputeHash(installer)).Replace("-", "").ToLowerInvariant();
						if (!String.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
							throw new InvalidDataException("SHA-256 установщика не совпадает с опубликованным GitHub.");
					}
				}
			}
			complete = true;
		}
		finally
		{
			if (!complete && fileCreated)
			{
				try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
			}
		}
	}

	public static AppReleaseInfo ParseLatestRelease(string json)
	{
		if (String.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("GitHub вернул пустые сведения о релизе.");
		Dictionary<string, object> release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
		string tag = StringValue(release, "tag_name");
		Version version;
		if (!Version.TryParse(tag.TrimStart('v', 'V'), out version)) throw new InvalidOperationException("Тег релиза NOVA VPN имеет некорректный формат версии.");
		if (version.Revision < 0) version = new Version(version.Major, version.Minor, version.Build, 0);
		string htmlUrl = StringValue(release, "html_url");
		if (!IsReleasePageUrl(htmlUrl)) throw new InvalidOperationException("Ссылка релиза NOVA VPN не прошла проверку.");
		if (!release.TryGetValue("assets", out object rawAssets) || !(rawAssets is IEnumerable assets)) throw new InvalidOperationException("В релизе NOVA VPN нет установщика.");

		foreach (object rawAsset in assets)
		{
			if (!(rawAsset is Dictionary<string, object> asset)) continue;
			string name = StringValue(asset, "name");
			string versionedName = name.StartsWith("NOVA VPN Setup ", StringComparison.OrdinalIgnoreCase)
				? name.Substring("NOVA VPN Setup ".Length)
				: name.StartsWith("NOVA.VPN.Setup.", StringComparison.OrdinalIgnoreCase)
					? name.Substring("NOVA.VPN.Setup.".Length)
				: "";
			if (String.IsNullOrWhiteSpace(versionedName) || !versionedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
			versionedName = versionedName.Substring(0, versionedName.Length - 4);
			Version assetVersion;
			if (!Version.TryParse(versionedName, out assetVersion)) continue;
			if (assetVersion.Revision < 0) assetVersion = new Version(assetVersion.Major, assetVersion.Minor, assetVersion.Build, 0);
			if (assetVersion != version) continue;

			string url = StringValue(asset, "browser_download_url");
			if (!IsInstallerDownloadUrl(url, tag, name)) throw new InvalidOperationException("Ссылка установщика NOVA VPN не прошла проверку.");
			long size;
			if (!Int64.TryParse(Convert.ToString(asset.ContainsKey("size") ? asset["size"] : null, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size <= 0 || size > MaximumInstallerSize)
				throw new InvalidOperationException("Размер установщика в GitHub некорректен.");
			string digest = StringValue(asset, "digest");
			if (!String.IsNullOrWhiteSpace(digest))
			{
				const string prefix = "sha256:";
				if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || digest.Length != prefix.Length + 64)
					throw new InvalidOperationException("Хеш установщика в GitHub имеет неподдерживаемый формат.");
				digest = digest.Substring(prefix.Length);
				for (int i = 0; i < digest.Length; i++)
					if (!Uri.IsHexDigit(digest[i])) throw new InvalidOperationException("Хеш установщика в GitHub содержит недопустимые символы.");
			}
			return new AppReleaseInfo { Version = version, Tag = tag, AssetName = name, DownloadUrl = url, ReleaseUrl = htmlUrl, Size = size, Sha256 = digest };
		}
		throw new InvalidOperationException("В последнем релизе GitHub не найден установщик Windows для этой версии.");
	}

	private static bool IsReleasePageUrl(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri)) return false;
		string expectedPath = "/" + Owner + "/" + Repository + "/releases/";
		return String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
			&& String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			&& uri.AbsolutePath.StartsWith(expectedPath, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsInstallerDownloadUrl(string value, string tag, string assetName)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri)) return false;
		string expectedPrefix = "/" + Owner + "/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
		bool trustedReleaseUrl = String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
			&& String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			&& uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
		if (!trustedReleaseUrl) return false;
		string actualAssetName = Uri.UnescapeDataString(uri.AbsolutePath.Substring(expectedPrefix.Length));
		return String.Equals(actualAssetName, assetName, StringComparison.OrdinalIgnoreCase);
	}

	private static string StringValue(Dictionary<string, object> data, string key)
	{
		return data != null && data.TryGetValue(key, out object value) ? value as string ?? Convert.ToString(value) : "";
	}

	public void Dispose() { client.Dispose(); downloadClient.Dispose(); }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class ZapretReleaseInfo
{
	public string Version { get; set; }
	public string Commit { get; set; }
	public string DownloadUrl { get; set; }
	public bool IsReleaseArchive { get; set; }
	public string SourceUrl { get; set; }
	public string Sha256 { get; set; }
}

public sealed class ZapretReleaseService
{
	private readonly HttpClient client = new HttpClient();

	public ZapretReleaseService()
	{
		client.Timeout = TimeSpan.FromMinutes(5);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("NOVA-VPN/2.1");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
	}

	public async Task<ZapretReleaseInfo> GetLatestAsync(ZapretSettings settings)
	{
		string owner = Uri.EscapeDataString(settings.RepositoryOwner);
		string repo = Uri.EscapeDataString(settings.RepositoryName);
		string branch = Uri.EscapeDataString(string.IsNullOrWhiteSpace(settings.RepositoryBranch) ? "master" : settings.RepositoryBranch);
		string source = "https://github.com/" + settings.RepositoryOwner + "/" + settings.RepositoryName;
		string releaseUrl = "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";
		using (HttpResponseMessage releaseResponse = await client.GetAsync(releaseUrl))
		{
			if (releaseResponse.IsSuccessStatusCode)
			{
				Dictionary<string, object> release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await releaseResponse.Content.ReadAsStringAsync());
				string tag = StringValue(release, "tag_name");
				string archive = ReleaseAssetUrl(release);
				string digest = ReleaseAssetDigest(release);
				if (string.IsNullOrWhiteSpace(archive)) archive = StringValue(release, "zipball_url");
				if (string.IsNullOrWhiteSpace(archive)) archive = "https://github.com/" + settings.RepositoryOwner + "/" + settings.RepositoryName + "/archive/refs/heads/" + branch + ".zip";
				return new ZapretReleaseInfo { Version = string.IsNullOrWhiteSpace(tag) ? "release" : tag, DownloadUrl = archive, Sha256 = digest, SourceUrl = source, IsReleaseArchive = true };
			}
		}
		string commitUrl = "https://api.github.com/repos/" + owner + "/" + repo + "/commits/" + branch;
		using (HttpResponseMessage commitResponse = await client.GetAsync(commitUrl))
		{
			commitResponse.EnsureSuccessStatusCode();
			Dictionary<string, object> commit = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await commitResponse.Content.ReadAsStringAsync());
			string sha = StringValue(commit, "sha");
			if (string.IsNullOrWhiteSpace(sha)) throw new InvalidDataException("GitHub не вернул идентификатор версии Zapret.");
			return new ZapretReleaseInfo
			{
				Version = "commit-" + sha.Substring(0, Math.Min(12, sha.Length)),
				Commit = sha,
				DownloadUrl = "https://github.com/" + settings.RepositoryOwner + "/" + settings.RepositoryName + "/archive/refs/heads/" + branch + ".zip",
				SourceUrl = source,
				IsReleaseArchive = false
			};
		}
	}

	public async Task<string> DownloadAsync(ZapretReleaseInfo release, string destination)
	{
		if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out Uri uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || (uri.Host != "github.com" && uri.Host != "objects.githubusercontent.com" && uri.Host != "api.github.com"))
			throw new InvalidDataException("Источник обновления Zapret не прошёл проверку.");
		using (HttpResponseMessage response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
		{
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > SecurityGuard.MaxArchiveBytes) throw new InvalidDataException("Архив Zapret слишком большой.");
			using (Stream source = await response.Content.ReadAsStreamAsync())
			using (FileStream target = File.Create(destination))
			using (SHA256 sha = SHA256.Create())
			{
				byte[] buffer = new byte[81920];
				long total = 0;
				int read;
				while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
				{
					total += read;
					if (total > SecurityGuard.MaxArchiveBytes) throw new InvalidDataException("Архив Zapret слишком большой.");
					target.Write(buffer, 0, read);
					sha.TransformBlock(buffer, 0, read, null, 0);
				}
				sha.TransformFinalBlock(new byte[0], 0, 0);
				if (total < 1024) throw new InvalidDataException("Архив Zapret пустой или повреждён.");
				string actual = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
				if (!string.IsNullOrWhiteSpace(release.Sha256) && !string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма архива Zapret не совпала.");
			}
		}
		return destination;
	}

	private static string StringValue(Dictionary<string, object> data, string key)
	{
		return data != null && data.TryGetValue(key, out object value) ? value as string ?? Convert.ToString(value) : "";
	}

	private static string ReleaseAssetUrl(Dictionary<string, object> release)
	{
		if (release == null || !release.TryGetValue("assets", out object value) || !(value is object[] assets)) return "";
		foreach (object item in assets)
		{
			if (!(item is Dictionary<string, object> asset)) continue;
			string name = StringValue(asset, "name");
			string url = StringValue(asset, "browser_download_url");
			if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url)) return url;
		}
		return "";
	}

	private static string ReleaseAssetDigest(Dictionary<string, object> release)
	{
		if (release == null || !release.TryGetValue("assets", out object value) || !(value is object[] assets)) return "";
		foreach (object item in assets)
		{
			if (!(item is Dictionary<string, object> asset)) continue;
			string name = StringValue(asset, "name");
			if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
			string digest = StringValue(asset, "digest");
			if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return digest.Substring(7).Trim();
		}
		return "";
	}
}

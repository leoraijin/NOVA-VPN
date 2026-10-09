using System;
using System.Text.RegularExpressions;

namespace NovaVpn;

public static class LogSanitizer
{
	private static readonly Regex Uuid = new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
	private static readonly Regex Credentials = new Regex("(?i)(\"?(?:password|uuid|public_key|private_key|short_id)\"?\\s*[:=]\\s*\"?)([^\"\\s,}\\]]+)", RegexOptions.Compiled);
	private static readonly Regex UriUserInfo = new Regex(@"(?i)(vless|vmess|trojan|ss|hysteria2|hy2|tuic)://[^@\s]+@", RegexOptions.Compiled);

	public static string Sanitize(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value ?? "";
		}
		string result = Uuid.Replace(value, "********-****-****-****-************");
		result = Credentials.Replace(result, "$1***");
		return UriUserInfo.Replace(result, "$1://***@");
	}
}

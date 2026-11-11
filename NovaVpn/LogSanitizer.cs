using System;
using System.Text.RegularExpressions;

namespace NovaVpn;

public static class LogSanitizer
{
	private static readonly Regex Uuid = new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex JsonCredentials = new Regex("(?i)(\"(?:password|username|uuid|public_key|private_key|short_id)\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);
    // Native auth diagnostics do not quote values. A comma can be part of a
    // credential, so retain only the diagnostic prefix before the auth field.
    private static readonly Regex NativeAuth = new Regex(@"(?im)(\b(?:password|username)\s*[:=]\s*)[^\r\n]*", RegexOptions.Compiled);
	private static readonly Regex Credentials = new Regex("(?i)(\\b(?:password|username|uuid|public_key|private_key|short_id)\\s*[:=]\\s*)([^,\\r\\n}\\]]+)", RegexOptions.Compiled);
    private static readonly Regex ProxyAuthorization = new Regex(@"(?im)(proxy-authorization\s*:\s*)[^\r\n]+", RegexOptions.Compiled);
	private static readonly Regex UriUserInfo = new Regex(@"(?i)(vless|vmess|trojan|ss|hysteria2|hy2|tuic|http|https|socks|socks5)://[^@\s]+@", RegexOptions.Compiled);

	public static string Sanitize(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value ?? "";
		}
		string result = Uuid.Replace(value, "********-****-****-****-************");
        result = JsonCredentials.Replace(result, "$1\"***\"");
        result = NativeAuth.Replace(result, "$1***");
		result = Credentials.Replace(result, "$1***");
        result = ProxyAuthorization.Replace(result, "$1***");
		return UriUserInfo.Replace(result, "$1://***@");
	}
}

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

    private static readonly Regex ReportLinks = new Regex(
        @"(?i)\b(?:https?|vless|vmess|trojan|ss|ssr|hysteria2?|hy2|tuic|socks5?)://[^\s""<>]+",
        RegexOptions.Compiled);
    private static readonly Regex ReportTokens = new Regex(
        @"(?im)(\b(?:authorization|token|api[_-]?key|secret)\b""?\s*[:=]\s*)[^\r\n]+",
        RegexOptions.Compiled);

    internal static string SanitizeForReport(string value) =>
        ReportTokens.Replace(ReportLinks.Replace(Sanitize(value), "[ссылка скрыта]"), "$1***");

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

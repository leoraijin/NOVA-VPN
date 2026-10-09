using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaVpn;

public sealed class DiagnosticReport
{
	public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

	public List<DiagnosticItem> Items { get; } = new List<DiagnosticItem>();

	public bool Success => Items.All(item => item.Success);

	public override string ToString()
	{
		return "NOVA VPN — диагностический отчёт\r\n" +
			"Создан: " + CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
			"Windows: " + Environment.OSVersion.VersionString + "\r\n\r\n" +
			string.Join("\r\n", Items.Select(item => item.ToString())) +
			"\r\n\r\nСекреты и ключи автоматически скрыты.";
	}
}

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace NovaVpn;

public static class WindowsStartupService
{
	public const string TaskName = "NOVA VPN Startup";
	public static string BuildTaskXml(string executable, string userSid, bool connect)
	{
		if (string.IsNullOrWhiteSpace(executable) || executable.IndexOf('"') >= 0) throw new ArgumentException("Некорректный путь приложения.");
		var text = new StringBuilder();
		using (var xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true })) {
			xml.WriteStartElement("Task", "http://schemas.microsoft.com/windows/2004/02/mit/task"); xml.WriteAttributeString("version", "1.2");
			xml.WriteStartElement("Triggers"); xml.WriteStartElement("LogonTrigger"); xml.WriteElementString("Enabled", "true"); xml.WriteElementString("UserId", userSid); xml.WriteElementString("Delay", "PT10S"); xml.WriteEndElement(); xml.WriteEndElement();
			xml.WriteStartElement("Principals"); xml.WriteStartElement("Principal"); xml.WriteAttributeString("id", "Author"); xml.WriteElementString("UserId", userSid); xml.WriteElementString("LogonType", "InteractiveToken"); xml.WriteElementString("RunLevel", "HighestAvailable"); xml.WriteEndElement(); xml.WriteEndElement();
			xml.WriteStartElement("Settings"); xml.WriteElementString("MultipleInstancesPolicy", "IgnoreNew"); xml.WriteElementString("DisallowStartIfOnBatteries", "false"); xml.WriteElementString("StopIfGoingOnBatteries", "false"); xml.WriteElementString("StartWhenAvailable", "true"); xml.WriteElementString("ExecutionTimeLimit", "PT0S"); xml.WriteEndElement();
			xml.WriteStartElement("Actions"); xml.WriteAttributeString("Context", "Author"); xml.WriteStartElement("Exec"); xml.WriteElementString("Command", Path.GetFullPath(executable)); xml.WriteElementString("Arguments", connect ? "--windows-startup --background-connect" : "--windows-startup"); xml.WriteElementString("WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(executable))); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
		}
		return text.ToString();
	}

	public static Task ApplyAsync(bool enabled, bool connect, string executable) => Task.Run(() => {
		string file = null;
		try {
			if (!enabled) { if (Run("/Query /TN \"" + TaskName + "\"", false) == 0) Run("/Delete /TN \"" + TaskName + "\" /F", true); return; }
			Directory.CreateDirectory(StateStore.DirectoryPath);
			file = Path.Combine(StateStore.DirectoryPath, "startup-" + Guid.NewGuid().ToString("N") + ".xml");
			File.WriteAllText(file, BuildTaskXml(executable, WindowsIdentity.GetCurrent().User.Value, connect), Encoding.Unicode);
			Run("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", true);
		} finally { if (file != null && File.Exists(file)) File.Delete(file); }
	});

	private static int Run(string arguments, bool requireSuccess)
	{
		using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
			var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Планировщик Windows не ответил."); }
			if (requireSuccess && process.ExitCode != 0) throw new InvalidOperationException("Не удалось изменить автозапуск. Запустите NOVA с правами администратора. " + LogSanitizer.Sanitize(error.GetAwaiter().GetResult()));
			return process.ExitCode;
		}
	}
}

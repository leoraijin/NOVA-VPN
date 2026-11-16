using System;
using System.IO;

namespace NovaVpn;

public enum VpnEngine { SingBox = 0, Reality = 1 }

public static class VpnEngines
{
    public const string RealitySourceRevision = "ac47b5563604fb185b6c63ed60db49325dcb1c2f";

    public static string DisplayName(VpnEngine engine) => engine == VpnEngine.Reality
        ? "Reality Core · экспериментальное" : "sing-box · по умолчанию";

    public static string ExecutablePath(VpnEngine engine) => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "core",
        engine == VpnEngine.Reality ? "reality-client.exe" : "sing-box.exe");

    public static string RunArguments(VpnEngine engine, string configPath) => engine == VpnEngine.Reality
        ? "--config " + QuotePath(configPath) + " --exit-on-stdin-eof"
        : "run -c " + QuotePath(configPath);

    public static string CheckArguments(VpnEngine engine, string configPath) => engine == VpnEngine.Reality
        ? "--config " + QuotePath(configPath) + " --check"
        : "check -c " + QuotePath(configPath);

    public static string VersionArguments(VpnEngine engine) => engine == VpnEngine.Reality ? "--version" : "version";

    private static string QuotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
            throw new ArgumentException("Некорректный путь к конфигурации.", nameof(path));
        return "\"" + Path.GetFullPath(path) + "\"";
    }
}

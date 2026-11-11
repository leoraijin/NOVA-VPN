using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaVpn;

// Initial onboarding supports explicitly pinned Ed25519 hosts. It never discovers
// or accepts trust automatically, and does not change ~/.ssh/config/known_hosts.
public sealed class VpsSshTarget
{
    public string Host { get; }
    public int Port { get; }
    public string User { get; }
    public string HostPublicKey { get; }
    public string HostFingerprint { get; }
    public string IdentityKey { get; }
    public VpsSshTarget(string host, int port, string user, string hostPublicKey)
    {
        if (String.IsNullOrWhiteSpace(host) || host.Length > 253 || host != host.Trim() || host.Any(c => Char.IsControl(c) || Char.IsWhiteSpace(c))) throw new ArgumentException("Укажите имя или IP-адрес SSH-сервера.");
        IPAddress ip;
        if (IPAddress.TryParse(host, out ip)) {
            if (host.Contains('%') || host.Contains('[') || host.Contains(']')) throw new ArgumentException("Укажите IP без скобок и зоны интерфейса.");
            Host = ip.ToString();
        } else {
            if (Uri.CheckHostName(host) != UriHostNameType.Dns) throw new ArgumentException("Некорректное имя SSH-сервера.");
            string ascii = new IdnMapping().GetAscii(host).TrimEnd('.').ToLowerInvariant();
            if (ascii.Length == 0 || ascii.Length > 253 || ascii.Split('.').Any(label => !Regex.IsMatch(label, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant))) throw new ArgumentException("Некорректное имя SSH-сервера.");
            Host = ascii;
        }
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (user == null || !Regex.IsMatch(user, @"\A[a-z_][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Укажите имя пользователя Linux.");
        Port = port; User = user;
        byte[] blob = ParseEd25519(hostPublicKey);
        HostPublicKey = "ssh-ed25519 " + Convert.ToBase64String(blob);
        using (var hash = SHA256.Create()) HostFingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(blob)).TrimEnd('=');
        using (var hash = SHA256.Create()) IdentityKey = String.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes("nova-vps-target/1\0" + Host + "\0" + Port.ToString(CultureInfo.InvariantCulture) + "\0" + User + "\0" + HostPublicKey)).Select(b => b.ToString("x2")));
    }
    static byte[] ParseEd25519(string key)
    {
        if (key == null || key.Length > 128 || !Regex.IsMatch(key, @"\Assh-ed25519 [A-Za-z0-9+/]+={0,2}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Укажите публичный Ed25519-ключ сервера без комментария.");
        byte[] blob;
        try { blob = Convert.FromBase64String(key.Substring(12)); } catch (FormatException) { throw new ArgumentException("Некорректный публичный SSH-ключ."); }
        // SSH wire strings: algorithm length/name, public-key length, public key.
        if (blob.Length != 51 || blob[0] != 0 || blob[1] != 0 || blob[2] != 0 || blob[3] != 11 || Encoding.ASCII.GetString(blob, 4, 11) != "ssh-ed25519" || blob[15] != 0 || blob[16] != 0 || blob[17] != 0 || blob[18] != 32) throw new ArgumentException("Некорректная структура публичного Ed25519-ключа.");
        if (Convert.ToBase64String(blob) != key.Substring(12)) throw new ArgumentException("Неканоническая запись публичного SSH-ключа.");
        return blob;
    }
    public string KnownHostsLine => "nova-vps-pinned " + HostPublicKey + "\n";
    public ProcessStartInfo CreatePreflightStartInfo(string sshExecutable, string privateKeyFile, string knownHostsFile)
        => CreateStartInfo(sshExecutable, privateKeyFile, knownHostsFile, "sh -s");
    internal ProcessStartInfo CreateAgentStartInfo(string sshExecutable, string privateKeyFile, string knownHostsFile, VpsAgentFrame frame)
    {
        if (frame == null || frame.TargetIdentityKey != IdentityKey) throw new ArgumentException("Пакет подготовлен для другого SSH-сервера.");
        return CreateStartInfo(sshExecutable, privateKeyFile, knownHostsFile, frame.RemoteCommand);
    }
    private ProcessStartInfo CreateStartInfo(string sshExecutable, string privateKeyFile, string knownHostsFile, string command)
    {
        string exe = FullLiteralPath(sshExecutable), key = FullLiteralPath(privateKeyFile), hosts = FullLiteralPath(knownHostsFile);
        var args = new List<string> { "-F", "NUL", "-T", "-p", Port.ToString(CultureInfo.InvariantCulture), "-l", User, "-i", key };
        foreach (string option in new[] {
            "BatchMode=yes", "IdentitiesOnly=yes", "IdentityAgent=none", "PreferredAuthentications=publickey", "PasswordAuthentication=no", "KbdInteractiveAuthentication=no",
            "StrictHostKeyChecking=yes", "HostKeyAlias=nova-vps-pinned", "HostKeyAlgorithms=ssh-ed25519", "UserKnownHostsFile=" + ConfigPath(hosts),
            "GlobalKnownHostsFile=NUL", "CheckHostIP=no", "VerifyHostKeyDNS=no", "UpdateHostKeys=no", "CanonicalizeHostname=no",
            "ProxyCommand=none", "ProxyJump=none", "ForwardAgent=no", "ForwardX11=no", "ClearAllForwardings=yes", "PermitLocalCommand=no",
            "ControlMaster=no", "ControlPath=none", "ControlPersist=no", "ConnectionAttempts=1", "ConnectTimeout=10", "ServerAliveInterval=5", "ServerAliveCountMax=2", "LogLevel=ERROR"
        }) { args.Add("-o"); args.Add(option); }
        args.Add("--"); args.Add(Host); args.Add(command);
        var info = new ProcessStartInfo(exe, String.Join(" ", args.Select(AppUpdateService.QuoteWindowsArgument))) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "never";
        return info;
    }
    static string ConfigPath(string value) => "\"" + value.Replace('\\', '/') + "\"";
    static string FullLiteralPath(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Any(c => Char.IsControl(c) || c == '"' || c == '%' || c == '$' || c == '*' || c == '?') || !Path.IsPathRooted(value) || value.StartsWith(@"\\") || value.Length < 3 || value[1] != ':' || (value[2] != '\\' && value[2] != '/') || value.Substring(2).Contains(':')) throw new ArgumentException("Выберите абсолютный локальный путь без подстановок.");
        return Path.GetFullPath(value);
    }
}

public sealed class VpsPreflightReport
{
    public string Kernel { get; private set; }
    public string Architecture { get; private set; }
    public uint UserId { get; private set; }
    public bool HasSystemd { get; private set; }
    public bool HasRequiredTools { get; private set; }
    public string ExistingUnit { get; private set; }
    // Platform readiness is not deployment, ownership, reachability or traffic proof.
    public bool PlatformReady => Kernel == "Linux" && (Architecture == "x86_64" || Architecture == "aarch64") && UserId == 0 && HasSystemd && HasRequiredTools && ExistingUnit != "unknown";
    public static VpsPreflightReport Parse(string output)
    {
        if (output == null || output.Length > 8192 || output.Any(c => Char.IsControl(c) && c != '\r' && c != '\n')) throw new InvalidDataException("Некорректный ответ проверки VPS.");
        string[] lines = output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length != 12 || lines[0] != "NOVA-VPS-PREFLIGHT/2") throw new InvalidDataException("Неизвестный формат проверки VPS.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new[] { "kernel", "arch", "uid", "systemd", "sha256", "tar", "flock", "base64", "python3", "unit", "end" };
        foreach (string line in lines.Skip(1)) {
            int split = line.IndexOf('=');
            if (split <= 0 || !names.Contains(line.Substring(0, split)) || fields.ContainsKey(line.Substring(0, split))) throw new InvalidDataException("Неполный или неоднозначный ответ VPS.");
            fields.Add(line.Substring(0, split), line.Substring(split + 1));
        }
        uint uid;
        if (!Regex.IsMatch(fields["kernel"], @"\A[A-Za-z0-9_.-]{1,64}\z") || !Regex.IsMatch(fields["arch"], @"\A[A-Za-z0-9_-]{1,32}\z") || !UInt32.TryParse(fields["uid"], NumberStyles.None, CultureInfo.InvariantCulture, out uid) || fields["end"] != "1" || !new[] { "absent", "present", "unknown" }.Contains(fields["unit"])) throw new InvalidDataException("Некорректные поля проверки VPS.");
        foreach (string name in new[] { "systemd", "sha256", "tar", "flock", "base64", "python3" }) if (fields[name] != "0" && fields[name] != "1") throw new InvalidDataException("Некорректные возможности VPS.");
        return new VpsPreflightReport { Kernel = fields["kernel"], Architecture = fields["arch"], UserId = uid, HasSystemd = fields["systemd"] == "1", HasRequiredTools = new[] { "sha256", "tar", "flock", "base64", "python3" }.All(name => fields[name] == "1"), ExistingUnit = fields["unit"] };
    }
    // Constant read-only script; no user values, credentials or installation commands.
    public static string Script => "set -eu\nLC_ALL=C\nexport LC_ALL\nkernel=$(uname -s)\narch=$(uname -m)\nuid=$(id -u)\n" +
        "printf 'NOVA-VPS-PREFLIGHT/2\\n'\n" +
        "printf 'kernel=%s\\n' \"$kernel\"\nprintf 'arch=%s\\n' \"$arch\"\nprintf 'uid=%s\\n' \"$uid\"\n" +
        "if command -v systemctl >/dev/null 2>&1 && test -d /run/systemd/system; then printf 'systemd=1\\n'; else printf 'systemd=0\\n'; fi\n" +
        "for entry in sha256:sha256sum tar:tar flock:flock base64:base64 python3:python3; do name=${entry%%:*}; tool=${entry#*:}; if command -v \"$tool\" >/dev/null 2>&1; then printf '%s=1\\n' \"$name\"; else printf '%s=0\\n' \"$name\"; fi; done\n" +
        "unit=unknown\nif state=$(systemctl show --property=LoadState --value nova-vpn-managed.service 2>/dev/null); then case \"$state\" in not-found) unit=absent;; loaded|masked|error|bad-setting|merged|stub) unit=present;; esac; fi\nprintf 'unit=%s\\nend=1\\n' \"$unit\"\n";
}

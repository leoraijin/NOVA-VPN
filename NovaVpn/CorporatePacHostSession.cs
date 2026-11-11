using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class CorporatePacHostDescriptor
{
    public int Schema;
    public string Script, Target, ConfigPath, ConfigHash, UpstreamTag;
    public bool AllowDirect;
}

// The descriptor remains in the same current-user/SYSTEM private directory as the
// config. Only its filename crosses the owned host's command-line boundary.
public sealed class CorporatePacHostSession : IDisposable
{
    private readonly CorporatePacBridge bridge;
    private CorporatePacHostSession(CorporatePacBridge bridge) { this.bridge = bridge; }
    public static string Prepare(string configPath, CorporatePacQuery query, string upstreamTag, bool allowDirect)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        configPath = Path.GetFullPath(configPath);
        VerifyPrivateFile(configPath);
        using (var config = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] bytes = Read(config, 16 * 1024 * 1024);
            Placeholder(Decode(bytes), upstreamTag);
            var descriptor = new CorporatePacHostDescriptor { Schema = 1, Script = query.Script.AbsoluteUri,
                Target = query.Target.AbsoluteUri, AllowDirect = allowDirect, ConfigPath = configPath,
                ConfigHash = Hash(bytes), UpstreamTag = upstreamTag };
            return PrivateRuntimeFiles.WriteJson(Path.GetDirectoryName(configPath), new JavaScriptSerializer().Serialize(descriptor));
        }
    }
    public static CorporatePacHostSession Start(string descriptorPath, string expectedConfigPath)
    {
        descriptorPath = Path.GetFullPath(descriptorPath);
        expectedConfigPath = Path.GetFullPath(expectedConfigPath);
        if (descriptorPath.Equals(expectedConfigPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(descriptorPath).Equals(Path.GetDirectoryName(expectedConfigPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PAC descriptor не соответствует каталогу конфигурации.");
        VerifyPrivateFile(descriptorPath); VerifyPrivateFile(expectedConfigPath);
        using (var file = new FileStream(descriptorPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var config = new FileStream(expectedConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var descriptor = serializer.Deserialize<CorporatePacHostDescriptor>(new UTF8Encoding(false, true).GetString(Read(file, 16384)));
            if (descriptor == null || descriptor.Schema != 1 || descriptor.ConfigPath != expectedConfigPath ||
                descriptor.ConfigHash == null || descriptor.ConfigHash.Length != 64) throw new InvalidDataException("Некорректный PAC descriptor.");
            byte[] original = Read(config, 16 * 1024 * 1024);
            if (Hash(original) != descriptor.ConfigHash) throw new InvalidDataException("Конфигурация изменилась после подготовки PAC.");
            var root = Decode(original);
            var outbound = Placeholder(root, descriptor.UpstreamTag);
            var query = new CorporatePacQuery(descriptor.Script, descriptor.Target);
            var bridge = new CorporatePacBridge(query, descriptor.AllowDirect);
            try
            {
                outbound["server_port"] = bridge.Port;
                outbound["username"] = bridge.Username; outbound["password"] = bridge.Password;
                byte[] rewritten = new UTF8Encoding(false, true).GetBytes(serializer.Serialize(root));
                config.Position = 0; config.SetLength(0); config.Write(rewritten, 0, rewritten.Length); config.Flush(true);
                return new CorporatePacHostSession(bridge);
            }
            catch { bridge.Dispose(); throw; }
        }
    }
    private static Dictionary<string, object> Decode(byte[] bytes) =>
        new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(bytes));
    private static Dictionary<string, object> Placeholder(Dictionary<string, object> root, string tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 128 || tag.Any(char.IsControl) || root == null ||
            !root.TryGetValue("outbounds", out object raw) || !(raw is IEnumerable list)) throw new InvalidDataException("Нет выхода PAC.");
        var matches = list.Cast<object>().OfType<Dictionary<string, object>>().Where(o => o.TryGetValue("tag", out object value) && Equals(value, tag)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Выход PAC отсутствует или неоднозначен.");
        var outbound = matches[0];
        if (!outbound.TryGetValue("type", out object type) || !Equals(type, "http") ||
            !outbound.TryGetValue("server", out object server) || !Equals(server, "127.0.0.1") ||
            !outbound.TryGetValue("server_port", out object port) || !Equals(port, 1) ||
            outbound.Keys.Except(new[] { "type", "tag", "server", "server_port" }).Any())
            throw new InvalidDataException("PAC placeholder не соответствует ожидаемому локальному выходу.");
        return outbound;
    }
    private static byte[] Read(Stream stream, int maximum)
    {
        if (stream.Length < 2 || stream.Length > maximum) throw new InvalidDataException("Недопустимый размер PAC-файла.");
        using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
    }
    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static void VerifyPrivateFile(string path)
    {
        string directory = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path);
        if (Path.GetExtension(path) != ".json" || !name.StartsWith("workload-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.Substring(9), "N", out _) || Path.GetFileName(directory) != "native-configs")
            throw new InvalidDataException("Неизвестное имя PAC runtime-файла.");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Перенаправленный PAC runtime-каталог.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Перенаправленный PAC runtime-файл.");
        var user = WindowsIdentity.GetCurrent().User;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (FileSystemSecurity security in new FileSystemSecurity[] { Directory.GetAccessControl(directory), File.GetAccessControl(path) })
        {
            if (!security.GetOwner(typeof(SecurityIdentifier)).Equals(user)) throw new UnauthorizedAccessException("Чужой владелец PAC runtime.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(user) && !rule.IdentityReference.Equals(system))
                    throw new UnauthorizedAccessException("PAC runtime доступен посторонней учётной записи.");
        }
    }
    public void Dispose() => bridge.Dispose();
}

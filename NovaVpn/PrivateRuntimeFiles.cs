using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace NovaVpn;

public static class PrivateRuntimeFiles
{
    public static void PrepareDirectory(string path)
    {
        path = Path.GetFullPath(path);
        // Refuse a redirected runtime directory before creating secret-bearing files.
        for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Каталог конфигураций не должен быть ссылкой или точкой перенаправления.");
        var user = WindowsIdentity.GetCurrent().User;
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(path)) { security.SetOwner(user); Directory.CreateDirectory(path, security); }
        else
        {
            if (!Directory.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier)).Equals(user)) throw new UnauthorizedAccessException("Каталог конфигураций принадлежит другой учётной записи.");
            // Existing ownership was verified. Persist only the DACL: assigning
            // even the same owner needs WRITE_OWNER, unlike the owner's WRITE_DAC.
            Directory.SetAccessControl(path, security);
        }
    }

    public static string WriteJson(string directory, string json)
    {
        PrepareDirectory(directory);
        string path = Path.Combine(Path.GetFullPath(directory), "workload-" + Guid.NewGuid().ToString("N") + ".json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
        return path;
    }
    public static string WriteCoreConfig(string runtime, string json) => WriteJson(Path.Combine(runtime, "native-configs"), json);
}

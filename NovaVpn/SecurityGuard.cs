using System;
using System.IO;
using System.Security.Cryptography;

namespace NovaVpn;

public static class SecurityGuard
{
    public const long MaxArchiveBytes = 512L * 1024L * 1024L;
    public const long MaxArchiveEntryBytes = 128L * 1024L * 1024L;
    public const int MaxArchiveEntries = 10000;

    public static bool IsSafeArchivePath(string root, string entryName, out string fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(entryName)) return false;
        string normalized = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) return false;
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(rootPath, normalized));
        if (!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return false;
        fullPath = candidate;
        return true;
    }

    public static string Sha256(byte[] data)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}

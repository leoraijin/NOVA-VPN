using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Current-user DPAPI + private leaf + exclusive file lease. The caller also holds
// its SID Scheduler lease while interpreting or mutating task state.
public sealed class WindowsStartupTaskJournal : IWindowsStartupTaskJournal, IDisposable
{
    const int Limit = 2 * 1024 * 1024;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NOVA-Windows-startup-journal-v1");
    readonly string path, pending; FileStream lease;
    public static bool HasPending(string directory)
    {
        try { File.GetAttributes(Path.Combine(Path.GetFullPath(directory), "startup-intent.bin")); return true; }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    public WindowsStartupTaskJournal(string directory)
    {
        PrivateRuntimeFiles.PrepareDirectory(directory);
        path = Path.Combine(Path.GetFullPath(directory), "startup-intent.bin"); pending = Path.Combine(Path.GetDirectoryName(path), "startup.next");
        string lockPath = Path.Combine(Path.GetDirectoryName(path), "startup.lock");
        Check(path); Check(pending); Check(lockPath);
        lease = OpenPrivate(lockPath, FileMode.OpenOrCreate, FileOptions.None);
        try { Check(lockPath); } catch { lease.Dispose(); lease = null; throw; }
    }
    static SecurityIdentifier User() { using (var identity = WindowsIdentity.GetCurrent()) return identity.User; }
    static FileStream OpenPrivate(string file, FileMode mode, FileOptions options)
    {
        var user = User();
        var security = new FileSecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileStream(file, mode, FileSystemRights.FullControl, FileShare.None, 4096, options, security);
    }
    static void Check(string file)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(file); }
        catch (FileNotFoundException) { return; } catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Журнал не должен быть ссылкой или каталогом.");
        var security = File.GetAccessControl(file); var user = User();
        if (!security.GetOwner(typeof(SecurityIdentifier)).Equals(user)) throw new UnauthorizedAccessException("Журнал принадлежит другой учётной записи.");
        string system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        bool userFull = false, systemFull = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl || rule.IdentityReference.Value != user.Value && rule.IdentityReference.Value != system) throw new UnauthorizedAccessException("Недоверенные права журнала.");
            if (rule.IdentityReference.Value == user.Value) userFull = true; if (rule.IdentityReference.Value == system) systemFull = true;
        }
        if (!userFull || !systemFull) throw new UnauthorizedAccessException("Неполные права журнала.");
    }
    void Open() { if (lease == null) throw new ObjectDisposedException(nameof(WindowsStartupTaskJournal)); Check(path); Check(pending); }
    public StartupTaskRecoveryRecord Read()
    {
        Open();
        byte[] encrypted;
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 16 || stream.Length > Limit) throw new InvalidDataException("Некорректный размер журнала.");
                encrypted = new byte[(int)stream.Length]; int offset = 0;
                while (offset < encrypted.Length) { int count = stream.Read(encrypted, offset, encrypted.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            }
        }
        catch (FileNotFoundException) { return null; }
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length == 0 || plain.Length > Limit) throw new InvalidDataException("Некорректный журнал.");
            return new JavaScriptSerializer { MaxJsonLength = Limit, RecursionLimit = 16 }.Deserialize<StartupTaskRecoveryRecord>(new UTF8Encoding(false, true).GetString(plain)) ?? throw new InvalidDataException("Пустой журнал.");
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
    public void Write(StartupTaskRecoveryRecord record)
    {
        Open(); if (record == null) throw new ArgumentNullException(nameof(record));
        byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = Limit, RecursionLimit = 16 }.Serialize(record));
        byte[] encrypted;
        try { if (plain.Length > Limit) throw new InvalidDataException("Слишком большой журнал."); encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(plain, 0, plain.Length); }
        if (encrypted.Length > Limit) throw new InvalidDataException("Слишком большой журнал.");
        try
        {
            using (var stream = OpenPrivate(pending, FileMode.Create, FileOptions.WriteThrough))
            { stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true); }
            Check(pending); Check(path);
            if (HasPending(Path.GetDirectoryName(path))) File.Replace(pending, path, null); else File.Move(pending, path);
        }
        finally { Check(pending); if (File.Exists(pending)) File.Delete(pending); }
    }
    public void Clear() { Open(); File.Delete(path); }
    public void Dispose() { var previous = lease; lease = null; previous?.Dispose(); }
}

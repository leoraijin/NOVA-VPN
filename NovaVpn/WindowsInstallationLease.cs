using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NovaVpn
{
    // Shared client readers / exclusive servicing writer on one persistent file.
    // Cooperative coordination, not a privilege boundary against an altered client.
    public static class WindowsInstallationLease
    {
        public const string FileName = ".nova-install.lock";
        static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), AdminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), UsersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        static SecurityIdentifier User() { using (var id = WindowsIdentity.GetCurrent()) return id.User; }
        static bool Machine() { using (var id = WindowsIdentity.GetCurrent()) return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        static string Root(string root)
        {
            if (String.IsNullOrWhiteSpace(root) || root.Length < 4 || !Char.IsLetter(root[0]) || root[1] != ':' || root[2] != '\\' || root.IndexOf(':', 2) >= 0 || root.Any(Char.IsControl)) throw new ArgumentException("Нужна отдельная локальная папка установки.");
            string full = Path.GetFullPath(root).TrimEnd('\\');
            if (full.Length <= 3) throw new ArgumentException("Нужна отдельная папка установки.");
            for (string cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                var attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw new IOException("Папка установки перенаправлена или недоступна.");
            }
            return full;
        }
        static FileSecurity Descriptor()
        {
            var actor = Machine() ? AdminSid : User(); var security = new FileSecurity(); security.SetOwner(actor); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { actor, SystemSid }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.Read, AccessControlType.Allow)); return security;
        }
        public static void ValidateDescriptor(FileSecurity security, string userSid, bool machineRequired)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)); var user = new SecurityIdentifier(userSid);
            bool machine = owner.Equals(AdminSid) || owner.Equals(SystemSid);
            if (!security.AreAccessRulesProtected || !machine && (machineRequired || !owner.Equals(user))) throw new UnauthorizedAccessException("Недоверенный владелец блокировки установки.");
            var actor = machine ? AdminSid : user; bool actorFull = false, systemFull = false, readerSeen = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.IsInherited || rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None || rule.AccessControlType != AccessControlType.Allow) throw new UnauthorizedAccessException("Недоверенные права блокировки установки.");
                if (rule.IdentityReference.Equals(UsersSid))
                {
                    if (readerSeen || rule.FileSystemRights != (FileSystemRights.Read | FileSystemRights.Synchronize)) throw new UnauthorizedAccessException("Общие права блокировки должны разрешать только чтение."); readerSeen = true;
                }
                else if (rule.IdentityReference.Equals(actor))
                { if (actorFull || rule.FileSystemRights != FileSystemRights.FullControl) throw new UnauthorizedAccessException("Некорректные права владельца блокировки."); actorFull = true; }
                else if (rule.IdentityReference.Equals(SystemSid))
                { if (systemFull || rule.FileSystemRights != FileSystemRights.FullControl) throw new UnauthorizedAccessException("Некорректные системные права блокировки."); systemFull = true; }
                else throw new UnauthorizedAccessException("Чужие права блокировки установки.");
            }
            if (!actorFull || !systemFull) throw new UnauthorizedAccessException("Неполные права блокировки установки.");
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Information { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out Information info);
        static void ValidateFile(FileStream file, bool machineRequired)
        {
            Information info;
            if (!GetFileInformationByHandle(file.SafeFileHandle, out info)) throw new IOException("Не удалось проверить блокировку установки.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 || info.Links != 1 || info.SizeHigh != 0 || info.SizeLow != 0) throw new IOException("Блокировка установки должна быть отдельным пустым файлом.");
            ValidateDescriptor(file.GetAccessControl(), User().Value, machineRequired);
        }
        static FileStream Open(string path, bool writer)
        {
            var handle = CreateFile(path, writer ? 0x1F01FFu : 0x80000000u, writer ? 0u : 1u, IntPtr.Zero, 3, 0x200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error(); handle.Dispose();
                if (code == 2) throw new FileNotFoundException("Блокировка установки ещё не создана.", path);
                if (code == 5) throw new UnauthorizedAccessException("Нет доступа к блокировке установки. Повторите установку NOVA.");
                throw new IOException("Установка NOVA занята другим процессом или недоступна.", new Win32Exception(code));
            }
            try { return new FileStream(handle, writer ? FileAccess.ReadWrite : FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        static FileStream Create(string path)
        { return new FileStream(path, FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, Descriptor()); }
        static FileStream Acquire(string root, bool writer)
        {
            string path = Path.Combine(Root(root), FileName); FileStream stream;
            try { stream = Open(path, writer); }
            catch (FileNotFoundException)
            {
                try { using (var created = Create(path)) ValidateFile(created, Machine()); }
                catch (IOException error) { if (error.HResult != unchecked((int)0x80070050) && error.HResult != unchecked((int)0x800700B7)) throw; }
                stream = Open(path, writer);
            }
            try
            {
                ValidateFile(stream, writer && Machine());
                // Existing descriptors are validated, never broadened automatically.
                // Legacy installers remove their private gate on close; new files
                // are created with the read-only shared role from the beginning.
                if (!writer && (Directory.GetDirectories(root, ".nova-update-*", SearchOption.TopDirectoryOnly).Length != 0 || Directory.GetDirectories(root, ".nova-remove-*", SearchOption.TopDirectoryOnly).Length != 0))
                    throw new IOException("Установка NOVA не завершена. Повторите установку для восстановления файлов.");
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        public static FileStream AcquireClient(string root) { return Acquire(root, false); }
        public static FileStream AcquireMaintenance(string root) { return Acquire(root, true); }
    }
}

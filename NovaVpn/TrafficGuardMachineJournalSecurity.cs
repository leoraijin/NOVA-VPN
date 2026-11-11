using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NovaVpn;

internal static class GuardMachineJournalSecurity
{
    private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), AdminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier ActorOwner()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User.Equals(SystemSid)) return SystemSid;
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return AdminSid;
        }
        throw new UnauthorizedAccessException("Machine recovery journal requires SYSTEM or an elevated administrator.");
    }
    internal static FileSystemSecurity Security(bool directory, SecurityIdentifier owner)
    {
        if (!owner.Equals(SystemSid) && !owner.Equals(AdminSid)) throw new UnauthorizedAccessException("Untrusted machine journal owner.");
        FileSystemSecurity result = directory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
        result.SetOwner(owner); result.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { SystemSid, AdminSid })
        {
            var rule = new FileSystemAccessRule(sid, FileSystemRights.FullControl, directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
            if (directory) ((DirectorySecurity)result).AddAccessRule(rule); else ((FileSecurity)result).AddAccessRule(rule);
        }
        return result;
    }
    internal static void Validate(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Redirected machine journal object.");
        FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!owner.Equals(SystemSid) && !owner.Equals(AdminSid) || directory && !security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Untrusted machine journal ACL/owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(SystemSid) && !rule.IdentityReference.Equals(AdminSid)) throw new UnauthorizedAccessException("Machine journal grants access to an untrusted principal.");
    }
    internal static void PrepareDirectory(string path)
    {
        var actor = ActorOwner(); path = Path.GetFullPath(path);
        for (var ancestor = new DirectoryInfo(path); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Redirected machine journal directory.");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path, (DirectorySecurity)Security(true, actor));
        Validate(path, true); // Never adopt an existing ordinary-user directory.
    }
    internal static FileStream Open(string path, FileMode mode, FileShare share, FileOptions options)
    {
        var actor = ActorOwner();
        if (File.Exists(path)) Validate(path, false);
        return new FileStream(path, mode, FileSystemRights.FullControl, share, 4096, options, (FileSecurity)Security(false, actor));
    }
}

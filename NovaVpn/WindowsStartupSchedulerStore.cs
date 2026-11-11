using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Principal;

namespace NovaVpn;

// Local Task Scheduler only; no password, S4U, task execution or remote connection.
public sealed class WindowsStartupSchedulerStore : IWindowsStartupTaskStore, IDisposable
{
    readonly string sid, account, executable;
    object scheduler, folder;
    public WindowsStartupSchedulerStore(string executable)
    {
        this.executable = WindowsStartupTaskPolicy.ExecutablePath(executable); sid = WindowsUserInstance.CurrentSid();
        using (var identity = WindowsIdentity.GetCurrent()) account = identity.Name;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            ((dynamic)scheduler).Connect(); folder = ((dynamic)scheduler).GetFolder(@"\");
        }
        catch { Dispose(); throw; }
    }
    public IDisposable AcquireLease(string expectedSid)
    {
        if (expectedSid != sid) throw new UnauthorizedAccessException("Нельзя изменять автозапуск другого пользователя.");
        return WindowsUserInstance.TryAcquire("StartupTask");
    }
    void Name(string name)
    {
        if (!string.Equals(name, WindowsStartupTaskPolicy.NameForSid(sid), StringComparison.OrdinalIgnoreCase) && !string.Equals(name, WindowsStartupTaskPolicy.LegacyName, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Задание не относится к этому пользователю NOVA.");
        if (folder == null) throw new ObjectDisposedException(nameof(WindowsStartupSchedulerStore));
    }
    public StartupTaskSnapshot Read(string name)
    {
        Name(name); object task = null;
        try
        {
            try { task = ((dynamic)folder).GetTask(name); }
            catch (COMException error) when (error.ErrorCode == unchecked((int)0x80070002)) { return null; }
            catch (FileNotFoundException error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
            if (!string.Equals((string)((dynamic)task).Path, @"\" + name, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Неожиданный путь задания.");
            return new StartupTaskSnapshot(name, (string)((dynamic)task).Xml, (string)((dynamic)task).GetSecurityDescriptor(5));
        }
        finally { Release(task); }
    }
    public string NormalizeXml(string xml)
    {
        if (scheduler == null) throw new ObjectDisposedException(nameof(WindowsStartupSchedulerStore));
        if (xml == null || xml.Length > 131072) throw new ArgumentException("Слишком большое задание.");
        object definition = null;
        try { definition = ((dynamic)scheduler).NewTask(0); ((dynamic)definition).XmlText = xml; return (string)((dynamic)definition).XmlText; }
        finally { Release(definition); }
    }
    public void Register(StartupTaskSnapshot task, bool update)
    {
        Name(task.Name); bool strict = !string.Equals(task.Name, WindowsStartupTaskPolicy.LegacyName, StringComparison.OrdinalIgnoreCase);
        if (!WindowsStartupTaskPolicy.IsOwned(task, sid, account, executable, strict)) throw new UnauthorizedAccessException("Непроверенное задание не регистрируется.");
        if (update)
        {
            var previous = Read(task.Name);
            if (!WindowsStartupTaskPolicy.IsOwned(previous, sid, account, executable, strict)) throw new UnauthorizedAccessException("Существующее задание сменило владельца или команду.");
        }
        object registered = null;
        try
        {
            // CREATE/UPDATE + DONT_ADD_PRINCIPAL_ACE + IGNORE_REGISTRATION_TRIGGERS.
            registered = ((dynamic)folder).RegisterTask(task.Name, task.Xml, (update ? 4 : 2) | 16 | 32, sid, null, 3, task.Sddl);
        }
        finally { Release(registered); }
    }
    public void Delete(string name)
    {
        Name(name); var current = Read(name);
        if (!WindowsStartupTaskPolicy.IsOwned(current, sid, account, executable, !string.Equals(name, WindowsStartupTaskPolicy.LegacyName, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Непроверенное задание не удаляется.");
        ((dynamic)folder).DeleteTask(name, 0);
    }
    static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public void Dispose() { var oldFolder = folder; var oldScheduler = scheduler; folder = scheduler = null; Release(oldFolder); Release(oldScheduler); }
}

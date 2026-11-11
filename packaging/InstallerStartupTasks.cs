using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml;
using NovaVpn;

internal interface IInstallerStartupTaskStore
{
    StartupTaskSnapshot[] List();
    StartupTaskSnapshot Read(string name);
    string ResolveSid(string principal);
    void Delete(StartupTaskSnapshot expected);
    void Restore(StartupTaskSnapshot original, string sid);
    bool Equivalent(StartupTaskSnapshot actual, StartupTaskSnapshot original, string sid);
}

// Installation-wide cleanup; caller holds the install-root/registration leases.
// Never execute a task or infer ownership from a display name alone.
internal static class InstallerStartupTasks
{
    internal sealed class Entry
    {
        public StartupTaskSnapshot Task; public string Sid, Account;
    }
    internal static Entry Identify(StartupTaskSnapshot task, string executable, Func<string, string> resolve)
    {
        executable = WindowsStartupTaskPolicy.ExecutablePath(executable);
        var doc = new XmlDocument(); doc.XmlResolver = null;
        using (var reader = XmlReader.Create(new StringReader(task.Xml), new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 131072 })) doc.Load(reader);
        var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", WindowsStartupTaskPolicy.Namespace);
        string matchedCommand = null;
        foreach (XmlNode command in doc.SelectNodes("/t:Task/t:Actions/t:Exec/t:Command", ns))
        {
            try { string candidate = WindowsStartupTaskPolicy.ExecutablePath(command.InnerText); if (String.Equals(Path.GetDirectoryName(candidate), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase)) matchedCommand = candidate; }
            catch (ArgumentException) { }
        }
        if (matchedCommand == null) return null; // Another installation is not ours to service.
        var principals = doc.SelectNodes("/t:Task/t:Principals/t:Principal/t:UserId", ns);
        if (principals.Count != 1) throw new InvalidDataException("Неоднозначный владелец задания удаляемой установки.");
        string account = principals[0].InnerText;
        string sid = new SecurityIdentifier(resolve(account)).Value;
        bool strict = !String.Equals(task.Name, WindowsStartupTaskPolicy.LegacyName, StringComparison.OrdinalIgnoreCase);
        if (!WindowsStartupTaskPolicy.IsOwned(task, sid, account, matchedCommand, strict)) throw new UnauthorizedAccessException("Задание этой установки не прошло проверку владельца, команды или прав.");
        return new Entry { Task = task, Sid = sid, Account = account };
    }
    internal static void Remove(IInstallerStartupTaskStore store, string executable)
    {
        if (store == null) throw new ArgumentNullException("store");
        executable = WindowsStartupTaskPolicy.ExecutablePath(executable);
        var tasks = store.List();
        if (tasks == null || tasks.Length > 1024 || tasks.Any(t => t == null) || tasks.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) throw new InvalidDataException("Некорректный список заданий.");
        var plan = tasks.Select(t => Identify(t, executable, store.ResolveSid)).Where(t => t != null).OrderBy(t => t.Task.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var attempted = new List<Entry>();
        try
        {
            foreach (var entry in plan)
            {
                Same(store.Read(entry.Task.Name), entry.Task);
                attempted.Add(entry); // A failed COM reply may follow a successful deletion.
                store.Delete(entry.Task);
                if (store.Read(entry.Task.Name) != null) throw new IOException("Удаление задания автозапуска не подтверждено.");
            }
        }
        catch (Exception primary)
        {
            var errors = new List<Exception>();
            foreach (var entry in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    var current = store.Read(entry.Task.Name);
                    if (current != null) { Same(current, entry.Task); continue; }
                    store.Restore(entry.Task, entry.Sid);
                    if (!store.Equivalent(store.Read(entry.Task.Name), entry.Task, entry.Sid)) throw new IOException("Восстановление задания не подтверждено.");
                }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count == 0) throw;
            errors.Insert(0, primary); throw new AggregateException("Очистка автозапуска не завершена; параллельные изменения не перезаписаны.", errors);
        }
    }
    internal static void Same(StartupTaskSnapshot current, StartupTaskSnapshot expected)
    {
        if (current == null || expected == null || current.Fingerprint != expected.Fingerprint) throw new IOException("Задание изменилось после проверки установки.");
    }
    internal static void RemoveInstalled(string install)
    {
        using (var store = new WindowsInstallerStartupTaskStore(Path.Combine(install, "NOVA VPN.exe"))) Remove(store, Path.Combine(install, "NOVA VPN.exe"));
    }
}

internal sealed class WindowsInstallerStartupTaskStore : IInstallerStartupTaskStore, IDisposable
{
    object scheduler, folder; readonly string executable;
    public WindowsInstallerStartupTaskStore(string executable)
    {
        this.executable = WindowsStartupTaskPolicy.ExecutablePath(executable);
        try { scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); ((dynamic)scheduler).Connect(); folder = ((dynamic)scheduler).GetFolder(@"\"); }
        catch { Dispose(); throw; }
    }
    static void Admin()
    {
        using (var identity = WindowsIdentity.GetCurrent()) if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Для очистки заданий установки нужны права администратора.");
    }
    static void Name(string name) { if (!WindowsStartupTaskPolicy.IsKnownName(name)) throw new UnauthorizedAccessException("Неизвестное имя задания."); }
    public StartupTaskSnapshot[] List()
    {
        var result = new List<StartupTaskSnapshot>(); object tasks = null;
        try
        {
            tasks = ((dynamic)folder).GetTasks(1); int count = (int)((dynamic)tasks).Count;
            if (count < 0 || count > 16384) throw new InvalidDataException("Слишком много заданий планировщика.");
            for (int i = 1; i <= count; i++)
            {
                object task = null;
                try { task = ((dynamic)tasks).Item(i); string name = (string)((dynamic)task).Name;
                    if (WindowsStartupTaskPolicy.IsKnownName(name)) { var snapshot = Snapshot(task, name); result.Add(snapshot); if (result.Count > 1024) throw new InvalidDataException("Слишком много заданий NOVA."); }
                } finally { Release(task); }
            }
            return result.ToArray();
        }
        finally { Release(tasks); }
    }
    static StartupTaskSnapshot Snapshot(object task, string name)
    {
        if (!String.Equals((string)((dynamic)task).Path, @"\" + name, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Неожиданный путь задания.");
        return new StartupTaskSnapshot(name, (string)((dynamic)task).Xml, (string)((dynamic)task).GetSecurityDescriptor(5));
    }
    public StartupTaskSnapshot Read(string name)
    {
        Name(name); object task = null;
        try
        {
            try { task = ((dynamic)folder).GetTask(name); }
            catch (COMException error) { if (error.ErrorCode == unchecked((int)0x80070002)) return null; throw; }
            catch (FileNotFoundException error) { if (error.HResult == unchecked((int)0x80070002)) return null; throw; }
            return Snapshot(task, name);
        }
        finally { Release(task); }
    }
    public string ResolveSid(string principal)
    {
        if (principal.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) return new SecurityIdentifier(principal).Value;
        return ((SecurityIdentifier)new NTAccount(principal).Translate(typeof(SecurityIdentifier))).Value;
    }
    public void Delete(StartupTaskSnapshot expected)
    {
        Admin(); Name(expected.Name);
        if (InstallerStartupTasks.Identify(expected, executable, ResolveSid) == null) throw new UnauthorizedAccessException("Чужая установка.");
        InstallerStartupTasks.Same(Read(expected.Name), expected);
        ((dynamic)folder).DeleteTask(expected.Name, 0);
    }
    public void Restore(StartupTaskSnapshot original, string sid)
    {
        Admin(); Name(original.Name); var entry = InstallerStartupTasks.Identify(original, executable, ResolveSid);
        if (entry == null || entry.Sid != sid || Read(original.Name) != null) throw new UnauthorizedAccessException("Восстановление поверх чужого задания запрещено.");
        object task = null;
        try { task = ((dynamic)folder).RegisterTask(original.Name, original.Xml, 2 | 16 | 32, sid, null, 3, original.Sddl); }
        finally { Release(task); }
    }
    string Normalize(string xml)
    {
        object definition = null;
        try { definition = ((dynamic)scheduler).NewTask(0); ((dynamic)definition).XmlText = xml; return (string)((dynamic)definition).XmlText; }
        finally { Release(definition); }
    }
    public bool Equivalent(StartupTaskSnapshot actual, StartupTaskSnapshot original, string sid)
    {
        if (actual == null) return false;
        var observed = InstallerStartupTasks.Identify(actual, executable, ResolveSid); var expected = InstallerStartupTasks.Identify(original, executable, ResolveSid);
        if (observed == null || expected == null || observed.Sid != sid || expected.Sid != sid) return false;
        return WindowsStartupTaskPolicy.SecurityKey(actual.Sddl) == WindowsStartupTaskPolicy.SecurityKey(original.Sddl) && WindowsStartupTaskPolicy.DefinitionKey(Normalize(actual.Xml), sid, observed.Account) == WindowsStartupTaskPolicy.DefinitionKey(Normalize(original.Xml), sid, expected.Account);
    }
    static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public void Dispose() { var f = folder; var s = scheduler; folder = scheduler = null; Release(f); Release(s); }
}

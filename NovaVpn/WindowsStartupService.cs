using System;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;

namespace NovaVpn;

public static class WindowsStartupService
{
    public const string TaskName = "NOVA VPN Startup";
    public static string ScopedTaskName(string sid) => WindowsStartupTaskPolicy.NameForSid(sid);
    public static string BuildTaskXml(string executable, string userSid, bool connect) => WindowsStartupTaskPolicy.BuildXml(executable, userSid, connect);
    static string JournalDirectory => Path.Combine(StateStore.DirectoryPath, "startup-recovery");
    static string Account() { using (var identity = WindowsIdentity.GetCurrent()) return identity.Name; }

    public static Task ApplyAsync(bool enabled, bool connect, string executable, IWindowsStartupPreferences preferences) => Task.Run(() => {
        string sid = WindowsUserInstance.CurrentSid(); string account = Account();
        using (var installation = WindowsInstallationLease.AcquireClient(Path.GetDirectoryName(WindowsStartupTaskPolicy.ExecutablePath(executable))))
        using (var journal = new WindowsStartupTaskJournal(JournalDirectory))
        using (var store = new WindowsStartupSchedulerStore(executable))
            WindowsStartupTaskRecovery.Apply(store, journal, preferences, sid, account, executable, enabled, connect);
    });
    public static void Recover(string executable, IWindowsStartupPreferences preferences)
    {
        if (!WindowsStartupTaskJournal.HasPending(JournalDirectory)) return;
        string sid = WindowsUserInstance.CurrentSid(); string account = Account();
        using (var installation = WindowsInstallationLease.AcquireClient(Path.GetDirectoryName(WindowsStartupTaskPolicy.ExecutablePath(executable))))
        using (var journal = new WindowsStartupTaskJournal(JournalDirectory))
        using (var store = new WindowsStartupSchedulerStore(executable))
            WindowsStartupTaskRecovery.Recover(store, journal, preferences, sid, account, executable);
    }
}
public sealed class WindowsStartupPreferenceAdapter : IWindowsStartupPreferences
{
    readonly Func<WindowsStartupPreferences> read; readonly Action<WindowsStartupPreferences> write;
    public WindowsStartupPreferenceAdapter(Func<WindowsStartupPreferences> read, Action<WindowsStartupPreferences> write)
    { this.read = read ?? throw new ArgumentNullException(nameof(read)); this.write = write ?? throw new ArgumentNullException(nameof(write)); }
    public WindowsStartupPreferences Read() => read();
    public void Write(WindowsStartupPreferences value) { value.Validate(); write(new WindowsStartupPreferences(value.Open, value.Connect)); }
}

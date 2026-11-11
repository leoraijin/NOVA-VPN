using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace NovaVpn
{
    public sealed class WindowsStartupPreferences
    {
        public bool Open, Connect;
        public WindowsStartupPreferences() { }
        public WindowsStartupPreferences(bool open, bool connect) { Open = open; Connect = connect; }
        public bool Same(WindowsStartupPreferences other) { return other != null && Open == other.Open && Connect == other.Connect; }
        public void Validate() { if (Open && Connect) throw new InvalidDataException("Неоднозначный режим автозапуска."); }
    }
    public interface IWindowsStartupPreferences
    {
        WindowsStartupPreferences Read();
        void Write(WindowsStartupPreferences value); // Must durably save flags without replacing unrelated state.
    }
    public sealed class StartupTaskSavedSnapshot
    {
        public string Name, Xml, Sddl;
        public static StartupTaskSavedSnapshot From(StartupTaskSnapshot task) { return task == null ? null : new StartupTaskSavedSnapshot { Name = task.Name, Xml = task.Xml, Sddl = task.Sddl }; }
        public StartupTaskSnapshot Task() { return new StartupTaskSnapshot(Name, Xml, Sddl); }
    }
    public enum StartupTaskRecoveryPhase { Prepared = 1, ScopedPending = 2, LegacyPending = 3, PreferencesPending = 4, Committed = 5 }
    public sealed class StartupTaskRecoveryRecord
    {
        public int Schema = 1;
        public string Id, Sid, Executable;
        public StartupTaskRecoveryPhase Phase;
        public StartupTaskSavedSnapshot Before, Legacy, Desired;
        public WindowsStartupPreferences PreferencesBefore, PreferencesDesired;
    }
    public interface IWindowsStartupTaskJournal
    {
        StartupTaskRecoveryRecord Read(); // Null only for a confirmed missing journal.
        void Write(StartupTaskRecoveryRecord record);
        void Clear();
    }
    // C#5-compatible orchestration. The journal is intention, not authority:
    // every recovered task is bound to the authenticated SID and exact executable.
    public static class WindowsStartupTaskRecovery
    {
        public static void Apply(IWindowsStartupTaskStore store, IWindowsStartupTaskJournal journal, IWindowsStartupPreferences preferences,
            string sid, string account, string executable, bool enabled, bool connect)
        {
            Arguments(store, journal, preferences, sid, executable);
            using (var lease = Lease(store, sid))
            {
                RecoverLocked(store, journal, preferences, sid, account, executable);
                string name = WindowsStartupTaskPolicy.NameForSid(sid);
                var before = store.Read(name); var legacy = store.Read(WindowsStartupTaskPolicy.LegacyName);
                if (before != null && !WindowsStartupTaskPolicy.IsOwned(before, sid, account, executable, true)) throw new UnauthorizedAccessException("Чужое задание автозапуска.");
                if (legacy != null)
                {
                    if (!WindowsStartupTaskPolicy.HasCurrentPrincipal(legacy.Xml, sid, account)) legacy = null;
                    else if (!WindowsStartupTaskPolicy.IsOwned(legacy, sid, account, executable, false)) throw new UnauthorizedAccessException("Прежнее задание не прошло проверку установки.");
                }
                var desired = enabled ? new StartupTaskSnapshot(name, store.NormalizeXml(WindowsStartupTaskPolicy.BuildXml(executable, sid, connect)), WindowsStartupTaskPolicy.PrivateSddl(sid)) : null;
                var previous = preferences.Read();
                if (previous == null) throw new InvalidDataException("Отсутствуют исходные настройки автозапуска.");
                var record = new StartupTaskRecoveryRecord { Id = Guid.NewGuid().ToString("N"), Sid = sid, Executable = WindowsStartupTaskPolicy.ExecutablePath(executable),
                    Phase = StartupTaskRecoveryPhase.Prepared, Before = StartupTaskSavedSnapshot.From(before), Legacy = StartupTaskSavedSnapshot.From(legacy), Desired = StartupTaskSavedSnapshot.From(desired),
                    PreferencesBefore = new WindowsStartupPreferences(previous.Open, previous.Connect), PreferencesDesired = new WindowsStartupPreferences(enabled && !connect, enabled && connect) };
                Validate(store, record, sid, account, executable);
                journal.Write(record); // No task/preferences may change before durable preparation.
                try
                {
                    if (desired != null || before != null)
                    {
                        Same(store.Read(name), before); Advance(journal, record, StartupTaskRecoveryPhase.ScopedPending);
                        if (desired == null) store.Delete(name); else store.Register(desired, before != null);
                        if (!Equivalent(store, store.Read(name), desired, sid, account, executable, true)) throw new IOException("Проверка нового задания не прошла.");
                    }
                    if (legacy != null)
                    {
                        Same(store.Read(legacy.Name), legacy); Advance(journal, record, StartupTaskRecoveryPhase.LegacyPending);
                        store.Delete(legacy.Name); if (store.Read(legacy.Name) != null) throw new IOException("Удаление прежнего задания не подтверждено.");
                    }
                    if (!record.PreferencesBefore.Same(preferences.Read())) throw new IOException("Настройки автозапуска изменились одновременно.");
                    Advance(journal, record, StartupTaskRecoveryPhase.PreferencesPending);
                    preferences.Write(record.PreferencesDesired);
                    if (!record.PreferencesDesired.Same(preferences.Read())) throw new IOException("Сохранение режима автозапуска не подтверждено.");
                    if (!Equivalent(store, store.Read(name), desired, sid, account, executable, true) || legacy != null && store.Read(legacy.Name) != null)
                        throw new IOException("Задание изменилось во время сохранения режима автозапуска.");
                    Advance(journal, record, StartupTaskRecoveryPhase.Committed);
                }
                catch (Exception primary)
                {
                    // A publication may succeed even if its acknowledgement fails.
                    try
                    {
                        var observed = journal.Read();
                        if (observed != null && observed.Id == record.Id && observed.Phase == StartupTaskRecoveryPhase.Committed)
                        { Validate(store, observed, sid, account, executable); TryClear(journal); return; }
                        if (observed != null && observed.Id != record.Id) throw new IOException("Журнал изменился одновременно; чужая операция не восстанавливается.");
                        RecoverLocked(store, journal, preferences, sid, account, executable);
                    }
                    catch (Exception rollback) { throw new AggregateException("Изменение автозапуска прервано; журнал сохранён для восстановления.", primary, rollback); }
                    throw;
                }
                TryClear(journal); // Committed intent is safe to clear on the next launch if IO is busy.
            }
        }
        public static void Recover(IWindowsStartupTaskStore store, IWindowsStartupTaskJournal journal, IWindowsStartupPreferences preferences, string sid, string account, string executable)
        {
            Arguments(store, journal, preferences, sid, executable);
            using (var lease = Lease(store, sid)) RecoverLocked(store, journal, preferences, sid, account, executable);
        }
        static void Arguments(IWindowsStartupTaskStore store, IWindowsStartupTaskJournal journal, IWindowsStartupPreferences preferences, string sid, string executable)
        {
            if (store == null || journal == null || preferences == null) throw new ArgumentNullException();
            if (new SecurityIdentifier(sid).Value != sid) throw new ArgumentException("Неканонический SID.");
            WindowsStartupTaskPolicy.ExecutablePath(executable);
        }
        static IDisposable Lease(IWindowsStartupTaskStore store, string sid)
        { var lease = store.AcquireLease(sid); if (lease == null) throw new IOException("Автозапуск уже изменяется другим экземпляром NOVA."); return lease; }
        static void Advance(IWindowsStartupTaskJournal journal, StartupTaskRecoveryRecord record, StartupTaskRecoveryPhase phase)
        { record.Phase = phase; journal.Write(record); }
        static void TryClear(IWindowsStartupTaskJournal journal) { try { journal.Clear(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public static void Validate(IWindowsStartupTaskStore store, StartupTaskRecoveryRecord record, string sid, string account, string executable)
        {
            Guid id;
            if (record == null || record.Schema != 1 || !Guid.TryParseExact(record.Id, "N", out id) || id == Guid.Empty || record.Sid != sid ||
                !String.Equals(WindowsStartupTaskPolicy.ExecutablePath(record.Executable), WindowsStartupTaskPolicy.ExecutablePath(executable), StringComparison.OrdinalIgnoreCase) ||
                !Enum.IsDefined(typeof(StartupTaskRecoveryPhase), record.Phase) || record.PreferencesBefore == null || record.PreferencesDesired == null) throw new InvalidDataException("Журнал относится к другой установке или повреждён.");
            record.PreferencesBefore.Validate(); record.PreferencesDesired.Validate();
            if (record.Before != null && !WindowsStartupTaskPolicy.IsOwned(record.Before.Task(), sid, account, executable, true) ||
                record.Legacy != null && !WindowsStartupTaskPolicy.IsOwned(record.Legacy.Task(), sid, account, executable, false) ||
                record.Desired != null && !WindowsStartupTaskPolicy.IsOwned(record.Desired.Task(), sid, account, executable, true)) throw new UnauthorizedAccessException("Непроверенные снимки в журнале автозапуска.");
            bool enabled = record.PreferencesDesired.Open || record.PreferencesDesired.Connect;
            if (enabled != (record.Desired != null)) throw new InvalidDataException("Режим и задание в журнале не согласованы.");
            if (enabled)
            {
                var expected = new StartupTaskSnapshot(WindowsStartupTaskPolicy.NameForSid(sid), store.NormalizeXml(WindowsStartupTaskPolicy.BuildXml(executable, sid, record.PreferencesDesired.Connect)), WindowsStartupTaskPolicy.PrivateSddl(sid));
                if (!Equivalent(store, record.Desired.Task(), expected, sid, account, executable, true)) throw new InvalidDataException("Журнал содержит неизвестное требуемое задание.");
            }
        }
        static void RecoverLocked(IWindowsStartupTaskStore store, IWindowsStartupTaskJournal journal, IWindowsStartupPreferences preferences, string sid, string account, string executable)
        {
            var record = journal.Read(); if (record == null) return;
            Validate(store, record, sid, account, executable);
            if (record.Phase == StartupTaskRecoveryPhase.Committed) { journal.Clear(); return; } // Never replay committed work over later user edits.
            var failures = new List<Exception>();
            if (record.Phase >= StartupTaskRecoveryPhase.PreferencesPending)
            {
                try
                {
                    var current = preferences.Read();
                    if (!record.PreferencesBefore.Same(current) && !record.PreferencesDesired.Same(current)) throw new IOException("Режим изменён независимо; восстановление не перезаписывает его.");
                    // Persist even if the in-memory flags already look old: a previous save may have failed ambiguously.
                    preferences.Write(record.PreferencesBefore);
                    if (!record.PreferencesBefore.Same(preferences.Read())) throw new IOException("Возврат режима не подтверждён.");
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (record.Phase >= StartupTaskRecoveryPhase.LegacyPending && record.Legacy != null)
                try { Restore(store, record.Legacy.Task(), null, sid, account, executable, false); } catch (Exception error) { failures.Add(error); }
            if (record.Phase >= StartupTaskRecoveryPhase.ScopedPending && (record.Before != null || record.Desired != null))
                try { Restore(store, record.Before == null ? null : record.Before.Task(), record.Desired == null ? null : record.Desired.Task(), sid, account, executable, true); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Восстановление автозапуска не завершено. Журнал сохранён.", failures);
            journal.Clear();
        }
        static void Restore(IWindowsStartupTaskStore store, StartupTaskSnapshot before, StartupTaskSnapshot desired, string sid, string account, string executable, bool strict)
        {
            string name = strict ? WindowsStartupTaskPolicy.NameForSid(sid) : WindowsStartupTaskPolicy.LegacyName;
            var current = store.Read(name);
            if (Equivalent(store, current, before, sid, account, executable, strict)) return;
            if (current != null && !Equivalent(store, current, desired, sid, account, executable, strict)) throw new IOException("Задание изменено независимо; журнал не разрешает перезапись.");
            Same(store.Read(name), current);
            if (before == null) { if (current != null) store.Delete(name); }
            else store.Register(before, current != null);
            if (!Equivalent(store, store.Read(name), before, sid, account, executable, strict)) throw new IOException("Восстановленное задание не подтверждено.");
        }
        static bool Equivalent(IWindowsStartupTaskStore store, StartupTaskSnapshot actual, StartupTaskSnapshot expected, string sid, string account, string executable, bool strict)
        {
            if (actual == null || expected == null) return actual == null && expected == null;
            if (actual.Fingerprint == expected.Fingerprint) return true;
            return WindowsStartupTaskPolicy.IsOwned(actual, sid, account, executable, strict) && WindowsStartupTaskPolicy.IsOwned(expected, sid, account, executable, strict) &&
                WindowsStartupTaskPolicy.SecurityKey(actual.Sddl) == WindowsStartupTaskPolicy.SecurityKey(expected.Sddl) &&
                WindowsStartupTaskPolicy.DefinitionKey(store.NormalizeXml(actual.Xml), sid, account) == WindowsStartupTaskPolicy.DefinitionKey(store.NormalizeXml(expected.Xml), sid, account);
        }
        static void Same(StartupTaskSnapshot actual, StartupTaskSnapshot expected)
        { if (actual == null ? expected != null : expected == null || actual.Fingerprint != expected.Fingerprint) throw new IOException("Задание изменилось после проверки."); }
    }
}

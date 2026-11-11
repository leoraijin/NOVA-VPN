using System;
using System.Collections.Generic;
using System.IO;

namespace NovaVpn
{
    public interface IWindowsStartupTaskStore
    {
        IDisposable AcquireLease(string sid);
        StartupTaskSnapshot Read(string name); // Null means confirmed missing, never access denied.
        string NormalizeXml(string xml); // Native definition normalization may be read-only.
        void Register(StartupTaskSnapshot task, bool update); // CREATE or UPDATE, never blind CREATE_OR_UPDATE.
        void Delete(string name);
    }
    public static class WindowsStartupTaskTransaction
    {
        public static void Apply(IWindowsStartupTaskStore store, string sid, string account, string executable, bool enabled, bool connect)
        {
            if (store == null) throw new ArgumentNullException("store");
            executable = WindowsStartupTaskPolicy.ExecutablePath(executable);
            string scoped = WindowsStartupTaskPolicy.NameForSid(sid);
            var lease = store.AcquireLease(sid);
            if (lease == null) throw new IOException("Автозапуск уже изменяется другим экземпляром NOVA.");
            using (lease)
            {
                StartupTaskSnapshot before = store.Read(scoped), legacy = store.Read(WindowsStartupTaskPolicy.LegacyName);
                if (before != null && !WindowsStartupTaskPolicy.IsOwned(before, sid, account, executable, true)) throw new UnauthorizedAccessException("Существующее задание не принадлежит этому клиенту и пользователю.");
                StartupTaskSnapshot ownLegacy = null;
                if (legacy != null && WindowsStartupTaskPolicy.HasCurrentPrincipal(legacy.Xml, sid, account))
                {
                    if (!WindowsStartupTaskPolicy.IsOwned(legacy, sid, account, executable, false)) throw new UnauthorizedAccessException("Задание прежней установки требует отдельного сопоставления. Оно не изменялось.");
                    ownLegacy = legacy;
                }
                StartupTaskSnapshot desired = enabled ? new StartupTaskSnapshot(scoped, store.NormalizeXml(WindowsStartupTaskPolicy.BuildXml(executable, sid, connect)), WindowsStartupTaskPolicy.PrivateSddl(sid)) : null;
                if (desired != null && !WindowsStartupTaskPolicy.IsOwned(desired, sid, account, executable, true)) throw new InvalidDataException("Подготовленное задание не прошло проверку.");
                bool scopedAttempted = false, legacyAttempted = false;
                try
                {
                    if (desired != null || before != null)
                    {
                        Same(store.Read(scoped), before); scopedAttempted = true;
                        if (desired == null) store.Delete(scoped); else store.Register(desired, before != null);
                        var after = store.Read(scoped);
                        if (desired == null ? after != null : !Desired(store, after, desired, sid, account, executable)) throw new IOException("Проверка сохранённого задания автозапуска не прошла.");
                    }
                    if (ownLegacy != null)
                    {
                        Same(store.Read(WindowsStartupTaskPolicy.LegacyName), ownLegacy); legacyAttempted = true;
                        store.Delete(WindowsStartupTaskPolicy.LegacyName);
                        if (store.Read(WindowsStartupTaskPolicy.LegacyName) != null) throw new IOException("Прежнее задание автозапуска не удалено.");
                    }
                }
                catch (Exception primary)
                {
                    var failures = new List<Exception>();
                    if (legacyAttempted) try { Restore(store, ownLegacy, null, sid, account, executable); } catch (Exception error) { failures.Add(error); }
                    if (scopedAttempted) try { RestoreScoped(store, scoped, before, desired, sid, account, executable); } catch (Exception error) { failures.Add(error); }
                    if (failures.Count == 0) throw;
                    failures.Insert(0, primary); throw new AggregateException("Автозапуск изменился одновременно или откат не завершён; чужие изменения сохранены.", failures);
                }
            }
        }
        static void Restore(IWindowsStartupTaskStore store, StartupTaskSnapshot before, StartupTaskSnapshot written, string sid, string account, string executable)
        {
            if (before == null) return;
            var current = store.Read(before.Name);
            if (Equal(current, before)) return;
            if (current != null) throw new IOException("Задание изменено другим участником; восстановление поверх него запрещено.");
            store.Register(before, false); Same(store.Read(before.Name), before);
        }
        static void RestoreScoped(IWindowsStartupTaskStore store, string name, StartupTaskSnapshot before, StartupTaskSnapshot desired, string sid, string account, string executable)
        {
            var current = store.Read(name);
            if (Equal(current, before)) return;
            if (current != null && !Desired(store, current, desired, sid, account, executable)) throw new IOException("Задание изменено одновременно; откат не перезаписывает его.");
            if (before == null) { if (current != null) { Same(store.Read(name), current); store.Delete(name); if (store.Read(name) != null) throw new IOException("Откат нового задания не подтверждён."); } }
            else { Same(store.Read(name), current); store.Register(before, current != null); Same(store.Read(name), before); }
        }
        static bool Desired(IWindowsStartupTaskStore store, StartupTaskSnapshot actual, StartupTaskSnapshot desired, string sid, string account, string executable)
        {
            return actual != null && desired != null && WindowsStartupTaskPolicy.IsOwned(actual, sid, account, executable, true) &&
                WindowsStartupTaskPolicy.DefinitionKey(store.NormalizeXml(actual.Xml), sid, account) == WindowsStartupTaskPolicy.DefinitionKey(desired.Xml, sid, account);
        }
        static bool Equal(StartupTaskSnapshot left, StartupTaskSnapshot right) { return left == null ? right == null : right != null && left.Fingerprint == right.Fingerprint; }
        static void Same(StartupTaskSnapshot current, StartupTaskSnapshot expected) { if (!Equal(current, expected)) throw new IOException("Задание автозапуска изменилось после проверки."); }
    }
}

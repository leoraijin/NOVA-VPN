using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace NovaVpn;

public static class RoutingMigrationService
{
    public static string Fingerprint(RoutingSettings settings)
    {
        var snapshot = settings ?? new RoutingSettings();
        using (var stream = new MemoryStream())
        using (var sha = SHA256.Create())
        { new DataContractJsonSerializer(typeof(RoutingSettings)).WriteObject(stream, snapshot); return Convert.ToBase64String(sha.ComputeHash(stream.ToArray())); }
    }
    public static void Apply(AppState state, ForeignRoutingPolicy policy, string expectedRouting, Func<bool> canChange, Action<AppState> persist)
    {
        Check(state, expectedRouting, canChange, persist);
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        var replacement = RoutingOverlayService.Clone(state.Routing); replacement.MigratedPolicy = policy.Copy();
        var backup = RoutingOverlayService.Clone(state.Routing);
        Commit(state, replacement, backup, expectedRouting, canChange, persist);
    }
    public static void UseLocalRules(AppState state, string expectedRouting, Func<bool> canChange, Action<AppState> persist)
    {
        Check(state, expectedRouting, canChange, persist);
        var replacement = SnapshotForRecovery(state.Routing); replacement.MigratedPolicy = null;
        Commit(state, replacement, SnapshotForRecovery(state.Routing), expectedRouting, canChange, persist);
    }
    public static void Undo(AppState state, string expectedRouting, Func<bool> canChange, Action<AppState> persist)
    {
        Check(state, expectedRouting, canChange, persist);
        if (state.RoutingMigrationBackup == null) throw new InvalidOperationException("Нет предыдущей маршрутизации для восстановления.");
        Commit(state, RoutingOverlayService.Clone(state.RoutingMigrationBackup), null, expectedRouting, canChange, persist);
    }
    private static RoutingSettings SnapshotForRecovery(RoutingSettings settings)
    {
        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(RoutingSettings)); serializer.WriteObject(stream, settings ?? new RoutingSettings());
            stream.Position = 0; return (RoutingSettings)serializer.ReadObject(stream);
        }
    }
    private static void Check(AppState state, string expected, Func<bool> canChange, Action<AppState> persist)
    {
        if (state == null || canChange == null || persist == null) throw new ArgumentNullException();
        if (!canChange() || state.Routing?.Gaming != null || state.Zapret?.RoutingChangesApplied == true)
            throw new InvalidOperationException("Отключите соединение и активные изменения Zapret перед переносом маршрутизации.");
        if (string.IsNullOrEmpty(expected) || Fingerprint(state.Routing) != expected) throw new InvalidOperationException("Маршрутизация изменилась после предпросмотра. Повторите перенос.");
    }
    private static void Commit(AppState state, RoutingSettings replacement, RoutingSettings backup, string expected, Func<bool> canChange, Action<AppState> persist)
    {
        Check(state, expected, canChange, persist);
        var previous = state.Routing; var previousBackup = state.RoutingMigrationBackup;
        state.Routing = replacement; state.RoutingMigrationBackup = backup;
        try { persist(state); }
        catch { state.Routing = previous; state.RoutingMigrationBackup = previousBackup; throw; }
    }
}

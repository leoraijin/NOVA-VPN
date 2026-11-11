using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

public enum GuardRecoveryPhase { Enabling = 1, Armed = 2, Disabling = 3, Disabled = 4 }
public enum GuardRecoveryStatus { Unknown, Disabled, Armed, RecoveryRequired }

// Intention only: never persist a reusable interface identity or trust a saved
// "Armed" flag as evidence of current packet protection.
public sealed class GuardRecoveryRecord
{
    public int Schema = 1;
    public long Revision;
    public string OwnerSid;
    public GuardRecoveryPhase Phase;
}

public interface IGuardRecoveryJournal
{
    GuardRecoveryRecord Read(); // null means absent; corruption must throw
    void Write(GuardRecoveryRecord record);
}

// A production adapter must implement complete native readback, not count-only
// snapshots. The guarded native store implements this boundary; activation still
// requires the SCM service, authenticated control plane and VM packet tests.
public interface IGuardRecoveryBackend
{
    bool HasOwnedFilters();
    void Replace(TrafficGuardPlan plan);
    bool VerifyExact(TrafficGuardPlan plan);
    void RemoveOwnedFilters();
}

internal interface IGuardClosedRecoveryBackend
{
    void ReplaceClosed(TrafficGuardPlan plan);
}

public sealed class GuardFileRecoveryJournal : IGuardRecoveryJournal, IDisposable
{
    private readonly string path;
    private readonly bool machine;
    private FileStream lease;
    public GuardFileRecoveryJournal(string directory) : this(directory, false) { }
    internal GuardFileRecoveryJournal(string directory, bool machine)
    {
        this.machine = machine;
        if (machine) GuardMachineJournalSecurity.PrepareDirectory(directory); else PrivateRuntimeFiles.PrepareDirectory(directory);
        path = Path.Combine(Path.GetFullPath(directory), "guard-intent.json");
        CheckFile(path);
        string lockPath = Path.Combine(Path.GetDirectoryName(path), "guard-intent.lock");
        CheckFile(lockPath);
        lease = machine ? GuardMachineJournalSecurity.Open(lockPath, FileMode.OpenOrCreate, FileShare.None, FileOptions.None) : new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static void CheckFile(string file)
    {
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recovery journal cannot be a reparse point.");
    }
    internal void DemandMachineLease(string directory)
    {
        if (!machine || lease == null || !lease.CanRead || !string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Exclusive machine journal lifetime required.");
    }
    internal static void Validate(GuardRecoveryRecord record)
    {
        if (record == null || record.Schema != 1 || record.Revision < 1 || !Enum.IsDefined(typeof(GuardRecoveryPhase), record.Phase))
            throw new InvalidDataException("Invalid traffic guard recovery record.");
        try { if (new SecurityIdentifier(record.OwnerSid).Value != record.OwnerSid) throw new FormatException(); }
        catch (Exception ex) when (ex is ArgumentException || ex is FormatException)
        { throw new InvalidDataException("Invalid recovery owner SID.", ex); }
    }
    public GuardRecoveryRecord Read()
    {
        if (lease == null) throw new ObjectDisposedException(nameof(GuardFileRecoveryJournal));
        CheckFile(path);
        if (!File.Exists(path)) return null;
        if (machine) GuardMachineJournalSecurity.Validate(path, false);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length < 2 || stream.Length > 4096) throw new InvalidDataException("Invalid recovery journal size.");
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false))
            {
                var record = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 8 }.Deserialize<GuardRecoveryRecord>(reader.ReadToEnd());
                Validate(record); return record;
            }
        }
    }
    public void Write(GuardRecoveryRecord record)
    {
        if (lease == null) throw new ObjectDisposedException(nameof(GuardFileRecoveryJournal));
        Validate(record); CheckFile(path);
        if (machine && File.Exists(path)) GuardMachineJournalSecurity.Validate(path, false);
        string pending = Path.Combine(Path.GetDirectoryName(path), "guard-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(record));
            using (var stream = machine ? GuardMachineJournalSecurity.Open(pending, FileMode.CreateNew, FileShare.None, FileOptions.WriteThrough) : new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            // Same-volume atomic replacement. Never fall back to deleting the old
            // record first; if replacement fails the old intent remains valid.
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public void Dispose() { var old = lease; lease = null; old?.Dispose(); }
}

// Service-internal orchestration. Caller authentication and machine-owner policy
// belong to the future SCM/IPC boundary, never to caller-supplied record data.
public sealed class GuardRecoveryController
{
    private readonly Dictionary<string, double> withdrawalTimings = new Dictionary<string, double>();
    internal Dictionary<string, double> WithdrawalTimings { get { lock (sync) return new Dictionary<string, double>(withdrawalTimings); } }
    private void MeasureWithdrawal(string phase, Action action)
    {
        var elapsed = Stopwatch.StartNew();
        try { action(); }
        finally { withdrawalTimings[phase] = elapsed.Elapsed.TotalMilliseconds; }
    }
    private readonly object sync = new object();
    private readonly IGuardRecoveryJournal journal;
    private readonly IGuardRecoveryBackend backend;
    private GuardRecoveryRecord current;
    private GuardRecoveryStatus status;
    public GuardRecoveryStatus Status { get { lock (sync) return status; } }
    public long Revision { get { lock (sync) return current?.Revision ?? 0; } }
    public string OwnerSid { get { lock (sync) return current?.OwnerSid; } }
    private TrafficGuardPlan verifiedPlan;
    private TrafficGuardPlan attemptedPlan;
    public GuardRecoveryStatus RefreshStatus()
    {
        lock (sync)
        {
            try
            {
                if (status == GuardRecoveryStatus.Armed && (verifiedPlan == null || !backend.VerifyExact(verifiedPlan)) ||
                    status == GuardRecoveryStatus.Disabled && backend.HasOwnedFilters()) status = GuardRecoveryStatus.RecoveryRequired;
            }
            catch { status = GuardRecoveryStatus.RecoveryRequired; }
            return status;
        }
    }
    internal bool IsVerifiedForCore(GuardCoreEndpointBinding binding)
    {
        lock (sync)
        {
            if (binding == null || verifiedPlan == null || !ReferenceEquals(verifiedPlan.CoreBinding, binding)) return false;
            return RefreshStatus() == GuardRecoveryStatus.Armed;
        }
    }
    // A session can withdraw only its own verified grants. A concurrent explicit
    // disable wins: automatic exit handling must never re-arm Disabled/Disabling.
    internal bool WithdrawCore(GuardCoreEndpointBinding binding)
    {
        lock (sync)
        {
            if (binding == null || !(ReferenceEquals(verifiedPlan?.CoreBinding, binding) || ReferenceEquals(attemptedPlan?.CoreBinding, binding)) || current == null ||
                current.Phase == GuardRecoveryPhase.Disabled || current.Phase == GuardRecoveryPhase.Disabling) return false;
            var closed = TrafficGuardCompiler.Compile(new TrafficGuardPolicy { OwnerSid = current.OwnerSid });
            withdrawalTimings.Clear();
            status = GuardRecoveryStatus.RecoveryRequired;
            try
            {
                MeasureWithdrawal("intent-enabling", () => Save(current.OwnerSid, GuardRecoveryPhase.Enabling));
                InstallVerified(closed, true);
                MeasureWithdrawal("intent-armed", () => Save(current.OwnerSid, GuardRecoveryPhase.Armed));
                status = GuardRecoveryStatus.Armed;
                return true;
            }
            catch { status = GuardRecoveryStatus.RecoveryRequired; throw; }
        }
    }
    public GuardRecoveryController(IGuardRecoveryJournal journal, IGuardRecoveryBackend backend)
    { this.journal = journal ?? throw new ArgumentNullException(nameof(journal)); this.backend = backend ?? throw new ArgumentNullException(nameof(backend)); }
    // Administrative tool only: inspect intent without restoring or deleting
    // filters. The explicit subsequent Disable still persists intent first.
    internal void PrepareAdministrativeDisable()
    {
        lock (sync)
        {
            status = GuardRecoveryStatus.RecoveryRequired; verifiedPlan = null; attemptedPlan = null; current = null;
            try
            {
                var record = journal.Read(); if (record != null) GuardFileRecoveryJournal.Validate(record);
                // A saturated/corrupted counter must not prevent an explicit
                // administrative unlock. The stopped service's epoch cannot be
                // reused after restart; preserve the owner while repairing count.
                current = record != null && record.Revision > long.MaxValue - 2
                    ? new GuardRecoveryRecord { OwnerSid = record.OwnerSid, Phase = record.Phase, Revision = 0 } : record;
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is FormatException || error is InvalidOperationException) { }
        }
    }
    private void Save(string owner, GuardRecoveryPhase phase)
    {
        var next = new GuardRecoveryRecord { OwnerSid = owner, Phase = phase, Revision = checked((current?.Revision ?? 0) + 1) };
        journal.Write(next); current = next;
    }
    private void InstallVerified(TrafficGuardPlan plan, bool closing = false)
    {
        attemptedPlan = plan; // Retain authority even if native readback fails after replacement.
        if (closing) MeasureWithdrawal("native-replace", () => { if (backend is IGuardClosedRecoveryBackend closed) closed.ReplaceClosed(plan); else backend.Replace(plan); });
        else backend.Replace(plan);
        bool exact = false;
        if (closing) MeasureWithdrawal("native-readback", () => exact = backend.VerifyExact(plan));
        else exact = backend.VerifyExact(plan);
        if (!exact) throw new InvalidDataException("Traffic guard rules did not match full readback.");
        verifiedPlan = plan;
    }
    public void Recover()
    {
        lock (sync)
        {
            status = GuardRecoveryStatus.RecoveryRequired;
            try
            {
                current = journal.Read();
                if (current == null) { if (!backend.HasOwnedFilters()) status = GuardRecoveryStatus.Disabled; return; }
                GuardFileRecoveryJournal.Validate(current);
                if (current.Phase == GuardRecoveryPhase.Disabled)
                { if (!backend.HasOwnedFilters()) status = GuardRecoveryStatus.Disabled; return; }
                if (current.Phase == GuardRecoveryPhase.Disabling) { FinishDisable(current.OwnerSid); return; }
                // Every service start revokes stale TUN and endpoint grants. An
                // authenticated fresh attachment can replace this closed plan.
                InstallVerified(TrafficGuardCompiler.Compile(new TrafficGuardPolicy { OwnerSid = current.OwnerSid }));
                Save(current.OwnerSid, GuardRecoveryPhase.Armed);
                status = GuardRecoveryStatus.Armed;
            }
            catch { status = GuardRecoveryStatus.RecoveryRequired; throw; }
        }
    }
    public void Enable(TrafficGuardPolicy policy)
    {
        lock (sync)
        {
            if (status == GuardRecoveryStatus.Unknown) throw new InvalidOperationException("Recover before accepting commands.");
            var plan = TrafficGuardCompiler.Compile(policy);
            status = GuardRecoveryStatus.RecoveryRequired;
            Save(plan.OwnerSid, GuardRecoveryPhase.Enabling);
            InstallVerified(plan);
            Save(plan.OwnerSid, GuardRecoveryPhase.Armed);
            status = GuardRecoveryStatus.Armed;
        }
    }
    public void Disable(string authorizedOwnerSid)
    {
        lock (sync)
        {
            if (status == GuardRecoveryStatus.Unknown) throw new InvalidOperationException("Recover before accepting commands.");
            GuardFileRecoveryJournal.Validate(new GuardRecoveryRecord { Revision = 1, Phase = GuardRecoveryPhase.Disabling, OwnerSid = authorizedOwnerSid });
            status = GuardRecoveryStatus.RecoveryRequired;
            Save(authorizedOwnerSid, GuardRecoveryPhase.Disabling);
            FinishDisable(authorizedOwnerSid);
        }
    }
    private void FinishDisable(string owner)
    {
        backend.RemoveOwnedFilters();
        if (backend.HasOwnedFilters()) throw new InvalidDataException("Traffic guard removal was not verified.");
        Save(owner, GuardRecoveryPhase.Disabled);
        verifiedPlan = null; attemptedPlan = null;
        status = GuardRecoveryStatus.Disabled;
    }
}

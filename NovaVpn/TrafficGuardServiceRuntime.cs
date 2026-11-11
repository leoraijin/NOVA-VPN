using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

// Owns handles only. Stopping the service must never mean disabling protection.
// Enabling remains gated until bootstrap, emergency recovery and VM packet
// verification are complete. No installer provisions the owner policy yet.
public sealed class TrafficGuardServiceRuntime : IDisposable
{
    private readonly GuardFileRecoveryJournal journal;
    private readonly TrafficGuardNativeStore store;
    private readonly GuardRecoveryController controller;
    private readonly GuardControlPipe pipe;
    private readonly CancellationTokenSource pipeCancellation;
    private readonly Task pipeLoop;
    private int disposed;
    private readonly object coreSync = new object();
    private readonly GuardOwnerPolicy ownerPolicy;
    private GuardOwnedCoreSession ownedCore;
    private GuardServiceProxyConfiguration coreConfiguration;
    private GuardCoreEndpointBinding coreEndpointBinding;
    private Task coreObserver;
    private readonly Task connectionMonitor;
    private readonly Dictionary<string, double> cleanupTimings = new Dictionary<string, double>();
    internal Dictionary<string, double> CleanupTimings { get { lock (cleanupTimings) return new Dictionary<string, double>(cleanupTimings); } }
    private void MeasureCleanup(string phase, Action action)
    {
        var elapsed = Stopwatch.StartNew();
        try { action(); }
        finally { lock (cleanupTimings) cleanupTimings[phase] = elapsed.Elapsed.TotalMilliseconds; }
    }
    private readonly TaskCompletionSource<Exception> coreFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    public GuardRecoveryStatus Status => controller.RefreshStatus();
    private TrafficGuardServiceRuntime(GuardFileRecoveryJournal journal, TrafficGuardNativeStore store, GuardRecoveryController controller, GuardOwnerPolicy owner, CancellationToken token)
    {
        this.journal = journal; this.store = store; this.controller = controller; ownerPolicy = owner;
        if (owner != null)
        {
            // A request cannot override this gate. Incomplete independent guard
            // must not be armed through an otherwise functional IPC transport.
            var dispatcher = new GuardCommandDispatcher(owner, controller, core => Path.Combine(TrafficGuardServiceIdentity.InstallRoot, "core", core == GuardCoreKind.SingBox ? "sing-box.exe" : "reality-client.exe"), false);
            pipe = GuardControlPipe.OpenForService(owner.OwnerSid, dispatcher.Execute);
            pipeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            pipeLoop = pipe.RunAsync(pipeCancellation.Token);
            connectionMonitor = MonitorConnectionsAsync(pipeCancellation.Token);
        }
    }

    private async Task MonitorConnectionsAsync(CancellationToken token)
    {
        try { await GuardConnectionWatchdog.RunAsync(ValidateOwnedConnection, TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
        catch (Exception error) { coreFailure.TrySetResult(error); }
    }
    private void ValidateOwnedConnection()
    {
        lock (coreSync)
        {
            if (Volatile.Read(ref disposed) != 0 || ownedCore == null) return;
            try
            {
                // A completed process has its own cleanup observer. Do not turn
                // an ordinary exit racing this tick into an authority failure.
                if (ownedCore.Completion.IsCompleted) return;
                coreConfiguration.DemandCurrent(ownerPolicy);
                // Includes the retained process and, once attached, TUN identity.
                coreEndpointBinding?.Read();
            }
            catch (Exception validation)
            {
                try { StopOwnedCoreLocked(); }
                catch (Exception cleanup) { throw new AggregateException("Connection authority and cleanup failed.", validation, cleanup); }
                throw new IOException("Owned connection authority changed; the session was closed.", validation);
            }
        }
    }

    // Early executable gate only; SCM registration/identity is checked again by
    // Open and every native mutation. This method alone grants no authority.
    public static void ValidateEntry()
    {
        TrafficGuardServiceIdentity.DemandProcess();
        if (Environment.UserInteractive) throw new UnauthorizedAccessException("Служба запускается только через SCM.");
    }

    public static TrafficGuardServiceRuntime Open(CancellationToken token)
    {
        // Reject desktop/console callers before touching machine state or BFE.
        TrafficGuardServiceIdentity.DemandProcess();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { TrafficGuardServiceIdentity.Demand(); using (var process = Process.GetCurrentProcess()) TrafficGuardServiceIdentity.ValidateServerProcess((uint)process.Id); break; }
            catch (UnauthorizedAccessException)
            {
                // SCM does not guarantee a usable process ID in START_PENDING.
                // OnStart returns promptly; this worker waits for RUNNING.
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10)) throw;
                if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
            }
        }
        GuardFileRecoveryJournal journal = null; TrafficGuardNativeStore store = null;
        try
        {
            token.ThrowIfCancellationRequested();
            string directory = TrafficGuardServiceIdentity.PrepareMachineJournalDirectory();
            journal = new GuardFileRecoveryJournal(directory, true);
            store = TrafficGuardNativeStore.OpenForService();
            token.ThrowIfCancellationRequested();
            var controller = new GuardRecoveryController(journal, store);
            try { controller.Recover(); }
            catch (Exception error) when (error is IOException || error is InvalidOperationException || error is ArgumentException || error is FormatException)
            { /* Remain RecoveryRequired. Only an authenticated explicit command may repair intent. */ }
            token.ThrowIfCancellationRequested();
            var owner = TrafficGuardServiceIdentity.ReadOwnerPolicy(directory);
            if (owner != null) store.BindCloseAuthority(owner);
            if (owner != null) GuardServiceProxyConfiguration.CleanupOrphans(journal);
            return new TrafficGuardServiceRuntime(journal, store, controller, owner, token);
        }
        catch { store?.Dispose(); journal?.Dispose(); throw; }
    }
    public void Wait(CancellationToken token)
    {
        if (pipeLoop == null) { token.WaitHandle.WaitOne(); return; }
        var completed = Task.WhenAny(pipeLoop, coreFailure.Task).GetAwaiter().GetResult();
        if (completed == coreFailure.Task) throw new InvalidOperationException("Owned core cleanup failed.", coreFailure.Task.GetAwaiter().GetResult());
        pipeLoop.GetAwaiter().GetResult();
        if (!token.IsCancellationRequested) throw new IOException("Traffic guard control listener stopped unexpectedly.");
    }
    // Only service-authored private configurations can reach this internal
    // preparation path. The core stays suspended so its actual SYSTEM identity
    // can be bound to endpoint policy before it is allowed to run.
    internal GuardProcessIdentity PrepareOwnedCore(GuardServiceConnection connection)
        => PrepareOwnedCoreSession(connection,false);
    internal GuardProcessIdentity PrepareOwnedTunnel(GuardServiceConnection connection)
        => PrepareOwnedCoreSession(connection,true);
    private GuardProcessIdentity PrepareOwnedCoreSession(GuardServiceConnection connection,bool tunnel)
    {
        TrafficGuardServiceIdentity.Demand();
        lock (coreSync)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TrafficGuardServiceRuntime));
            if (ownerPolicy == null || controller.RefreshStatus() != GuardRecoveryStatus.Armed || controller.OwnerSid != ownerPolicy.OwnerSid) throw new InvalidOperationException("Arm the independent guard before preparing a core.");
            if (ownedCore != null) throw new InvalidOperationException("A service-owned core session already exists.");
            var configuration = tunnel ? GuardServiceProxyConfiguration.CreateTunnel(ownerPolicy,connection) : GuardServiceProxyConfiguration.Create(ownerPolicy, connection);
            GuardOwnedCoreSession candidate = null;
            try
            {
                candidate = GuardOwnedCoreSession.PrepareGenerated(configuration, ownerPolicy);
                var identity = candidate.Inspect(); ownedCore = candidate; coreConfiguration = configuration;
                coreObserver = candidate.Completion.ContinueWith(completed => OwnedCoreExited(candidate, completed), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return identity;
            }
            catch
            {
                ownedCore = null; coreConfiguration = null;
                try { candidate?.Dispose(); } finally { configuration.Dispose(); }
                throw;
            }
        }
    }
    // Service-generated connection data only. Public IPC still has no core
    // launch/config import command; its installed user remains the owner.
    internal void BindOwnedCoreEndpoints()
    {
        TrafficGuardServiceIdentity.Demand();
        lock (coreSync)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TrafficGuardServiceRuntime));
            if (ownerPolicy == null || ownedCore == null || controller.RefreshStatus() != GuardRecoveryStatus.Armed || controller.OwnerSid != ownerPolicy.OwnerSid) throw new InvalidOperationException("Owned core and original armed owner required.");
            if (ownedCore.Inspect().Resumed) throw new InvalidOperationException("Bind core endpoints before resuming the core.");
            if (coreConfiguration == null) throw new InvalidOperationException("Generated connection required.");
            coreConfiguration.DemandCurrent(ownerPolicy);
            var requested = coreConfiguration.Endpoints();
            var binding = GuardCoreEndpointBinding.FromOwnedCore(ownedCore);
            var identity = binding.Read();
            var policy = new TrafficGuardPolicy { OwnerSid = ownerPolicy.OwnerSid, CoreBinding = binding };
            foreach (var endpoint in requested)
            {
                if (endpoint == null || endpoint.Core != ownedCore.Kind) throw new FormatException("Endpoint core kind does not match the owned core.");
                policy.Endpoints.Add(new TrafficGuardEndpoint { ApplicationPath = identity.Image, Address = endpoint.Address, Port = endpoint.Port, Network = endpoint.Network });
            }
            TrafficGuardCompiler.Compile(policy); // Complete validation before journal mutation.
            coreEndpointBinding = binding; // Retain cleanup authority before any native attempt.
            try
            {
                controller.Enable(policy); // Transaction, exact native/live readback, then Armed intent.
                if (!controller.IsVerifiedForCore(binding)) throw new InvalidOperationException("Owned core endpoint policy was not verified.");
            }
            catch (Exception admission)
            {
                try { StopOwnedCoreLocked(); }
                catch (Exception cleanup) { throw new AggregateException("Core admission and cleanup failed.", admission, cleanup); }
                throw;
            }
        }
    }
    internal void BindOwnedTunnel()
    {
        TrafficGuardServiceIdentity.Demand();
        lock(coreSync)
        {
            if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(nameof(TrafficGuardServiceRuntime));
            if(ownerPolicy==null||ownedCore==null||coreConfiguration?.Tunnel==null||coreEndpointBinding==null||controller.RefreshStatus()!=GuardRecoveryStatus.Armed||controller.OwnerSid!=ownerPolicy.OwnerSid)throw new InvalidOperationException("Running generated tunnel and original armed owner required.");
            try
            {
                coreEndpointBinding.AttachTunnel(ownedCore,coreConfiguration,ownerPolicy);
                var identity=coreEndpointBinding.Read();var policy=new TrafficGuardPolicy{OwnerSid=ownerPolicy.OwnerSid,CoreBinding=coreEndpointBinding};
                foreach(var endpoint in coreConfiguration.Endpoints())
                {
                    if(endpoint.Core!=ownedCore.Kind)throw new FormatException("Tunnel endpoint core mismatch.");
                    policy.Endpoints.Add(new TrafficGuardEndpoint{ApplicationPath=identity.Image,Address=endpoint.Address,Port=endpoint.Port,Network=endpoint.Network});
                }
                TrafficGuardCompiler.Compile(policy);controller.Enable(policy);
                if(!controller.IsVerifiedForCore(coreEndpointBinding))throw new InvalidOperationException("Owned tunnel native policy was not verified.");
            }
            catch(Exception admission)
            {
                try{StopOwnedCoreLocked();}catch(Exception cleanup){throw new AggregateException("Tunnel admission and cleanup failed.",admission,cleanup);}throw;
            }
        }
    }
    internal void ResumeOwnedCore()
    {
        TrafficGuardServiceIdentity.Demand();
        lock (coreSync)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TrafficGuardServiceRuntime));
            if (ownerPolicy == null || ownedCore == null || controller.RefreshStatus() != GuardRecoveryStatus.Armed || controller.OwnerSid != ownerPolicy.OwnerSid) throw new InvalidOperationException("Owned core and armed guard required.");
            if (!controller.IsVerifiedForCore(coreEndpointBinding)) throw new InvalidOperationException("Verify the live owned core endpoint policy before resume.");
            ownedCore.Resume();
        }
    }
    internal void StopOwnedCore()
    {
        TrafficGuardServiceIdentity.Demand();
        lock (coreSync)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TrafficGuardServiceRuntime));
            StopOwnedCoreLocked();
        }
    }
    private void StopOwnedCoreLocked()
    {
        var core = ownedCore;
        if (core == null) return;
        try
        {
            if (coreEndpointBinding != null)
            {
                if (ownerPolicy == null || controller.OwnerSid != ownerPolicy.OwnerSid) throw new UnauthorizedAccessException("Owned core owner changed.");
                MeasureCleanup("withdraw-policy", () => controller.WithdrawCore(coreEndpointBinding));
            }
        }
        finally
        {
            // Withdrawal failure must still terminate this owned core. Never
            // disable persistent deny protection as part of session cleanup.
            try { MeasureCleanup("revoke-grants", () => store.RevokeCoreGrants(coreEndpointBinding)); }
            finally
            {
                try { MeasureCleanup("core-process", core.Dispose); }
                finally
                {
                    try { MeasureCleanup("private-configuration", () => coreConfiguration?.Dispose()); }
                    finally { ownedCore = null; coreEndpointBinding = null; coreConfiguration = null; }
                }
            }
        }
    }
    private void OwnedCoreExited(GuardOwnedCoreSession expected, Task<uint> completion)
    {
        // Observe monitor failures as well as process exit. Cleanup still runs;
        // the service worker must not silently swallow a lost process monitor.
        Exception failure = completion.IsFaulted ? completion.Exception : completion.IsCanceled ? new TaskCanceledException(completion) : null;
        try
        {
            lock (coreSync)
            {
                if (Volatile.Read(ref disposed) == 0 && ReferenceEquals(ownedCore, expected)) StopOwnedCoreLocked();
            }
        }
        catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
        if (failure != null) coreFailure.TrySetResult(failure);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { MeasureCleanup("control-pipe", () => { pipeCancellation?.Cancel(); pipeLoop?.GetAwaiter().GetResult(); }); }
        finally
        {
            try
            {
                try { MeasureCleanup("core-stop-total", () => { lock (coreSync) StopOwnedCoreLocked(); }); }
                finally
                {
                    MeasureCleanup("process-observer", () => coreObserver?.GetAwaiter().GetResult());
                    MeasureCleanup("connection-monitor", () => connectionMonitor?.GetAwaiter().GetResult());
                    if (coreFailure.Task.IsCompleted) throw new InvalidOperationException("Owned core cleanup failed.", coreFailure.Task.GetAwaiter().GetResult());
                }
            }
            finally
            {
                try { MeasureCleanup("control-resources", () => { pipe?.Dispose(); pipeCancellation?.Dispose(); }); }
                finally
                {
                    try { MeasureCleanup("native-store", store.Dispose); }
                    finally { MeasureCleanup("journal", journal.Dispose); }
                }
            }
        }
    }
}

// Sequential polling: at most one validation runs, and cancellation joins any
// in-flight validation before the returned task completes. No async-void work.
internal static class GuardConnectionWatchdog
{
    internal static async Task RunAsync(Action validate, TimeSpan interval, CancellationToken token)
    {
        if (validate == null) throw new ArgumentNullException(nameof(validate));
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        try
        {
            while (true)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                validate();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

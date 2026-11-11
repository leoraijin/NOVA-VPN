using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using NovaVpn;

internal sealed class TrafficGuardWindowsService : ServiceBase
{
    private readonly Func<CancellationToken, IDisposable> initialize;
    private CancellationTokenSource cancellation;
    private Task worker;
    private volatile bool ready;
    internal bool WorkerReady { get { return ready; } }
    internal Exception WorkerError { get; private set; }
    internal TrafficGuardWindowsService() : this(token => TrafficGuardServiceRuntime.Open(token)) { }
    internal TrafficGuardWindowsService(Func<CancellationToken, IDisposable> initialize)
    {
        this.initialize = initialize;
        ServiceName = TrafficGuardCompiler.ServiceName;
        CanStop = true; CanShutdown = true; CanPauseAndContinue = false;
        AutoLog = false; // Event source provisioning belongs to the future installer.
    }
    protected override void OnStart(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("This service accepts no startup commands.");
        if (worker != null) throw new InvalidOperationException("Service worker already started.");
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        worker = Task.Factory.StartNew(() =>
        {
            try
            {
                using (var session = initialize(token))
                {
                    if (session == null) throw new InvalidOperationException("Service initialization returned no session.");
                    token.ThrowIfCancellationRequested();
                    ready = true;
                    var runtime = session as TrafficGuardServiceRuntime;
                    if (runtime != null) runtime.Wait(token); else token.WaitHandle.WaitOne();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                WorkerError = error;
                // Do not join this worker from itself inside OnStop.
                ThreadPool.QueueUserWorkItem(_ => { try { ExitCode = 1; Stop(); } catch { } });
            }
            finally { ready = false; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    protected override void OnStop()
    {
        ready = false;
        cancellation?.Cancel();
        if (worker != null && !worker.IsCompleted)
        {
            // Native close verification and owned job termination each have a
            // bounded wait. Give the worker time to finish its session disposal.
            try { RequestAdditionalTime(30000); } catch (InvalidOperationException) { /* Unit host or shutdown callback without STOP_PENDING. */ }
            if (!worker.Wait(30000)) ExitCode = 1;
        }
        // Worker cleanup may revoke session grants and stop its core. It never
        // writes a Disabled intent or removes persistent deny protection here.
    }
    protected override void OnShutdown() { OnStop(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && cancellation != null)
        {
            cancellation.Cancel();
            var source = cancellation; cancellation = null;
            if (worker == null || worker.IsCompleted) source.Dispose();
            else worker.ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }
}

internal static class TrafficGuardServiceEntry
{
    public static int Main(string[] args)
    {
        // No install, console, arbitrary configuration path or emergency-unlock
        // switch is accepted by the privileged service executable.
        if (args.Length != 0) return 64;
        try { TrafficGuardServiceRuntime.ValidateEntry(); }
        catch (UnauthorizedAccessException) { return 5; }
        ServiceBase.Run(new TrafficGuardWindowsService());
        return 0;
    }
}

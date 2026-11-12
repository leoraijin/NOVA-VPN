using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NovaVpn;

public sealed partial class CoreManager : IDisposable
{
	private Process process;
    private readonly object coreCheckObservationSync = new object();
    private long coreCheckSequence;
    private CoreCheckObservation lastCoreCheck;
    internal CoreCheckObservation LastCoreCheck { get { lock(coreCheckObservationSync) return lastCoreCheck; } }
    private static string CheckConfigHash(string path)
    {
        try { using(var file=File.OpenRead(path)) { if(file.Length>1024*1024)return null; using(var hash=System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); } }
        catch(IOException) { return null; } catch(UnauthorizedAccessException) { return null; }
    }

    public bool EcoModeRequested { get; private set; }
    private readonly Func<ProcessStartInfo,Process> validationProcessStarter;
    public CoreManager() : this(Process.Start) { }
    private CoreManager(Func<ProcessStartInfo,Process> startValidationProcess) { validationProcessStarter=startValidationProcess??throw new ArgumentNullException(nameof(startValidationProcess)); }
    public CoreManager(bool ecoMode) : this(Process.Start) { EcoModeRequested = ecoMode; }
    public void ConfigureEcoModeForNextStart(bool enabled) { EcoModeRequested = enabled; }
    public async Task<CoreResourceSnapshot> ReadResourcesAsync(System.Threading.CancellationToken token)
    {
        var owned = process; if (owned == null) throw new InvalidOperationException("Ядро не запущено.");
        var snapshot = await CoreResourceClient.ReadAsync(owned, token).ConfigureAwait(false);
        if (!ReferenceEquals(process, owned)) throw new InvalidOperationException("Ядро изменилось во время измерения.");
        return snapshot;
    }
    public async Task SetEcoModeAsync(bool enabled, System.Threading.CancellationToken token)
    {
        var owned = process;
        if (owned != null)
        {
            await CoreResourceClient.SetEcoAsync(owned, enabled, token).ConfigureAwait(false);
            if (!ReferenceEquals(process, owned)) throw new InvalidOperationException("Ядро изменилось во время настройки.");
        }
        EcoModeRequested = enabled;
    }

	private readonly StringBuilder log = new StringBuilder();
    private readonly System.Collections.Generic.List<(DateTime Time, string Text)> reportLog =
        new System.Collections.Generic.List<(DateTime Time, string Text)> { (DateTime.UtcNow, "Запуск NOVA VPN") };

    internal void RecordReportLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string safe = LogSanitizer.SanitizeForReport(message);
        lock (reportLog) reportLog.Add((DateTime.UtcNow, safe));
    }

    internal string GetReportLog(DateTime? sinceUtc)
    {
        var output = new StringBuilder();
        lock (reportLog)
        {
            foreach (var entry in reportLog)
                if (!sinceUtc.HasValue || entry.Time >= sinceUtc.Value)
                    output.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'")).Append("  ").AppendLine(entry.Text);
        }
        return output.ToString();
    }

	private string configPath;
    private CompiledRoutingPlan activeRoutingPlan;
    private VpnProfile[] activeProfiles;
    private Process activeRoutingProcess;
    private readonly object routingSnapshotSync=new object();
    private int activeRoutingGeneration;
    private bool TryPublishRoutingPlan(CompiledRoutingPlan captured,int generation,Process expected)
    {
        lock(routingSnapshotSync){
            if(expected==null||stopping||generation!=RuntimeGeneration||Status!=CoreStatus.Connecting||!ReferenceEquals(process,expected))return false;
            try{if(expected.HasExited)return false;}catch(InvalidOperationException){return false;}
            activeRoutingGeneration=generation;System.Threading.Volatile.Write(ref activeRoutingProcess,expected);System.Threading.Volatile.Write(ref activeRoutingPlan,captured);return true;
        }
    }
    private bool TryCompleteStart(VpnProfile profile,string profileKey,RoutingSettings routing,CompiledRoutingPlan plan,int generation,Process expected) =>
        TryCompleteStartWithCatalog(profile,profileKey,routing,plan,generation,expected,null);
    private bool TryCompleteStartWithCatalog(VpnProfile profile,string profileKey,RoutingSettings routing,CompiledRoutingPlan plan,int generation,Process expected,System.Collections.Generic.IEnumerable<VpnProfile> profiles)
    {
        var frozenRouting=RoutingOverlayService.Clone(routing);
        var frozenProfiles=MultiServerConfigBuilder.CaptureProfiles(profiles??new[]{profile});
        lock(routingSnapshotSync){
            if(!TryPublishRoutingPlan(plan,generation,expected))return false;
            ConnectedAtUtc=DateTime.UtcNow;LastConnectedRouting=frozenRouting;activeProfiles=frozenProfiles;connectedProfile=profile;connectedProfileKey=profileKey;Status=CoreStatus.Connected;return true;
        }
    }
    private bool NotifyCompletedStart(int generation,Process expected)
    {
        lock(routingSnapshotSync){if(!IsCompletedStartCurrent(generation,expected))return false;}
        NotifyStatus(CoreStatus.Connected,"Защищено",generation);
        lock(routingSnapshotSync)return IsCompletedStartCurrent(generation,expected);
    }
    private bool StartInitialVerification(int generation,Process expected)
    {
        VerificationOperation operation;bool run=false;
        lock(routingSnapshotSync)lock(verificationSync){
            if(!IsCompletedStartCurrent(generation,expected))return false;
            operation=verificationOperation;
            if(operation!=null&&!OwnsVerificationContext(operation))return false;
            if(operation==null){operation=BeginVerification();run=operation!=null;}
            InitialVerification=operation==null?Task.FromResult(Verification):operation.Completion.Task;
        }
        // Never invoke HTTP handlers or continuations while lifecycle locks are held.
        if(run)_=CompleteVerificationAsync(operation);
        lock(routingSnapshotSync)return IsCompletedStartCurrent(generation,expected);
    }
    private bool IsCompletedStartCurrent(int generation,Process expected) => !stopping&&generation==RuntimeGeneration&&Status==CoreStatus.Connected&&ReferenceEquals(process,expected);
    internal CompiledRoutingPlan ActiveRoutingPlan {
        get{
            int generation=System.Threading.Volatile.Read(ref activeRoutingGeneration);var captured=System.Threading.Volatile.Read(ref activeRoutingPlan);
            if(captured==null||stopping||Status!=CoreStatus.Connected||generation!=RuntimeGeneration)return null;
            var owned=process;try{if(owned==null||!ReferenceEquals(owned,System.Threading.Volatile.Read(ref activeRoutingProcess))||owned.HasExited)return null;}catch(InvalidOperationException){return null;}
            return !stopping&&Status==CoreStatus.Connected&&generation==RuntimeGeneration&&generation==System.Threading.Volatile.Read(ref activeRoutingGeneration)&&ReferenceEquals(process,owned)&&ReferenceEquals(owned,System.Threading.Volatile.Read(ref activeRoutingProcess))&&ReferenceEquals(captured,System.Threading.Volatile.Read(ref activeRoutingPlan))?captured:null;
        }
    }

	private bool stopping;

	private int startGeneration;
	private VpnProfile connectedProfile;
	private bool verificationBusy;
    private readonly object verificationSync = new object();
    private VerificationOperation verificationOperation;
    private VerificationOperation publishedVerificationContext;
    private long verificationNetworkEpoch;
    private Func<HttpMessageHandler> verificationHandlerFactory = null;

    // Dispose is deferred while cancellation callbacks execute, including callbacks
    // that finish the async verification synchronously on the cancelling thread.
    private sealed class VerificationOperation : IDisposable
    {
        readonly System.Threading.CancellationTokenSource source = new System.Threading.CancellationTokenSource();
        readonly object sync = new object();
        int cancelers;
        bool disposeRequested, disposed;
        public System.Threading.CancellationToken Token { get; private set; }
        public readonly TaskCompletionSource<ConnectionVerification> Completion=new TaskCompletionSource<ConnectionVerification>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly int Generation;
        public readonly long NetworkEpoch;
        public readonly VpnProfile Profile;
        public readonly string ProfileId,ProfileKey,ProfileConfigKey,CorePath;
        public readonly ActiveCoreProbe Channel;
        public readonly Process RuntimeProcess;
        public VerificationOperation(int generation,long networkEpoch,VpnProfile profile,string profileKey,ActiveCoreProbe channel,string corePath,Process runtimeProcess)
        {
            Token=source.Token;Generation=generation;NetworkEpoch=networkEpoch;Profile=profile;ProfileId=profile?.Id;ProfileKey=profileKey??"";
            ProfileConfigKey=profile==null?"":SubscriptionManager.ConfigKey(profile);Channel=channel;CorePath=corePath;RuntimeProcess=runtimeProcess;
        }
        public void Cancel()
        {
            lock(sync) { if(disposed)return;cancelers++; }
            try { source.Cancel(); }
            finally { lock(sync) { cancelers--;if(cancelers==0 && disposeRequested && !disposed){disposed=true;source.Dispose();} } }
        }
        public void Dispose()
        {
            lock(sync) {
                if(disposed || disposeRequested)return;
                disposeRequested=true;
                if(cancelers==0){disposed=true;source.Dispose();}
            }
        }
    }
    private VerificationOperation BeginVerification()
    {
        lock(routingSnapshotSync)lock(verificationSync){
            if(verificationBusy||stopping||Status!=CoreStatus.Connected)return null;
            var operation=new VerificationOperation(RuntimeGeneration,verificationNetworkEpoch,connectedProfile,connectedProfileKey,activeProbe,CorePath,process);
            verificationOperation=operation;verificationBusy=true;return operation;
        }
    }
    // Called only while both lifecycle and verification locks are held.
    private bool OwnsVerificationContext(VerificationOperation operation) => ReferenceEquals(verificationOperation,operation)&&MatchesVerificationContext(operation);
    private bool MatchesVerificationContext(VerificationOperation operation) => !stopping&&Status==CoreStatus.Connected&&
        operation.Generation==RuntimeGeneration&&operation.NetworkEpoch==verificationNetworkEpoch&&ReferenceEquals(process,operation.RuntimeProcess)&&ReferenceEquals(activeProbe,operation.Channel)&&
        ReferenceEquals(connectedProfile,operation.Profile)&&connectedProfile?.Id==operation.ProfileId&&(connectedProfileKey??"")==operation.ProfileKey&&
        (connectedProfile==null?"":SubscriptionManager.ConfigKey(connectedProfile))==operation.ProfileConfigKey&&CorePath==operation.CorePath;
    private bool IsCurrentVerification(VerificationOperation operation)
    {lock(routingSnapshotSync)lock(verificationSync)return OwnsVerificationContext(operation);}
    private bool TryPublishVerification(VerificationOperation operation,int generation,ConnectionVerification result)
    {
        lock(routingSnapshotSync)lock(verificationSync){
            if(generation!=operation.Generation||result.ProfileId!=operation.ProfileId||!OwnsVerificationContext(operation)||operation.Token.IsCancellationRequested)return false;
            result.CheckedUtc=DateTime.UtcNow;result.CheckedTimestamp=Stopwatch.IsHighResolution?Stopwatch.GetTimestamp():-1;result.Completed=true;
            Verification=result;publishedVerificationContext=operation;return true;
        }
    }
    private void FinishVerification(VerificationOperation operation)
    {
        lock(verificationSync) { if(ReferenceEquals(verificationOperation,operation)){verificationOperation=null;verificationBusy=false;} }
        operation.Dispose();
    }
    // Lock order is routingSnapshotSync -> verificationSync. Detach while the
    // lifecycle owner is still held; execute cancellation callbacks after release.
    private VerificationOperation DetachVerification()
    {
        lock(verificationSync) {
            var operation=verificationOperation;
            Verification=new ConnectionVerification();
            // Preserve legacy fixture suppression when no operation owns busy.
            if(operation!=null){verificationOperation=null;verificationBusy=false;}
            return operation;
        }
    }
    public void InvalidateNetworkVerification()
    {
        VerificationOperation previous;
        lock(routingSnapshotSync)lock(verificationSync)
        {
            verificationNetworkEpoch++;
            previous = DetachVerification();
            InitialVerification = null;
        }
        CancelVerification(previous);
    }
    private void CancelVerification(VerificationOperation operation)
    {
        if(operation==null)return;
        try { operation.Cancel(); } catch(AggregateException) { /* All callbacks were attempted; continue stopping the owned core. */ }
    }
    private int tunnelLeaseDenied;
    private int hostNetworkRefused;
    private GameConnectionMonitor gameMonitor;
    private ActiveCoreProbe activeProbe;
    private TunnelRoutingReadiness activeReadiness;
    private string connectedProfileKey;
    public Task<System.Collections.Generic.List<GameEndpoint>> GameEndpointsAsync(InstalledGame game, System.Threading.CancellationToken token)
    {
        if (Status != CoreStatus.Connected || gameMonitor == null) throw new InvalidOperationException("Для определения соединений подключите обычный профиль sing-box в NOVA.");
        return gameMonitor.ReadAsync(game, token);
    }

	public DateTime ConnectedAtUtc { get; private set; }

	public string LastError { get; private set; } = "";

	private ConnectionVerification verification = new ConnectionVerification();
    public ConnectionVerification Verification
    {
        get
        {
            lock(routingSnapshotSync)lock(verificationSync)
            {
                if(publishedVerificationContext != null && !MatchesVerificationContext(publishedVerificationContext))
                {
                    verification = new ConnectionVerification();
                    publishedVerificationContext = null;
                }
                return verification;
            }
        }
        private set
        {
            verification = value;
            publishedVerificationContext = null;
        }
    }

	public RoutingSettings LastConnectedRouting { get; private set; }
    private Task<ConnectionVerification> initialVerification;
    public Task<ConnectionVerification> InitialVerification {get=>System.Threading.Volatile.Read(ref initialVerification);private set=>System.Threading.Volatile.Write(ref initialVerification,value);}
	public string ActiveProfileId => connectedProfile?.Id;
    public VpnProfile[] CaptureActiveProfiles()
    {
        lock(routingSnapshotSync) {
            if(Status!=CoreStatus.Connected)return Array.Empty<VpnProfile>();
            return MultiServerConfigBuilder.CaptureProfiles(activeProfiles??(connectedProfile==null?Array.Empty<VpnProfile>():new[]{connectedProfile}));
        }
    }
    internal bool HasMultiServerCatalogChanges(IEnumerable<VpnProfile> current)
    {
        MultiServerSettings appliedPolicy;
        VpnProfile[] appliedCatalog;
        lock (routingSnapshotSync)
        {
            if (Status != CoreStatus.Connected || LastConnectedRouting?.MultiServer == null || activeProfiles == null) return false;
            appliedPolicy = LastConnectedRouting.MultiServer;
            appliedCatalog = activeProfiles;
        }
        return MultiServerConfigBuilder.CatalogChanged(appliedPolicy, appliedCatalog, current);
    }

    internal bool HasOwnedRuntime { get { lock(routingSnapshotSync) return process!=null; } }
    internal int RuntimeGeneration => System.Threading.Volatile.Read(ref startGeneration);

	public CoreStatus Status { get; private set; }

	public VpnEngine Engine { get; private set; }
    public string ProbeCorePath => VpnEngines.ExecutablePath(VpnEngine.SingBox);
    public string CorePath => VpnEngines.ExecutablePath(Engine);
    public bool CanSelectEngine {get{lock(routingSnapshotSync)return process==null&&Status!=CoreStatus.Connected&&Status!=CoreStatus.Connecting;}}
    public void SelectEngine(VpnEngine engine)
    {
        lock(routingSnapshotSync){
            if(!CanSelectEngine)throw new InvalidOperationException("Сначала отключите VPN, затем выберите ядро.");
            if(!Enum.IsDefined(typeof(VpnEngine),engine))throw new ArgumentOutOfRangeException(nameof(engine));
            Engine=engine;
        }
    }
    public static string BuildPreparedConfiguration(VpnProfile profile, RoutingSettings routing, VpnEngine engine,
        System.Collections.Generic.IEnumerable<VpnProfile> profiles = null, string cachePath = null)
    {
        if (routing?.MultiServer != null) {
            if (engine != VpnEngine.SingBox) throw new NotSupportedException("Назначения серверов требуют sing-box.");
            return MultiServerConfigBuilder.Build(routing, profiles ?? throw new InvalidOperationException("Нет снимка серверов."),
                cachePath ?? throw new InvalidOperationException("Нет приватного пути DNS-кэша."));
        }
        return engine == VpnEngine.Reality ? RealityConfigBuilder.Build(profile,routing) : ConfigBuilder.Build(profile,routing);
    }
    // Multi-server documents always produce our managed TUN, even if their
    // retained selection was formerly a raw JSON profile.
    internal static bool UsesManagedConfiguration(VpnProfile profile, RoutingSettings routing) =>
        routing?.MultiServer != null || profile?.Protocol != "sing-box";

    private string BuildConfig(VpnProfile profile, RoutingSettings routing, System.Collections.Generic.IEnumerable<VpnProfile> profiles = null) =>
        BuildPreparedConfiguration(profile,routing,Engine,profiles,Path.Combine(StateStore.DirectoryPath,"multi-server-dns-v2.db"));

	public bool IsCoreInstalled => File.Exists(CorePath);

	public string Logs
	{
		get
		{
			lock (log)
			{
				return LogSanitizer.Sanitize(log.ToString());
			}
		}
	}

	public event Action<CoreStatus, string> StatusChanged;
    internal event Action<CoreStatus,string,int> RuntimeStatusChanged;

	public event Action<string> LogReceived;

	public event Action UnexpectedExit;

	public event Action<ConnectionVerification> VerificationChanged;

	public async Task StartAsync(VpnProfile profile, RoutingSettings routing, System.Collections.Generic.IEnumerable<VpnProfile> profiles = null)
	{
        // Gaming probes own separate SOCKS-only cores and private configs. They must not
        // prevent the normal VPN from reconnecting after an unrelated network outage.
		if (Status != CoreStatus.Connected && Status != CoreStatus.Connecting)
		{

            int requestGeneration;Process previousProcess;VpnEngine preparedEngine;
            lock(routingSnapshotSync){if(Status==CoreStatus.Connected||Status==CoreStatus.Connecting)return;requestGeneration=RuntimeGeneration;previousProcess=process;preparedEngine=Engine;}
            if(!File.Exists(VpnEngines.ExecutablePath(preparedEngine)))throw new FileNotFoundException("Не найдено выбранное VPN-ядро: "+VpnEngines.DisplayName(preparedEngine),VpnEngines.ExecutablePath(preparedEngine));
            var preparedRouting=RoutingOverlayService.Clone(routing);
            var preparedProfiles = preparedRouting.MultiServer == null ? null : MultiServerConfigBuilder.CaptureProfiles(profiles);
            if (preparedProfiles != null) profile = MultiServerStartupSelection.ResolveCatalogIdentity(preparedRouting.MultiServer, preparedProfiles);
            string preparedConfig=BuildPreparedConfiguration(profile,preparedRouting,preparedEngine,preparedProfiles,Path.Combine(StateStore.DirectoryPath,"multi-server-dns-v2.db"));
            string preparedProfileKey = SubscriptionManager.ConfigKey(profile);
            bool managedConfiguration = UsesManagedConfiguration(profile,preparedRouting);
            // Multi-exit disabled-IPv6 mode still captures that family for an explicit reject.
            bool captureIpv6 = preparedRouting.EnableIpv6 || preparedRouting.MultiServer != null;
            var connectedNetworks = TunnelNetworkPreflight.Capture();
            TunnelNetworkPreflight.ManagedConfiguration tunnelNetwork = null;
            if (managedConfiguration) {
                tunnelNetwork = TunnelNetworkPreflight.PrepareManaged(preparedConfig, connectedNetworks, captureIpv6);
                preparedConfig = tunnelNetwork.Json;
            } else TunnelNetworkPreflight.Validate(preparedConfig, connectedNetworks);
            var pacPlan = preparedRouting.MultiServer==null?CorporatePacPlan.Freeze(profile, preparedConfig):null;
            // PAC's own WinHTTP/DNS traffic under TUN still needs physical-interface
            // acceptance. Do not turn a missing proof into recursive startup traffic.
            if (pacPlan != null) throw new NotSupportedException("PAC пока проверяется в изолированных локальных прокси. Подключение TUN по PAC ещё не готово.");
            int? stoppedGeneration=StopRuntime(requestGeneration,previousProcess);
            if(!stoppedGeneration.HasValue)return;
            int generation=stoppedGeneration.Value;
            ActiveCoreProbe checkChannel=null;string preparedPath=null;Process startedProcess=null;
            try {
            using(var routeReadiness=preparedEngine==VpnEngine.SingBox&&managedConfiguration?new TunnelRoutingReadiness(preparedRouting.MultiServer!=null):null){
            if(routeReadiness!=null)preparedConfig=routeReadiness.Apply(preparedConfig,captureIpv6);
            var preparedMonitor=preparedEngine==VpnEngine.SingBox&&managedConfiguration?new GameConnectionMonitor():null;
            if(preparedMonitor!=null)preparedConfig=preparedMonitor.Apply(preparedConfig);
            checkChannel=managedConfiguration?new ActiveCoreProbe():null;
            if(checkChannel!=null)preparedConfig=preparedRouting.MultiServer==null?checkChannel.Apply(preparedConfig):
                checkChannel.ApplyMulti(preparedConfig,preparedRouting.MultiServer,preparedProfiles);
            var preparedRoutingPlan=preparedRouting.MultiServer==null ? CompiledRoutingPlan.TryCapture(preparedConfig) : CompiledRoutingPlan.TryCaptureWithExits(preparedConfig,MultiServerConfigBuilder.ExitIdentities(preparedRouting.MultiServer,preparedProfiles));
            string runtimeDir=Path.Combine(StateStore.DirectoryPath,"runtime");
            preparedPath=PrivateRuntimeFiles.WriteCoreConfig(runtimeDir,preparedConfig);
            if(!TryAdoptPreparedStartup(generation,preparedEngine,preparedPath,preparedMonitor,checkChannel,routeReadiness)){
                SafeDeleteConfig(preparedPath);ReleaseProbe(checkChannel);return;
            }
			string validation = await RunCheckAsync(preparedPath,preparedEngine);
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				SafeDeleteConfig(preparedPath); ReleaseProbe(checkChannel);
				return;
			}
			if (!string.IsNullOrWhiteSpace(validation))
			{
                if(!TryReportStartupError(generation,preparedPath,null,FriendlyError(validation))){SafeDeleteConfig(preparedPath);ReleaseProbe(checkChannel);return;}

				SafeDeleteConfig(preparedPath); ReleaseProbe(checkChannel);
				throw new InvalidOperationException(FriendlyError(validation));
			}
			ProcessStartInfo start = new ProcessStartInfo
			{
				FileName = VpnEngines.ExecutablePath(preparedEngine),
                Arguments = VpnEngines.RunArguments(preparedEngine, preparedPath),
                RedirectStandardInput = preparedEngine == VpnEngine.Reality,
				WorkingDirectory = Path.GetDirectoryName(VpnEngines.ExecutablePath(preparedEngine)),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
				SafeDeleteConfig(preparedPath); ReleaseProbe(checkChannel);
				return;
			}
            if (!RevalidatePreparedNetwork(preparedConfig, preparedPath, generation)) { ReleaseProbe(checkChannel); return; }
            if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping) {
                SafeDeleteConfig(preparedPath); ReleaseProbe(checkChannel);
                return;
            }
            startedProcess=new Process();
            startedProcess.StartInfo=OwnedCoreProcess.Wrap(start,preparedPath,true,EcoModeRequested,tunnelLeaseScope:MachineTunnelLease.DesktopScope,revalidateTunnelNetwork:true);
            startedProcess.OutputDataReceived+=OnData;
            startedProcess.ErrorDataReceived+=OnData;
            startedProcess.Exited+=OnExited;
            if(!TryLaunchPreparedProcess(startedProcess,preparedPath,generation)){
                startedProcess.Dispose();SafeDeleteConfig(preparedPath);ReleaseProbe(checkChannel);return;
            }
			await Task.Delay(1100);
			if (generation != System.Threading.Volatile.Read(ref startGeneration) || stopping)
			{
                ReleaseProbe(checkChannel);
				return;
			}
			if (process == null || process.HasExited)
			{
				string raw = LastMeaningfulLog();
                var ended = process;
                bool leaseDenied = System.Threading.Volatile.Read(ref tunnelLeaseDenied) != 0 || ended != null && ended.HasExited && ended.ExitCode == MachineTunnelLease.UnavailableExitCode;
                bool networkRefused = System.Threading.Volatile.Read(ref hostNetworkRefused) != 0 || ended != null && ended.HasExited && ended.ExitCode == TunnelNetworkPreflight.HostRefusedExitCode;
                string startupError=leaseDenied?new TunnelLeaseBusyException().Message:networkRefused?TunnelNetworkPreflight.HostRefusedMessage:FriendlyError(raw);
                if(!TryReportStartupError(generation,preparedPath,startedProcess,startupError)){ReleaseProbe(checkChannel);return;}
                if(leaseDenied)throw new TunnelLeaseBusyException();
                throw new InvalidOperationException(startupError);
			}
            // A live sing-box process is not proof that Windows has finished publishing
            // the recreated TUN address. Starting a game during address detection can
            // bypass process_path matching because the core still sees a non-local source.
            if (preparedEngine == VpnEngine.SingBox && managedConfiguration) {
                try {
                    if (!await WaitForWindowsTunnelAsync(generation,startedProcess,tunnelNetwork.Addresses)) { ReleaseProbe(checkChannel); return; }
                    if (!await routeReadiness.WaitAsync(()=>IsPreparingRuntimeCurrent(generation,startedProcess), captureIpv6)) { ReleaseProbe(checkChannel); return; }
                }
                catch (Exception ex) {
                    int? failedGeneration=StopRuntime(generation,startedProcess);
                    if(!failedGeneration.HasValue||!TryReportStartupError(failedGeneration.Value,null,null,ex.Message,true)){ReleaseProbe(checkChannel);return;}
                    throw;
                }
            }
            ReleaseReadiness(routeReadiness);
            if(!TryCompleteStartWithCatalog(profile,preparedProfileKey,preparedRouting,preparedRoutingPlan,generation,startedProcess,preparedProfiles)){ReleaseProbe(checkChannel);return;}
			if(!NotifyCompletedStart(generation,startedProcess)){ReleaseProbe(checkChannel);return;}
			if(!StartInitialVerification(generation,startedProcess)){ReleaseProbe(checkChannel);return;}
            }
            } catch(Exception failure) {
                if(!CleanupFailedStartup(generation,preparedPath,startedProcess,checkChannel,failure))return;
                throw;
            }
		}
	}


    private bool CleanupFailedStartup(int generation,string preparedPath,Process candidate,ActiveCoreProbe probe,Exception failure)
    {
        string error=LogSanitizer.Sanitize(FriendlyError(failure.Message));TunnelRoutingReadiness ownedReadiness=null;
        try{
            Process owned;
            lock(routingSnapshotSync){
                if(generation!=RuntimeGeneration||configPath!=preparedPath||(process!=null&&!ReferenceEquals(process,candidate)))return false;
                owned=process;
                if(owned==null){
                    configPath=null;gameMonitor=null;ownedReadiness=activeReadiness;
                    if(ReferenceEquals(activeProbe,probe))activeProbe=null;
                    LastError=error;Status=CoreStatus.Error;
                }
            }
            if(owned!=null){
                int? stopped=StopRuntime(generation,owned);
                return stopped.HasValue&&TryReportStartupError(stopped.Value,null,null,error,true);
            }
            NotifyStatus(CoreStatus.Error,error,generation);
            lock(routingSnapshotSync)return generation==RuntimeGeneration&&Status==CoreStatus.Error&&process==null;
        }finally{
            // A failed graceful stop deliberately retains the live process and
            // its config. A stale handler cannot dispose replacement ownership.
            ReleaseReadiness(ownedReadiness);
            ReleaseProbe(probe);
            bool retainProcess,retainPath;
            lock(routingSnapshotSync){
                retainProcess=candidate!=null&&ReferenceEquals(process,candidate);
                retainPath=preparedPath!=null&&configPath==preparedPath&&(process!=null||Status==CoreStatus.Connecting||Status==CoreStatus.Connected);
            }
            if(!retainPath)SafeDeleteConfig(preparedPath);
            if(candidate!=null&&!retainProcess)candidate.Dispose();
        }
    }

    private bool TryAdoptPreparedStartup(int generation,VpnEngine expectedEngine,string preparedPath,GameConnectionMonitor monitor,ActiveCoreProbe probe,TunnelRoutingReadiness readiness)
    {
        lock(routingSnapshotSync){
            if(string.IsNullOrWhiteSpace(preparedPath)||generation!=RuntimeGeneration||Engine!=expectedEngine||!stopping||Status!=CoreStatus.Disconnected||process!=null||activeProbe!=null||activeReadiness!=null||configPath!=null||readiness?.IsCanceled==true)return false;
            configPath=preparedPath;gameMonitor=monitor;activeProbe=probe;activeReadiness=readiness;stopping=false;
            System.Threading.Volatile.Write(ref tunnelLeaseDenied,0);System.Threading.Volatile.Write(ref hostNetworkRefused,0);
            LastError="";lock(log)log.Clear();Status=CoreStatus.Connecting;
        }
        NotifyStatus(CoreStatus.Connecting,"Проверяем конфигурацию",generation);
        lock(routingSnapshotSync)return generation==RuntimeGeneration&&!stopping&&Status==CoreStatus.Connecting&&configPath==preparedPath&&ReferenceEquals(activeProbe,probe);
    }

    private bool TryLaunchPreparedProcess(Process child,string preparedPath,int generation)
    {
        lock(routingSnapshotSync){
            if(stopping||generation!=RuntimeGeneration||Status!=CoreStatus.Connecting||process!=null||configPath!=preparedPath)return false;
            // Do not publish an unstarted Process to Stop. Starting, publishing and
            // registering stream reads share the lifecycle capture lock.
            if(!child.Start())throw new IOException("Не удалось запустить VPN-ядро.");
            process=child;
            if(child.StartInfo.RedirectStandardOutput)child.BeginOutputReadLine();
            if(child.StartInfo.RedirectStandardError)child.BeginErrorReadLine();
            child.EnableRaisingEvents=true;
            return true;
        }
    }

    private bool TryReportStartupError(int generation,string preparedPath,Process expectedProcess,string message,bool stopped=false)
    {
        string error=LogSanitizer.Sanitize(message);
        lock(routingSnapshotSync){
            if(generation!=RuntimeGeneration||!ReferenceEquals(process,expectedProcess))return false;
            if(stopped){if(!stopping||Status!=CoreStatus.Disconnected||process!=null)return false;}
            else if(stopping||Status!=CoreStatus.Connecting||configPath!=preparedPath)return false;
            LastError=error;Status=CoreStatus.Error;gameMonitor=null;
        }
        NotifyStatus(CoreStatus.Error,error,generation);
        lock(routingSnapshotSync)return generation==RuntimeGeneration&&Status==CoreStatus.Error&&ReferenceEquals(process,expectedProcess)&&(stopped?stopping:!stopping&&configPath==preparedPath);
    }

    private void ReleaseProbe(ActiveCoreProbe expected)
    {
        if (expected == null) return;
        System.Threading.Interlocked.CompareExchange(ref activeProbe, null, expected);
        expected.Dispose();
    }

    private bool RevalidatePreparedNetwork(string preparedConfig, string preparedPath, int generation)
    {
        try {
            TunnelNetworkPreflight.Validate(preparedConfig, TunnelNetworkPreflight.Capture());
            return true;
        }
        catch (Exception error) {
            // No child has been created at this boundary. Readiness listeners
            // unwind through their enclosing using; do not stop another process.
            bool deleted = SafeDeleteConfig(preparedPath);
            string message=error.Message;
            if(!deleted)message+=" Временный конфиг не удалось удалить; он остаётся в защищённой папке NOVA.";
            if(!TryReportStartupError(generation,preparedPath,null,message))return false;
            throw;
        }
    }

    private bool IsPreparingRuntimeCurrent(int generation,Process expected)
    {
        lock(routingSnapshotSync){if(expected==null||stopping||generation!=RuntimeGeneration||Status!=CoreStatus.Connecting||!ReferenceEquals(process,expected))return false;}
        try{if(expected.HasExited)return false;}catch(InvalidOperationException){return false;}
        lock(routingSnapshotSync)return !stopping&&generation==RuntimeGeneration&&Status==CoreStatus.Connecting&&ReferenceEquals(process,expected);
    }
    private void ReleaseReadiness(TunnelRoutingReadiness expected)
    {
        if(expected==null)return;
        expected.Cancel();
        System.Threading.Interlocked.CompareExchange(ref activeReadiness,null,expected);
    }
    private async Task<bool> WaitForWindowsTunnelAsync(int generation,Process expected,string[] requiredAddresses)
    {
        var clock = Stopwatch.StartNew();
        var required = requiredAddresses.Select(IPAddress.Parse).ToArray();
        long stableSince = -1;
        // The first start can install the Windows tunnel driver before publishing its addresses.
        var startupTimeout = TimeSpan.FromSeconds(75);
        while (clock.Elapsed < startupTimeout) {
            if(!IsPreparingRuntimeCurrent(generation,expected))return false;
            bool ready = false;
            try {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals("NOVA", StringComparison.OrdinalIgnoreCase));
                if (adapter != null && adapter.OperationalStatus == OperationalStatus.Up) {
                    var addresses = adapter.GetIPProperties().UnicastAddresses;
                    if (addresses.Any(a => required.Contains(a.Address) && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate))
                        throw new IOException("Windows обнаружила конфликт адреса VPN-интерфейса NOVA.");
                    ready = required.All(ip => addresses.Any(a => a.Address.Equals(ip) && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred));
                }
            } catch (NetworkInformationException) { /* Interface can disappear while Windows recreates it. */ }
            if (ready) {
                if (stableSince < 0) stableSince = clock.ElapsedMilliseconds;
                if (clock.ElapsedMilliseconds - stableSince >= 500) return IsPreparingRuntimeCurrent(generation,expected);
            } else stableSince = -1;
            await Task.Delay(100);
        }
        if(!IsPreparingRuntimeCurrent(generation,expected))return false;
        throw new IOException("Windows не завершила подготовку VPN-интерфейса. Повторите подключение.");
    }

    public void Stop() => StopRuntime(null,null);
    private int? StopRuntime(int? expectedGeneration,Process expectedProcess)
    {
        Process owned; string ownedPath; ActiveCoreProbe ownedProbe; VpnEngine ownedEngine; int generation; VerificationOperation ownedVerification;TunnelRoutingReadiness ownedReadiness;
        lock(routingSnapshotSync){
            if(expectedGeneration.HasValue&&(expectedGeneration.Value!=RuntimeGeneration||!ReferenceEquals(process,expectedProcess)))return null;
            generation=System.Threading.Interlocked.Increment(ref startGeneration);stopping=true;InitialVerification=null;
            owned=process;ownedPath=configPath;ownedEngine=Engine;ownedReadiness=activeReadiness;
            ownedProbe=System.Threading.Interlocked.Exchange(ref activeProbe,null);
            System.Threading.Interlocked.Exchange(ref activeRoutingPlan,null);
            System.Threading.Interlocked.Exchange(ref activeRoutingProcess,null);
            gameMonitor=null;ownedVerification=DetachVerification();
        }
        // Cancellation callbacks may synchronously start another generation.
        // Stop only the resources captured before invoking those callbacks.
        ReleaseReadiness(ownedReadiness);
        CancelVerification(ownedVerification);
        ownedProbe?.Dispose();
        if(owned!=null){
            StopOwnedProcess(owned,ownedEngine);
            lock(routingSnapshotSync){if(ReferenceEquals(process,owned))process=null;}
            owned.Dispose();
        }
        SafeDeleteConfig(ownedPath);
        lock(routingSnapshotSync){
            if(generation!=RuntimeGeneration||!stopping||process!=null)return null;
            if(configPath==ownedPath)configPath=null;
            ConnectedAtUtc=default(DateTime);connectedProfile=null;connectedProfileKey=null;activeProfiles=null;
            Verification=new ConnectionVerification();Status=CoreStatus.Disconnected;
        }
        NotifyStatus(CoreStatus.Disconnected,"Не подключено",generation);
        return generation;
    }
    private void StopOwnedProcess(Process owned,VpnEngine? capturedEngine=null)
    {
        if (owned == null || owned.HasExited) return;
        if ((capturedEngine??Engine) == VpnEngine.Reality)
        {
            // Closing the private stdin pipe triggers native route/TUN cleanup.
            owned.StandardInput.Close();
            if (!owned.WaitForExit(10000) && !owned.HasExited)
                throw new InvalidOperationException("Ядро ещё завершает соединение. Повторите отключение; принудительная остановка не выполнена.");
        }
        else
        {
            owned.Kill();
            // The process can become signalled at the timeout boundary before
            // this thread resumes. Recheck this same retained process identity;
            // a completed termination must not leave the UI in a stop error.
            if (!owned.WaitForExit(3000) && !owned.HasExited) throw new InvalidOperationException("VPN-ядро ещё не завершилось.");
        }
    }

	public async Task<int> TestLatencyAsync(VpnProfile profile)
	{
		return (await HealthCheckService.TestAsync(profile)).LatencyMs;
	}

	public async Task<string> ValidateAsync(VpnProfile profile, RoutingSettings routing, System.Collections.Generic.IEnumerable<VpnProfile> profiles = null)
	{
		if (!IsCoreInstalled)
		{
			return "Не найдено выбранное VPN-ядро: " + VpnEngines.DisplayName(Engine);
		}
		string runtimeDir = Path.Combine(StateStore.DirectoryPath, "runtime");
		Directory.CreateDirectory(runtimeDir);
        string path = null;
		try
		{
            path = PrivateRuntimeFiles.WriteCoreConfig(runtimeDir, BuildConfig(profile, routing, routing?.MultiServer == null ? null : MultiServerConfigBuilder.CaptureProfiles(profiles)));
			return await RunCheckAsync(path);
		}
		catch (Exception ex)
		{
			return LogSanitizer.Sanitize(ex.Message);
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
	}

	public async Task<string> GetVersionAsync()
	{
		if (!IsCoreInstalled)
		{
			return "не установлено";
		}
		ProcessStartInfo start = new ProcessStartInfo
		{
			FileName = CorePath,
			Arguments = VpnEngines.VersionArguments(Engine),
			WorkingDirectory = Path.GetDirectoryName(CorePath),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		using (Process version = Process.Start(start))
		{
			string output = await version.StandardOutput.ReadToEndAsync();
			string error = await version.StandardError.ReadToEndAsync();
			await Task.Run(() => version.WaitForExit(5000));
			return LogSanitizer.Sanitize(string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim());
		}
	}

    public Task<ConnectionVerification> VerifyConnectionAsync()
    {
        try{
            var operation=BeginVerification();
            if(operation==null){
                lock(routingSnapshotSync)lock(verificationSync){
                    if(verificationOperation!=null)return OwnsVerificationContext(verificationOperation)?verificationOperation.Completion.Task:
                        Task.FromResult(new ConnectionVerification{Message="Подключение изменилось во время проверки"});
                    return Task.FromResult(Verification);
                }
            }
            _=CompleteVerificationAsync(operation);
            return operation.Completion.Task;
        }catch(Exception error){return Task.FromException<ConnectionVerification>(error);}
    }
    private async Task CompleteVerificationAsync(VerificationOperation operation)
    {
        try{operation.Completion.TrySetResult(await VerifyOwnedConnectionAsync(operation));}
        catch(Exception error){operation.Completion.TrySetException(error);}
    }
    private async Task<ConnectionVerification> VerifyOwnedConnectionAsync(VerificationOperation operation)
    {
        int generation=operation.Generation;
        VpnProfile profile=operation.Profile;
        ConnectionVerification result=new ConnectionVerification{ProfileId=operation.ProfileId};
		try {
        if(operation.Token.IsCancellationRequested||!IsCurrentVerification(operation))return result;
        if(operation.Channel?.HasMultiExits==true)return await VerifyMultiExitConnectionAsync(operation).ConfigureAwait(false);
		try
		{
            using (var handler = verificationHandlerFactory == null ? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false } : verificationHandlerFactory())
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) })
            foreach (string target in new[] { "https://cp.cloudflare.com/generate_204", "https://www.gstatic.com/generate_204" })
            {
                if (operation.Token.IsCancellationRequested || generation != startGeneration || Status != CoreStatus.Connected) break;
                try
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    using (var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, operation.Token))
                    {
                        if (operation.Token.IsCancellationRequested) break;
                        if (response.StatusCode != HttpStatusCode.NoContent) continue;
                        result.InternetOk = true;
                        result.DnsOk = true; // Successful validated HTTPS to a hostname proves resolution for this target.
                        result.InternetLatencyMs = (int)Math.Max(1L, watch.ElapsedMilliseconds);
                        break;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { }
            }
			result.Message = result.DnsOk && result.InternetOk ? "DNS и интернет работают через активный маршрут." : "Туннель запущен, но контрольный запрос не прошёл.";
		}
		catch (Exception ex)
		{
			result.Message = "Контрольный запрос: " + LogSanitizer.Sanitize(ex.Message);
		}
        if(operation.Token.IsCancellationRequested || generation!=startGeneration || Status!=CoreStatus.Connected || !IsCurrentVerification(operation))return result;
        var channel=operation.Channel;
        var proof = new ServerMapHealth {Supported=channel!=null, ProfileKey=operation.ProfileKey, NetworkKey=ProfileHealthEvidence.NetworkKey(), CoreKey=ProfileHealthEvidence.CoreKey(operation.CorePath), Message="Проверка активного ядра недоступна"};
        result.ProfileProbe=proof;
        if (profile != null && channel != null && generation == startGeneration && Status == CoreStatus.Connected && SubscriptionManager.ConfigKey(profile)==proof.ProfileKey)
        {
            foreach (string target in new[]{"https://cp.cloudflare.com/generate_204","https://www.gstatic.com/generate_204"})
            {
                if (operation.Token.IsCancellationRequested || generation!=startGeneration || Status!=CoreStatus.Connected || !ReferenceEquals(channel,activeProbe)) break;
                try {
                    var watch=Stopwatch.StartNew();
                    int status=await channel.ReadStatusAsync(new Uri(target),operation.Token);
                    if(operation.Token.IsCancellationRequested)break;
                    if(status!=204)continue;
                    proof.Success=true;proof.LatencyMs=(int)Math.Max(1,watch.ElapsedMilliseconds);proof.Target=target;proof.Message="HTTPS через активное VPN-ядро подтверждён";
                    break;
                } catch(Exception error) when(error is IOException || error is SocketException || error is System.Security.Authentication.AuthenticationException || error is OperationCanceledException || error is ObjectDisposedException) { proof.Message="HTTPS через активное VPN-ядро не прошёл"; }
            }
            if (String.IsNullOrEmpty(proof.NetworkKey) || String.IsNullOrEmpty(proof.CoreKey) || proof.CoreKey!=ProfileHealthEvidence.CoreKey(operation.CorePath) || proof.NetworkKey!=ProfileHealthEvidence.NetworkKey() || !ReferenceEquals(channel,activeProbe) || SubscriptionManager.ConfigKey(profile)!=proof.ProfileKey)
            {proof.Success=false;proof.Supported=false;proof.Message="Сеть, профиль или активное ядро изменились во время проверки";}
        }
        else if(channel!=null) {proof.Supported=false;proof.Message="Активный конфиг больше не соответствует профилю";}
        if(operation.Token.IsCancellationRequested || generation!=startGeneration || Status!=CoreStatus.Connected || !IsCurrentVerification(operation))return result;
        proof.CheckedUtc=DateTime.UtcNow;
        if(TryPublishVerification(operation,generation,result))VerificationChanged?.Invoke(result);
        else return new ConnectionVerification{ProfileId=operation.ProfileId,Message="Подключение изменилось во время проверки"};
		return result;
		} finally { FinishVerification(operation); }
	}

	private async Task<string> RunCheckAsync(string path,VpnEngine? capturedEngine=null)
	{
        VpnEngine selectedEngine=capturedEngine??Engine;string selectedPath=VpnEngines.ExecutablePath(selectedEngine);
        long sequence; lock(coreCheckObservationSync) { sequence=++coreCheckSequence;lastCoreCheck=null; }
        int generation=RuntimeGeneration;
        var preparation=Stopwatch.StartNew();string configHash=CheckConfigHash(path);double preparationMs=preparation.Elapsed.TotalMilliseconds;
		ProcessStartInfo start = new ProcessStartInfo
		{
			FileName = selectedPath,
            Arguments = VpnEngines.CheckArguments(selectedEngine, path),
			WorkingDirectory = Path.GetDirectoryName(selectedPath),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			StandardErrorEncoding = Encoding.UTF8,
			StandardOutputEncoding = Encoding.UTF8
		};
        Process check=null;int? pid=null,exitCode=null;long? created=null;double? cpuMs=null,peakBytes=null;
        bool waitExited=false,exitConfirmed=false,timedOut=false,terminationRequested=false;string errorType=null;
        double startMs=-1,waitStartMs=-1,launchWorkerMs=-1,launchCallMs=-1;var watch=Stopwatch.StartNew();
        try
        {
            Task<string> stdout=null,stderr=null;
            // Process creation and stream setup can block on OS/AV work; never hold the UI dispatcher.
            await Task.Run(()=>{
                launchWorkerMs=watch.Elapsed.TotalMilliseconds;var launch=Stopwatch.StartNew();
                try { check=validationProcessStarter(start); } finally { launchCallMs=launch.Elapsed.TotalMilliseconds; }
                startMs=watch.Elapsed.TotalMilliseconds;pid=check.Id;
                try { created=check.StartTime.ToUniversalTime().ToFileTimeUtc(); } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
                stdout=check.StandardOutput.ReadToEndAsync();stderr=check.StandardError.ReadToEndAsync();
                // Timeout cleanup does not await readers; observe eventual faults.
                _=stdout.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                _=stderr.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            });
            waitExited=await Task.Run(()=>{waitStartMs=watch.Elapsed.TotalMilliseconds;return check.WaitForExit(15000);});
            if(!waitExited)
            {
                timedOut=true;terminationRequested=true;
                // Only the same validation child is stopped; it never starts a tunnel.
                try { check.Kill(); } catch(InvalidOperationException) { }
                exitConfirmed=await Task.Run(()=>check.WaitForExit(3000));
                return "Проверка конфигурации ядра не завершилась за 15 секунд.";
            }
            string output=await stdout,error=await stderr;
            return check.ExitCode==0?"":LogSanitizer.Sanitize((error+Environment.NewLine+output).Trim());
        }
        catch(Exception error) { errorType=error.GetType().Name;throw; }
        finally
        {
            if(check!=null)
            {
                try { exitConfirmed=check.HasExited;if(exitConfirmed)exitCode=check.ExitCode; } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
                try { cpuMs=check.TotalProcessorTime.TotalMilliseconds;peakBytes=check.PeakWorkingSet64; } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
            }
            var observation=new CoreCheckObservation(sequence,generation,selectedEngine.ToString(),configHash,pid,created,preparationMs,startMs,waitStartMs,
                watch.Elapsed.TotalMilliseconds,waitExited,timedOut,terminationRequested,exitConfirmed,exitCode,cpuMs,peakBytes,errorType,launchWorkerMs,launchCallMs);
            lock(coreCheckObservationSync) { if(sequence==coreCheckSequence)lastCoreCheck=observation; }
            check?.Dispose();
        }
    }

	private void OnData(object sender, DataReceivedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(e.Data))
		{
			return;
		}
		lock (log)
		{
			log.AppendLine(LogSanitizer.Sanitize(e.Data));
			if (log.Length > 120000)
			{
				log.Remove(0, 40000);
			}
		}
		RecordReportLog(e.Data);
		this.LogReceived?.Invoke(LogSanitizer.Sanitize(e.Data));
	}

    private void OnExited(object sender, EventArgs e)
    {
        int generation; string exitedPath, error; ActiveCoreProbe exitedProbe; bool report; VerificationOperation exitedVerification;TunnelRoutingReadiness exitedReadiness;
        lock(routingSnapshotSync){
            if(!ReferenceEquals(process,sender))return;
            generation=RuntimeGeneration;
            report=!stopping;
            if(report)Status=CoreStatus.Error; // Refuse new probes before cancellation callbacks.
            bool leaseDenied=((Process)sender).ExitCode==MachineTunnelLease.UnavailableExitCode;
            bool networkRefused=((Process)sender).ExitCode==TunnelNetworkPreflight.HostRefusedExitCode;
            System.Threading.Volatile.Write(ref tunnelLeaseDenied,leaseDenied?1:0);
            System.Threading.Volatile.Write(ref hostNetworkRefused,networkRefused?1:0);
            error=leaseDenied?new TunnelLeaseBusyException().Message:networkRefused?TunnelNetworkPreflight.HostRefusedMessage:FriendlyError(LastMeaningfulLog());
            process=null;
            exitedPath=configPath;configPath=null;exitedReadiness=activeReadiness;
            exitedProbe=System.Threading.Interlocked.Exchange(ref activeProbe,null);
            System.Threading.Interlocked.Exchange(ref activeRoutingPlan,null);
            System.Threading.Interlocked.Exchange(ref activeRoutingProcess,null);
            gameMonitor=null;ConnectedAtUtc=default(DateTime);connectedProfile=null;connectedProfileKey=null;activeProfiles=null;InitialVerification=null;
            exitedVerification=DetachVerification();
        }
        // Cancellation can run arbitrary continuations synchronously, including a
        // replacement connection. Never read shared resource fields after it.
        ReleaseReadiness(exitedReadiness);
        CancelVerification(exitedVerification);
        exitedProbe?.Dispose();
        SafeDeleteConfig(exitedPath);
        lock(routingSnapshotSync){
            if(generation!=RuntimeGeneration||process!=null)return;
            if(!report){
                // A bounded Stop may already have returned while the captured
                // process was still exiting. Its later terminal event must
                // complete that stop rather than leave a stale Connected UI.
                if(!stopping||Status==CoreStatus.Disconnected)return;
                Verification=new ConnectionVerification();Status=CoreStatus.Disconnected;
            }else{
                if(stopping||Status!=CoreStatus.Error)return;
                LastError=error;
            }
        }
        if(!report){NotifyStatus(CoreStatus.Disconnected,"Не подключено",generation);return;}
        NotifyStatus(CoreStatus.Error,error,generation);
        // A status observer can itself replace/stop the connection.
        if(!stopping&&generation==RuntimeGeneration&&process==null&&Status==CoreStatus.Error)UnexpectedExit?.Invoke();
    }
	private string LastMeaningfulLog()
	{
		string text = Logs.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return "VPN-ядро завершило работу.";
		}
		string[] array = text.Replace("\r", "").Split('\n');
		return array[array.Length - 1];
	}

	private string FriendlyError(string raw)
	{
		raw = (raw ?? "").Trim();
		string text = raw.ToLowerInvariant();
		if (text.Contains("permission") || text.Contains("access is denied"))
		{
			return "Windows не дала создать TUN-адаптер. Запустите NOVA от имени администратора.";
		}
		if (text.Contains("certificate"))
		{
			return "Не удалось проверить сертификат VPN-сервера. Проверьте SNI и параметры TLS.";
		}
		if (text.Contains("connection refused"))
		{
			return "Сервер отклонил подключение. Проверьте адрес и порт.";
		}
		if (text.Contains("no such host") || text.Contains("name resolution") || text.Contains("lookup"))
		{
			return "Не удалось найти VPN-сервер через DNS. Проверьте адрес и подключение к интернету.";
		}
		if (text.Contains("timeout") || text.Contains("deadline exceeded"))
		{
			return "Сервер не ответил вовремя. Проверьте сеть или выберите другой сервер.";
		}
		if (text.Contains("authentication") || text.Contains("unauthorized"))
		{
			return "Сервер отклонил авторизацию. Проверьте ключ или обновите подписку.";
		}
		if (text.Contains("unknown field") || text.Contains("decode config"))
		{
			return "В ключе есть параметры, которые не поддерживает текущее ядро. Подробности доступны в журнале.";
		}
		if (raw.Length > 190)
		{
			raw = raw.Substring(raw.Length - 190);
		}
		if (!string.IsNullOrWhiteSpace(raw))
		{
			return raw;
		}
		return "Не удалось запустить VPN-ядро.";
	}

    private void NotifyStatus(CoreStatus status,string message,int generation)
    {
        RuntimeStatusChanged?.Invoke(status,message,generation);
        StatusChanged?.Invoke(status,message);
    }
	private void SafeDeleteConfig() { SafeDeleteConfig(configPath); }
    private static bool SafeDeleteConfig(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) return true;
        try { File.Delete(path); return true; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

	public void Dispose()
	{
		Stop();
	}
}

// Diagnostic metadata only: no raw config, paths, endpoints or child output.
internal sealed class CoreCheckObservation
{
    public long Sequence { get; }
    public int RuntimeGeneration { get; }
    public string Engine { get; }
    public string ConfigSha256 { get; }
    public int? ProcessId { get; }
    public long? ProcessCreatedFileTimeUtc { get; }
    public double PreparationMilliseconds { get; }
    public double ProcessStartMilliseconds { get; }
    public double LaunchWorkerStartMilliseconds { get; }
    public double LaunchCallMilliseconds { get; }
    public double WaitStartMilliseconds { get; }
    public double ElapsedMilliseconds { get; }
    public bool WaitReportedExited { get; }
    public bool TimedOut { get; }
    public bool TerminationRequested { get; }
    public bool ExitConfirmed { get; }
    public int? ExitCode { get; }
    public double? CpuMilliseconds { get; }
    public double? PeakWorkingSetBytes { get; }
    public string ErrorType { get; }
    internal CoreCheckObservation(long sequence,int generation,string engine,string configHash,int? pid,long? created,double preparationMs,double startMs,double waitMs,
        double elapsed,bool waited,bool timedOut,bool termination,bool exited,int? exitCode,double? cpuMs,double? peakBytes,string errorType,double launchWorkerStartMs=-1,double launchCallMs=-1)
    {
        Sequence=sequence;RuntimeGeneration=generation;Engine=engine;ConfigSha256=configHash;ProcessId=pid;ProcessCreatedFileTimeUtc=created;
        PreparationMilliseconds=preparationMs;ProcessStartMilliseconds=startMs;WaitStartMilliseconds=waitMs;ElapsedMilliseconds=elapsed;
        WaitReportedExited=waited;TimedOut=timedOut;TerminationRequested=termination;ExitConfirmed=exited;ExitCode=exitCode;
        CpuMilliseconds=cpuMs;PeakWorkingSetBytes=peakBytes;ErrorType=errorType;
        LaunchWorkerStartMilliseconds=launchWorkerStartMs;LaunchCallMilliseconds=launchCallMs;
    }
}

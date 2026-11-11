using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class LocalProxySnapshot
{
    public string Id { get; internal set; }
    public string Status { get; internal set; }
    public bool Running { get; internal set; }
    public bool Starting { get; internal set; }
    public bool StopRequested { get; internal set; }
    public bool Exists { get; internal set; }
    public bool ApplicationLimits { get; internal set; }
    public string ProfileKey { get; internal set; }
    public string Generation { get; internal set; }
    public bool DnsExists { get; internal set; }
    public bool DnsRunning { get; internal set; }
    public int DnsPort { get; internal set; }
}

public sealed class LocalProxyManager : IDisposable
{
    private sealed class Run
    {
        public string Id, File, PacDescriptor, Status = "Запускается", ProfileKey, ProfileId;
        public Process Process;
        public bool Ready, Starting = true;
        public readonly string Generation=Guid.NewGuid().ToString("N");
        public bool Controlled;
        public string CorePath;public LocalProxyEndpoint CoreEndpoint;
        public GatewayDnsService Dns;public CancellationTokenSource DnsOwner;public int DnsPort;
        public ApplicationBandwidthSocksListener Listener;
        public CancellationTokenRegistration ListenerCancellation;
        public CancellationTokenSource Cancel;
    }
    private readonly object sync = new object();
    private readonly Dictionary<string, Run> runs = new Dictionary<string, Run>(StringComparer.Ordinal);
    private readonly SemaphoreSlim mutation = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly string runtimeDirectory;
    private bool disposed;
    private long networkGeneration;
    public event Action Changed;
    internal int EntryCount { get { lock (sync) return runs.Count; } }
    public bool HasEntries { get { lock (sync) return runs.Count > 0; } }
    internal string[] ProfilesInUse { get { lock(sync) return runs.Values.Select(r=>r.ProfileId).Where(id=>!String.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray(); } }

    public LocalProxyManager(string runtimeDirectory) { this.runtimeDirectory = Path.GetFullPath(runtimeDirectory); NetworkChange.NetworkAddressChanged+=OnNetworkAddressChanged; }
    private void OnNetworkAddressChanged(object sender,EventArgs args)
    {
        CancellationTokenSource[] owners;
        lock(sync){if(disposed)return;networkGeneration++;owners=runs.Values.Select(run=>{lock(run)return run.DnsOwner;}).Where(owner=>owner!=null).ToArray();}
        // Capture owners before cancellation; a delayed callback cannot revoke a replacement.
        foreach(var owner in owners)try{owner.Cancel();}catch(ObjectDisposedException){}
        NotifyChanged();
    }
    public LocalProxySnapshot Snapshot(string id)
    {
        lock (sync)
        {
            if (id != null && runs.TryGetValue(id, out var run))
            {
                bool alive = false; try { alive = run.Process != null && !run.Process.HasExited; } catch (InvalidOperationException) { }
                bool dnsExists,dnsRunning;int dnsPort;lock(run){dnsExists=run.Dns!=null||run.DnsOwner!=null;dnsRunning=run.Dns?.IsListening==true&&!run.Cancel.IsCancellationRequested;dnsPort=run.DnsPort;}
                return new LocalProxySnapshot { DnsExists=dnsExists,DnsRunning=dnsRunning,DnsPort=dnsPort,Generation=run.Generation,Id = id, Status = run.Cancel.IsCancellationRequested ? "Остановка не подтверждена" : run.Ready && alive && run.Listener!=null && !run.Listener.IsRunning ? "Локальный вход остановился — повторите остановку и запуск." : run.Status, Exists = true, Running = run.Ready && alive && (run.Listener==null||run.Listener.IsRunning), Starting = run.Starting, StopRequested = run.Cancel.IsCancellationRequested, ApplicationLimits = run.Ready && alive && run.Listener != null && run.Listener.IsRunning, ProfileKey = run.ProfileKey };
            }
            return new LocalProxySnapshot { Id = id, Status = "Остановлен" };
        }
    }

    public Task StartAsync(LocalProxyEndpoint endpoint, VpnProfile profile, string corePath, CancellationToken token) =>
        StartAsync(endpoint, profile, corePath, VpnEngine.SingBox, false, false, token);

    public Task StartAsync(LocalProxyEndpoint endpoint, VpnProfile profile, string corePath, VpnEngine engine, bool measureResources, bool eco, CancellationToken token) =>
        StartInternalAsync(endpoint, profile, corePath, engine, measureResources, eco, token, null, null);

    // Explicit opt-in: mixed/HTTP must not silently bypass application limits.
    // Budgets are borrowed and shared by the owning client across shaped entries.
    public Task StartShapedSocksAsync(LocalProxyEndpoint endpoint, VpnProfile profile, string corePath, ApplicationBandwidthController budgets, Func<ApplicationBandwidthIdentity,string,int,bool> approve, CancellationToken token)
    {
        if(budgets==null||approve==null)throw new ArgumentNullException("application limits");
        if(endpoint==null||endpoint.InboundType!="socks")throw new NotSupportedException("Лимиты приложений пока требуют отдельный SOCKS-вход.");
        return StartInternalAsync(endpoint, profile, corePath, VpnEngine.SingBox, true, false, token, budgets, approve);
    }
    private async Task StartInternalAsync(LocalProxyEndpoint endpoint, VpnProfile profile, string corePath, VpnEngine engine, bool measureResources, bool eco, CancellationToken token, ApplicationBandwidthController budgets, Func<ApplicationBandwidthIdentity,string,int,bool> approve)
    {
        // Freeze inputs before the first await; subscription refresh cannot change an in-flight configuration.
        if (eco && !measureResources) throw new ArgumentException("EcoQoS требует подтверждаемого управления собственным процессом.");
        corePath=Path.GetFullPath(corePath);
        var serializer = new JavaScriptSerializer();
        // Preserve the public endpoint; only the owned core receives private auth.
        var coreEndpoint = serializer.Deserialize<LocalProxyEndpoint>(serializer.Serialize(endpoint));
        if(budgets!=null){
            string validation=LocalProxyConfiguration.Validate(endpoint,profile);if(validation!=null)throw new InvalidOperationException(validation);
            var privatePort=new TcpListener(IPAddress.Loopback,0);privatePort.Start();try{coreEndpoint.Port=((IPEndPoint)privatePort.LocalEndpoint).Port;}finally{privatePort.Stop();}
            if(coreEndpoint.Port==endpoint.Port)throw new IOException("Не удалось выделить отдельный порт ядра.");
            coreEndpoint.Username="core-"+Guid.NewGuid().ToString("N");coreEndpoint.Password=Guid.NewGuid().ToString("N");
        }
        string config = LocalProxyConfiguration.Build(coreEndpoint, profile, engine);
        var pacPlan = CorporatePacPlan.Freeze(profile, config, "workload");
        var json = new JavaScriptSerializer(); endpoint = json.Deserialize<LocalProxyEndpoint>(json.Serialize(endpoint));
        var run = new Run { Id = endpoint.Id, ProfileId = endpoint.ProfileId, Controlled = measureResources, CorePath=corePath, CoreEndpoint=json.Deserialize<LocalProxyEndpoint>(json.Serialize(coreEndpoint)), ProfileKey = SubscriptionManager.ConfigKey(profile), Cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token) };
        lock (sync)
        {
            if (disposed) { run.Cancel.Dispose(); throw new ObjectDisposedException(nameof(LocalProxyManager)); }
            if (runs.ContainsKey(run.Id)) { run.Cancel.Dispose(); throw new InvalidOperationException("Этот прокси уже запущен или запускается."); }
            if (runs.Count >= LocalProxyConfiguration.MaxRunning) { run.Cancel.Dispose(); throw new InvalidOperationException("Одновременно допускается до восьми локальных прокси."); }
            runs.Add(run.Id, run);
        }
        NotifyChanged(); bool acquired = false;
        try
        {
            await mutation.WaitAsync(run.Cancel.Token).ConfigureAwait(false); acquired = true;
            if (!File.Exists(corePath)) throw new FileNotFoundException("Не найдено выбранное VPN-ядро.");
            var reservation = new TcpListener(IPAddress.Loopback, endpoint.Port); reservation.ExclusiveAddressUse = true;
            try { reservation.Start(); } catch (SocketException) { throw new InvalidOperationException("Порт занят другим приложением. Выберите другой порт."); } finally { reservation.Stop(); }
            run.File = PrivateRuntimeFiles.WriteCoreConfig(runtimeDirectory, config);
            await ValidateCoreAsync(corePath, run.File, engine, run.Cancel.Token).ConfigureAwait(false);
            run.Cancel.Token.ThrowIfCancellationRequested();
            run.PacDescriptor = pacPlan?.Prepare(run.File);
            run.Process = Launch(corePath, VpnEngines.RunArguments(engine, run.File), run.File, measureResources, eco, run.PacDescriptor);
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(run.Cancel.Token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(7));
                bool listening = false;
                while (!run.Process.HasExited)
                {
                    ready.Token.ThrowIfCancellationRequested();
                    try { listening = await CheckListenerAsync(coreEndpoint, ready.Token).ConfigureAwait(false); }
                    catch (SocketException) { } catch (IOException) { }
                    if (listening) break;
                    await Task.Delay(80, ready.Token).ConfigureAwait(false);
                }
                if (!listening) throw new IOException("Ядро не подтвердило запуск локального входа.");
                await Task.Delay(350, ready.Token).ConfigureAwait(false);
                if (run.Process.HasExited) throw new IOException("Ядро завершилось во время запуска.");
            }
            if(budgets!=null){
                // ReadAsync authenticates the host pipe and its cached job-owned
                // primary child; the socket preparer then matches native PID,
                // creation time, SID and literal core image before credentials.
                var child=await CoreResourceClient.ReadAsync(run.Process,run.Cancel.Token).ConfigureAwait(false);
                string sid;using(var owner=System.Security.Principal.WindowsIdentity.GetCurrent())sid=owner.User.Value;
                var identity=new ApplicationBandwidthIdentity(sid,Path.GetFullPath(corePath));
                long created=new DateTime(child.ProcessStartUtcTicks,DateTimeKind.Utc).ToFileTimeUtc();
                run.Cancel.Token.ThrowIfCancellationRequested();
                run.Listener=new ApplicationBandwidthSocksListener(budgets,endpoint.Port,new IPEndPoint(IPAddress.Loopback,coreEndpoint.Port),sid,endpoint.Username,endpoint.Password,(application,destination,port)=>{run.Cancel.Token.ThrowIfCancellationRequested();return approve(application,destination,port);},
                    (destination,port)=>new ApplicationBandwidthSocksCore(child.ProcessId,created,identity,coreEndpoint.Username,coreEndpoint.Password,destination,port));
                run.ListenerCancellation=run.Cancel.Token.Register(()=>run.Listener?.Dispose());
                if(run.Process.HasExited||run.Cancel.IsCancellationRequested)throw new OperationCanceledException(run.Cancel.Token);
            }
            lock (sync) { run.Ready = true; run.Starting = false; run.Status = "Вход запущен · внешний сервер отдельно не проверен"; }
            run.Process.Exited += (s, e) => { RevokeDns(run);run.Listener?.Dispose(); lock (sync) { run.Ready = false; run.Starting = false; run.Status = "Ядро остановилось — нажмите «Остановить» перед повторным запуском"; } NotifyChanged(); };
            run.Process.EnableRaisingEvents = true;
            NotifyChanged();
        }
        catch
        {
            if (!acquired) { await mutation.WaitAsync().ConfigureAwait(false); acquired = true; }
            bool owned; lock (sync) owned = runs.TryGetValue(run.Id, out var current) && ReferenceEquals(current, run);
            if (owned)
            {
                try { await StopRunAsync(run).ConfigureAwait(false); lock (sync) runs.Remove(run.Id); run.Cancel.Dispose(); }
                catch { lock (sync) { run.Starting = false; run.Status = "Не удалось завершить прокси. Повторите остановку."; } throw; }
                finally { NotifyChanged(); }
            }
            throw;
        }
        finally { if (acquired) mutation.Release(); }
    }

    // Borrow only the authenticated native child of a frozen managed SOCKS run.
    // No LAN/firewall/DNS settings are changed; caller owns network/profile scope.
    public async Task StartDnsAsync(string id,string expectedProfileKey,string expectedGeneration,IPAddress address,int port,IEnumerable<IPAddress> clients,IPAddress resolver,int resolverPort=53,CancellationToken token=default(CancellationToken))
    {
        if(clients==null||resolver==null)throw new ArgumentNullException("DNS endpoint");
        if(port<1||port>65535||resolverPort<1||resolverPort>65535)throw new ArgumentOutOfRangeException("DNS port");
        var bind=IPAddress.Parse(GatewayDnsTcpServer.Address(address));var destination=IPAddress.Parse(resolver.ToString());
        var allowed=clients.Take(65).Select(ip=>ip==null?null:new IPAddress(ip.GetAddressBytes())).ToArray();
        long network;lock(sync){if(disposed)throw new ObjectDisposedException(nameof(LocalProxyManager));network=networkGeneration;}
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try{
            Run run;lock(sync){if(disposed||id==null||!runs.TryGetValue(id,out run)||!run.Ready||run.Cancel.IsCancellationRequested||!run.Controlled||String.IsNullOrEmpty(expectedProfileKey)||run.ProfileKey!=expectedProfileKey||String.IsNullOrEmpty(expectedGeneration)||run.Generation!=expectedGeneration)throw new InvalidOperationException("Нужен действующий собственный профиль с управляемым ядром.");
            if(network!=networkGeneration)throw new InvalidOperationException("Сеть изменилась во время запуска DNS. Повторите запуск.");
            if(run.Listener!=null||run.CoreEndpoint.InboundType!="socks")throw new NotSupportedException("DNS шлюза пока требует отдельный управляемый SOCKS-вход без лимитов приложений.");
            lock(run){if(run.DnsOwner!=null||run.Dns!=null)throw new InvalidOperationException("Сначала завершите предыдущий DNS-вход.");run.DnsOwner=CancellationTokenSource.CreateLinkedTokenSource(token,run.Cancel.Token);run.DnsPort=port;}}
            try{
                CancellationToken owner;lock(run)owner=run.DnsOwner.Token;
                var child=await CoreResourceClient.ReadAsync(run.Process,owner).ConfigureAwait(false);owner.ThrowIfCancellationRequested();
                string sid;using(var user=System.Security.Principal.WindowsIdentity.GetCurrent())sid=user.User.Value;
                var transport=new GatewayDnsTcpClient(new IPEndPoint(IPAddress.Loopback,run.CoreEndpoint.Port),child.ProcessId,new DateTime(child.ProcessStartUtcTicks,DateTimeKind.Utc).ToFileTimeUtc(),new ApplicationBandwidthIdentity(sid,run.CorePath),run.CoreEndpoint.Username,run.CoreEndpoint.Password,destination,resolverPort);
                var dns=new GatewayDnsService(transport,bind,port,allowed,owner);lock(run)run.Dns=dns;
                _=ObserveDnsCompletionAsync(run,dns);
                await dns.StartAsync().ConfigureAwait(false);owner.ThrowIfCancellationRequested();
                if(!dns.IsListening)throw new IOException("DNS-вход не подтвердил запуск.");
            }catch(Exception failure){try{await StopDnsRunAsync(run).ConfigureAwait(false);}catch(Exception cleanup){throw new AggregateException("Не удалось запустить и завершить DNS-вход.",failure,cleanup);}throw;}
        }finally{mutation.Release();NotifyChanged();}
    }
    private async Task ObserveDnsCompletionAsync(Run run,GatewayDnsService dns)
    {
        // Completion retains cleanup errors for StopDnsAsync; this observer only refreshes state.
        try{await dns.Completion.ConfigureAwait(false);}catch(Exception){}
        lock(sync){if(disposed||!runs.TryGetValue(run.Id,out var current)||!ReferenceEquals(current,run))return;lock(run)if(!ReferenceEquals(run.Dns,dns))return;}
        NotifyChanged();
    }
    public async Task StopDnsAsync(string id)
    {
        Run run;CancellationTokenSource owner;
        lock(sync){if(id==null||!runs.TryGetValue(id,out run))return;lock(run)owner=run.DnsOwner;}
        if(owner==null)return;
        var original=run;try{owner.Cancel();}catch(ObjectDisposedException){}
        await mutation.WaitAsync().ConfigureAwait(false);
        try{
            lock(sync){if(!runs.TryGetValue(id,out run)||!ReferenceEquals(run,original))return;lock(run)if(!ReferenceEquals(run.DnsOwner,owner))return;}
            await StopDnsRunAsync(run).ConfigureAwait(false);
        }finally{mutation.Release();NotifyChanged();}
    }
    static void RevokeDns(Run run){CancellationTokenSource owner;lock(run)owner=run.DnsOwner;try{owner?.Cancel();}catch(ObjectDisposedException){}}
    static async Task StopDnsRunAsync(Run run)
    {
        RevokeDns(run);GatewayDnsService dns;CancellationTokenSource owner;lock(run){dns=run.Dns;owner=run.DnsOwner;}
        if(dns!=null)await dns.StopAsync().ConfigureAwait(false);
        lock(run){if(!ReferenceEquals(run.Dns,dns)||!ReferenceEquals(run.DnsOwner,owner))throw new InvalidOperationException("Владелец DNS-входа изменился.");run.Dns=null;run.DnsOwner=null;run.DnsPort=0;}
        owner?.Dispose();
    }
    public async Task<CoreResourceSnapshot> ReadResourcesAsync(string id, CancellationToken token)
    {
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Run run;
            lock (sync)
                if (disposed || id == null || !runs.TryGetValue(id, out run) || !run.Ready || !run.Controlled)
                    throw new InvalidOperationException("Нет запущенного собственного прокси с измерением нагрузки.");
            return await CoreResourceClient.ReadAsync(run.Process, token).ConfigureAwait(false);
        }
        finally { mutation.Release(); }
    }

    public async Task<Dictionary<string, bool>> StopInvalidProfilesAsync(IEnumerable<VpnProfile> profiles, Func<VpnProfile, bool> allowed)
    {
        if (profiles == null || allowed == null) throw new ArgumentNullException();
        var current = profiles.Where(p => p != null && !string.IsNullOrEmpty(p.Id)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : null);
        Run[] active; lock (sync) active = runs.Values.ToArray();
        // Evaluate caller-owned policy on the calling thread before any asynchronous stop.
        var invalid = active.Where(run => run.Cancel.IsCancellationRequested || !current.TryGetValue(run.ProfileId, out var p) || p == null || p.RetiredFromSubscription || SubscriptionManager.ConfigKey(p) != run.ProfileKey || !allowed(p)).ToArray();
        var result = new Dictionary<string, bool>();
        // Results describe the captured generation; a same-ID replacement is not its stop target.
        foreach (var captured in invalid)
        {
            try { await StopCapturedAsync(captured.Id,captured).ConfigureAwait(false); result[captured.Id] = true; }
            catch (Exception) { result[captured.Id] = false; }
        }
        return result;
    }

    public Task StopAsync(string id)=>StopCapturedAsync(id,null);
    private async Task StopCapturedAsync(string id,Run expected)
    {
        Run run;
        lock (sync) { if (id == null || !runs.TryGetValue(id, out run) || expected!=null&&!ReferenceEquals(run,expected)) return; }
        var original=run;try { run.Cancel.Cancel(); } catch (ObjectDisposedException) { return; }
        await mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) { if (!runs.TryGetValue(id, out run)||!ReferenceEquals(run,original)) return; }
            await StopRunAsync(run).ConfigureAwait(false); lock (sync) runs.Remove(id); run.Cancel.Dispose(); NotifyChanged();
        }
        finally { mutation.Release(); }
    }

    private static Process Launch(string core, string arguments, string cleanup, bool control = false, bool eco = false, string pacDescriptor = null)
    {
        var info = new ProcessStartInfo(core, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(core) };
        var process = new Process { StartInfo = OwnedCoreProcess.Wrap(info, cleanup, control, eco, pacDescriptor) };
        process.OutputDataReceived += delegate { }; process.ErrorDataReceived += delegate { };
        try { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process; }
        catch { process.Dispose(); throw; }
    }

    private static async Task ValidateCoreAsync(string core, string file, VpnEngine engine, CancellationToken token)
    {
        using (var process = Launch(core, VpnEngines.CheckArguments(engine, file), null))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { while (!process.HasExited) { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(40, deadline.Token).ConfigureAwait(false); } if (process.ExitCode != 0) throw new InvalidOperationException("Ядро отклонило конфигурацию локального прокси."); }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
        }
    }

    private static async Task<bool> CheckListenerAsync(LocalProxyEndpoint endpoint, CancellationToken token)
    {
        using (var client = new TcpClient())
        using (token.Register(() => client.Close()))
        {
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port).ConfigureAwait(false);
            var stream = client.GetStream();
            if (endpoint.InboundType == "http")
            {
                byte[] request = Encoding.ASCII.GetBytes("GET http://listener-check.invalid/ HTTP/1.1\r\nHost: listener-check.invalid\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);
                byte[] response = await ReadAsync(stream, 12, token).ConfigureAwait(false);
                return Encoding.ASCII.GetString(response).StartsWith("HTTP/1.1 407", StringComparison.Ordinal);
            }
            await stream.WriteAsync(new byte[] { 5, 1, 2 }, 0, 3, token).ConfigureAwait(false);
            var greeting = await ReadAsync(stream, 2, token).ConfigureAwait(false); if (greeting[0] != 5 || greeting[1] != 2) return false;
            byte[] user = Encoding.UTF8.GetBytes(endpoint.Username), password = Encoding.UTF8.GetBytes(endpoint.Password);
            byte[] auth = new byte[user.Length + password.Length + 3]; auth[0] = 1; auth[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, auth, 2, user.Length); auth[user.Length + 2] = (byte)password.Length; Buffer.BlockCopy(password, 0, auth, user.Length + 3, password.Length);
            await stream.WriteAsync(auth, 0, auth.Length, token).ConfigureAwait(false);
            byte[] accepted = await ReadAsync(stream, 2, token).ConfigureAwait(false); return accepted[0] == 1 && accepted[1] == 0;
        }
    }
    private static async Task<byte[]> ReadAsync(Stream stream, int size, CancellationToken token)
    {
        var bytes = new byte[size]; int offset = 0;
        while (offset < size) { int read = await stream.ReadAsync(bytes, offset, size - offset, token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); offset += read; } return bytes;
    }
    private static async Task StopRunAsync(Run run)
    {
        run.Cancel.Cancel();
        await StopDnsRunAsync(run).ConfigureAwait(false);
        if(run.Listener!=null){await run.Listener.StopAsync().ConfigureAwait(false);run.Listener.Dispose();run.Listener=null;}
        run.ListenerCancellation.Dispose();
        if (run.Process != null)
        {
            if (!run.Process.HasExited) { run.Process.Kill(); if (!run.Process.WaitForExit(3000)) throw new IOException("Не удалось завершить собственный процесс прокси."); }
            run.Process.Dispose(); run.Process = null;
        }
        if (run.File != null && File.Exists(run.File)) File.Delete(run.File);
        if (run.PacDescriptor != null && File.Exists(run.PacDescriptor)) File.Delete(run.PacDescriptor);
    }
    private void NotifyChanged() { try { Changed?.Invoke(); } catch (Exception) { /* A closing view cannot prevent owned-core cleanup. */ } }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        NetworkChange.NetworkAddressChanged-=OnNetworkAddressChanged;
        // Cancellation can synchronously run cleanup continuations and process-exit
        // handlers. Never invoke it while holding the state lock.
        lifetime.Cancel();
        mutation.Wait();
        try
        {
            Run[] active; lock (sync) active = runs.Values.ToArray();
            var errors = new List<Exception>();
            foreach (var run in active)
            {
                try { StopRunAsync(run).GetAwaiter().GetResult(); lock (sync) runs.Remove(run.Id); run.Cancel.Dispose(); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new AggregateException("Не удалось завершить часть локальных прокси.", errors);
        }
        finally { mutation.Release(); }
    }
}

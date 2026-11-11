using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public enum BandwidthDirection { Upload, Download }

// Identity supplied by a future authenticated capture backend. This value object
// does not attest a Windows process, SID, executable image or socket owner.
public sealed class ApplicationBandwidthIdentity
{
    public string UserSid { get; }
    public string ExecutablePath { get; }
    internal string Key=>UserSid+"|"+ExecutablePath;
    public ApplicationBandwidthIdentity(string userSid,string executablePath)
    {
        UserSid=new SecurityIdentifier(userSid).Value;
        // Kernel image names are literal paths: expanding %VAR% could attribute
        // a valid percent-containing executable name to a different application.
        if(String.IsNullOrWhiteSpace(executablePath)||executablePath!=executablePath.Trim()||!Path.IsPathRooted(executablePath)||!String.Equals(Path.GetExtension(executablePath),".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Нужен полный путь к исполняемому файлу приложения.");
        string root=Path.GetPathRoot(executablePath);
        if(root=="\\"||root=="/")throw new ArgumentException("Нужен абсолютный путь с диском или сетевым ресурсом.");
        ExecutablePath=Path.GetFullPath(executablePath);
    }
}

// Shared admission budgets only. No system QoS/WFP/TUN activation, persistence or
// automatic routing is implied. Capture must authenticate identity and use the
// same registry/budget for all streams and datagrams belonging to that identity.
public sealed class ApplicationBandwidthController : IDisposable
{
    readonly object sync=new object();
    readonly Dictionary<string,ApplicationBandwidthBudget> applications=new Dictionary<string,ApplicationBandwidthBudget>(StringComparer.OrdinalIgnoreCase);
    readonly int maximumApplications;
    readonly Func<double> clock;
    readonly Func<TimeSpan,CancellationToken,Task> delay;
    bool disposed;
    public ApplicationBandwidthController(int maximumApplications=1024):this(maximumApplications,()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,(time,token)=>Task.Delay(time,token)){}
    internal ApplicationBandwidthController(int maximumApplications,Func<double> clock,Func<TimeSpan,CancellationToken,Task> delay)
    {
        if(maximumApplications<1||maximumApplications>65536||clock==null||delay==null)throw new ArgumentException("Некорректные параметры ограничителя скорости.");
        this.maximumApplications=maximumApplications;this.clock=clock;this.delay=delay;
    }
    public ApplicationBandwidthBudget Get(ApplicationBandwidthIdentity identity)
    {
        if(identity==null)throw new ArgumentNullException(nameof(identity));
        lock(sync){
            if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthController));
            if(applications.TryGetValue(identity.Key,out var budget))return budget;
            if(applications.Count>=maximumApplications)throw new InvalidOperationException("Достигнут предел приложений ограничителя скорости.");
            budget=new ApplicationBandwidthBudget(clock,delay);applications.Add(identity.Key,budget);return budget;
        }
    }
    public ApplicationBandwidthBudget Configure(ApplicationBandwidthIdentity identity,long uploadBytesPerSecond,long downloadBytesPerSecond,int burstBytes=65536)
    {
        ApplicationBandwidthBudget.Validate(uploadBytesPerSecond,downloadBytesPerSecond,burstBytes);
        lock(sync){var budget=Get(identity);budget.Configure(uploadBytesPerSecond,downloadBytesPerSecond,burstBytes);return budget;}
    }
    // Keep the registry lock through the synchronous durable commit: new capture
    // identities cannot consume the preflighted capacity while settings save.
    // Existing transfers use their budget locks and continue independently.
    internal void CommitPolicy(ApplicationBandwidthPolicySnapshot previous,ApplicationBandwidthPolicySnapshot next,Action commit)
    {
        if(previous==null||next==null||commit==null)throw new ArgumentNullException("bandwidth commit");
        var before=previous.Policies.ToDictionary(p=>p.Identity.Key,StringComparer.OrdinalIgnoreCase);
        var after=next.Policies.ToDictionary(p=>p.Identity.Key,StringComparer.OrdinalIgnoreCase);
        lock(sync){
            void CheckCapacity(){if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthController));if(applications.Count+after.Keys.Count(key=>!applications.ContainsKey(key))>maximumApplications)throw new InvalidOperationException("Достигнут предел приложений в текущем сеансе. Остановите локальные прокси перед изменением списка.");}
            CheckCapacity();foreach(var old in before.Values)if(!applications.ContainsKey(old.Identity.Key))throw new InvalidOperationException("Прежний бюджет приложения не найден.");
            commit();CheckCapacity();
            // Retired budgets stay registered: a still-running flow and a later
            // re-added rule must never acquire independent pools for one app.
            foreach(var old in before.Values)if(!after.ContainsKey(old.Identity.Key))applications[old.Identity.Key].Configure(0,0,old.BurstBytes);
            foreach(var row in after.Values){
                if(!before.TryGetValue(row.Identity.Key,out var old)||old.UploadBytesPerSecond!=row.UploadBytesPerSecond||old.DownloadBytesPerSecond!=row.DownloadBytesPerSecond||old.BurstBytes!=row.BurstBytes)Get(row.Identity).Configure(row.UploadBytesPerSecond,row.DownloadBytesPerSecond,row.BurstBytes);
            }
        }
    }
    public void Dispose(){ApplicationBandwidthBudget[] values;lock(sync){if(disposed)return;disposed=true;values=applications.Values.ToArray();applications.Clear();}foreach(var value in values)value.Dispose();}
}

public sealed class ApplicationBandwidthBudget : IDisposable
{
    public const long MaximumBytesPerSecond=1000000000000L;
    public const int MaximumBurstBytes=1048576,MaximumPendingPerDirection=128;
    sealed class Bucket
    {
        public long Rate;
        public double Tokens,Last;
        public int Pending;
        public readonly SemaphoreSlim Gate=new SemaphoreSlim(1,1);
    }
    readonly object sync=new object();
    readonly Bucket upload=new Bucket(),download=new Bucket();
    readonly Func<double> clock;
    readonly Func<TimeSpan,CancellationToken,Task> delay;
    readonly CancellationTokenSource lifetime=new CancellationTokenSource();
    readonly CancellationToken lifetimeToken;
    TaskCompletionSource<bool> changed=Signal();
    int burst=65536;
    bool disposed;
    static TaskCompletionSource<bool> Signal()=>new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ApplicationBandwidthBudget(Func<double> clock,Func<TimeSpan,CancellationToken,Task> delay)
    {
        this.clock=clock;this.delay=delay;lifetimeToken=lifetime.Token;
        upload.Last=download.Last=Now();
    }
    static bool Finite(double value)=>!Double.IsNaN(value)&&!Double.IsInfinity(value);
    double Now(){double value=clock();if(!Finite(value))throw new InvalidOperationException("Недоступны монотонные часы ограничителя.");return value;}
    internal static void Validate(long up,long down,int burst)
    {
        if(up<0||down<0||up>MaximumBytesPerSecond||down>MaximumBytesPerSecond||burst<1||burst>MaximumBurstBytes)throw new ArgumentOutOfRangeException("bandwidth");
    }
    internal void Configure(long up,long down,int size)
    {
        Validate(up,down,size);TaskCompletionSource<bool> previous;
        lock(sync){
            if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthBudget));
            double now=Now();
            // Policy edits never mint fresh burst credit or leave an old low-rate
            // timer asleep after an increase/removal. Active flows retain identity.
            upload.Rate=up;download.Rate=down;upload.Tokens=download.Tokens=0;
            upload.Last=download.Last=Math.Max(Math.Max(upload.Last,download.Last),now);burst=size;previous=changed;changed=Signal();
        }
        previous.TrySetResult(true);
    }
    public int Pending(BandwidthDirection direction)=>Volatile.Read(ref Choose(direction).Pending);
    Bucket Choose(BandwidthDirection direction){if(direction==BandwidthDirection.Upload)return upload;if(direction==BandwidthDirection.Download)return download;throw new ArgumentOutOfRangeException(nameof(direction));}
    // Stream callers may consume a prefix and then request the remaining bytes.
    public Task<int> AcquireStreamAsync(BandwidthDirection direction,int maximumBytes,CancellationToken token)=>AcquireAsync(direction,maximumBytes,false,token);
    // Datagram callers receive all bytes or no grant; no splitting or bypass.
    public Task<int> AcquireDatagramAsync(BandwidthDirection direction,int bytes,CancellationToken token)=>AcquireAsync(direction,bytes,true,token);
    async Task<int> AcquireAsync(BandwidthDirection direction,int requested,bool atomic,CancellationToken token)
    {
        if(requested<1||requested>MaximumBurstBytes)throw new ArgumentOutOfRangeException(nameof(requested));
        var bucket=Choose(direction);lock(sync){if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthBudget));token.ThrowIfCancellationRequested();if(bucket.Rate==0)return requested;}
        if(Interlocked.Increment(ref bucket.Pending)>MaximumPendingPerDirection){Interlocked.Decrement(ref bucket.Pending);throw new InvalidOperationException("Очередь ограничителя скорости заполнена.");}
        bool entered=false;
        try{
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetimeToken)){
                await bucket.Gate.WaitAsync(linked.Token).ConfigureAwait(false);entered=true;
                while(true){
                    linked.Token.ThrowIfCancellationRequested();Task signal;TimeSpan wait;
                    lock(sync){
                        if(disposed)throw new OperationCanceledException(lifetimeToken);
                        if(bucket.Rate==0)return requested;
                        int wanted=atomic?requested:Math.Min(requested,burst);
                        if(wanted>burst)throw new ArgumentException("Датаграмма превышает ёмкость текущего лимита.");
                        double now=Now(),elapsed=Math.Max(0,now-bucket.Last);bucket.Last=Math.Max(bucket.Last,now);
                        bucket.Tokens=bucket.Rate==0?0:Math.Min(burst,bucket.Tokens+elapsed*bucket.Rate);
                        if(bucket.Tokens>=wanted){bucket.Tokens-=wanted;return wanted;}
                        double milliseconds=Math.Ceiling((wanted-bucket.Tokens)/bucket.Rate*1000);
                        wait=TimeSpan.FromMilliseconds(Math.Max(1,Math.Min(60000,milliseconds)));signal=changed.Task;
                    }
                    using(var timer=CancellationTokenSource.CreateLinkedTokenSource(linked.Token)){
                        Task paused=delay(wait,timer.Token);
                        try{var completed=await Task.WhenAny(paused,signal).ConfigureAwait(false);if(completed==paused)await paused.ConfigureAwait(false);}
                        finally{timer.Cancel();}
                    }
                }
            }
        }finally{if(entered)bucket.Gate.Release();Interlocked.Decrement(ref bucket.Pending);}
    }
    // Bounded read-ahead controls delivered payload, not physical link bytes.
    // A capture/transport integration must also own routing and socket lifecycle.
    public async Task<long> CopyPayloadAsync(BandwidthDirection direction,Stream source,Stream destination,CancellationToken token)
    {
        if(source==null||destination==null)throw new ArgumentNullException("bandwidth stream");
        Choose(direction);lock(sync)if(disposed)throw new ObjectDisposedException(nameof(ApplicationBandwidthBudget));
        using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetimeToken)){
            byte[] buffer=new byte[16384];long copied=0;
            while(true){linked.Token.ThrowIfCancellationRequested();int count=await source.ReadAsync(buffer,0,buffer.Length,linked.Token).ConfigureAwait(false);if(count==0)return copied;
                int at=0;while(at<count){int amount=await AcquireStreamAsync(direction,count-at,linked.Token).ConfigureAwait(false);await destination.WriteAsync(buffer,at,amount,linked.Token).ConfigureAwait(false);at+=amount;copied=checked(copied+amount);}
            }
        }
    }
    public void Dispose(){TaskCompletionSource<bool> previous;lock(sync){if(disposed)return;disposed=true;previous=changed;}lifetime.Cancel();previous.TrySetResult(true);}
}

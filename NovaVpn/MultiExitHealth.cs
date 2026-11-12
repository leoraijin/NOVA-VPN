using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaVpn;

public enum ExitProbePhase { Queued, Verified, Failed, Unavailable, Unsupported }
public sealed class ExitProbeTarget
{
    public string ProfileId {get;}
    public string Name {get;}
    public string ProfileFingerprint {get;}
    public ExitProbePhase Phase {get;}
    public ExitProbeTarget(string id,string name,string fingerprint,ExitProbePhase phase) {
        ProfileId=id;Name=name;ProfileFingerprint=fingerprint;Phase=phase;
    }
    public static string Fingerprint(VpnProfile profile) {
        if(profile==null)return null;
        using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(SubscriptionManager.ConfigKey(profile)))).Replace("-","").ToLowerInvariant();
    }
}
public sealed class ExitProbeResult
{
    public ExitProbeTarget Exit {get;}
    public ExitProbePhase Phase {get;}
    public int LatencyMs {get;}
    public DateTime CheckedUtc {get;}
    public string Target {get;}
    public ExitProbeResult(ExitProbeTarget exit,ExitProbePhase phase,int latency,DateTime now,string target=null) {
        Exit=exit;Phase=phase;LatencyMs=latency;CheckedUtc=now;Target=target;
    }
    public bool HasRecentSuccess(DateTime now) => Phase==ExitProbePhase.Verified&&LatencyMs>0&&CheckedUtc!=default(DateTime)&&
        now>=CheckedUtc&&now-CheckedUtc<TimeSpan.FromMinutes(3);
    internal bool UsesIpTarget => Uri.TryCreate(Target, UriKind.Absolute, out Uri uri) &&
        System.Net.IPAddress.TryParse(uri.Host, out _);
    public string Label(DateTime now) => Phase==ExitProbePhase.Verified?(HasRecentSuccess(now)?(UsesIpTarget ? "HTTPS по IP · DNS не подтверждён · " : "HTTPS подтверждён · ")+LatencyMs+" мс":"Нужна свежая проверка"):
        Phase==ExitProbePhase.Unavailable?"Конфиг недоступен":Phase==ExitProbePhase.Unsupported?"Проверка не поддерживается":
        Phase==ExitProbePhase.Queued?"Проверяется…":"HTTPS не подтверждён";
    public bool IsFreshFor(VpnProfile profile,DateTime now) {
        return HasRecentSuccess(now)&&profile!=null&&!profile.RetiredFromSubscription&&profile.Id==Exit.ProfileId&&
            Exit.ProfileFingerprint==ExitProbeTarget.Fingerprint(profile)&&
            Uri.TryCreate(Target,UriKind.Absolute,out Uri target)&&target.Scheme==Uri.UriSchemeHttps&&string.IsNullOrEmpty(target.UserInfo);
    }
}
public static class MultiExitHealthRunner
{
    public static async Task<ReadOnlyCollection<ExitProbeResult>> RunAsync(IReadOnlyList<ExitProbeTarget> exits,
        Func<string,Uri,CancellationToken,Task<int>> read,CancellationToken token)
    {
        if(exits==null||exits.Count==0||exits.Count>2000||exits.Select(e=>e.ProfileId).Distinct(StringComparer.Ordinal).Count()!=exits.Count)
            throw new ArgumentException("Некорректный план проверки выходов.");
        using(var slots=new SemaphoreSlim(4)) {
            var tasks=exits.Select(async exit=> {
                token.ThrowIfCancellationRequested();
                if(exit.Phase==ExitProbePhase.Unavailable||exit.Phase==ExitProbePhase.Unsupported)
                    return new ExitProbeResult(exit,exit.Phase,-1,DateTime.UtcNow);
                if(exit.Phase!=ExitProbePhase.Queued)throw new ArgumentException("Некорректное состояние плана проверки.");
                await slots.WaitAsync(token).ConfigureAwait(false);
                try {
                    foreach(string target in new[]{"https://cp.cloudflare.com/generate_204","https://1.1.1.1/cdn-cgi/trace"}) {
                        token.ThrowIfCancellationRequested();
                        try {
                            var watch=Stopwatch.StartNew();int status=await read(exit.ProfileId,new Uri(target),token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if(status==(target.StartsWith("https://1.1.1.1/",StringComparison.Ordinal)?200:204))return new ExitProbeResult(exit,ExitProbePhase.Verified,(int)Math.Max(1,watch.ElapsedMilliseconds),DateTime.UtcNow,target);
                        } catch(Exception error) when(error is IOException||error is SocketException||error is System.Security.Authentication.AuthenticationException||error is OperationCanceledException||error is ObjectDisposedException) {
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    return new ExitProbeResult(exit,ExitProbePhase.Failed,-1,DateTime.UtcNow);
                } finally {slots.Release();}
            }).ToArray();
            var results=await Task.WhenAll(tasks).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(results);
        }
    }
}

public sealed class MultiExitHealthSnapshot
{
    public IReadOnlyList<ExitProbeResult> Results {get;}
    public string NetworkKey {get;}
    public string CoreKey {get;}
    public MultiExitHealthSnapshot(IEnumerable<ExitProbeResult> results,string networkKey,string coreKey) {
        var array=results.ToArray();
        if(array.Length==0||array.Length>2000||array.Select(r=>r.Exit.ProfileId).Distinct(StringComparer.Ordinal).Count()!=array.Length)
            throw new ArgumentException("Некорректный результат проверки выходов.");
        Results=Array.AsReadOnly(array);NetworkKey=networkKey;CoreKey=coreKey;
    }
    public bool AllVerified => Results.All(r=>r.Phase==ExitProbePhase.Verified);
    public bool? RecoveryHealthy
    {
        get
        {
            if (AllVerified) return true;
            // Unavailable assignments have no outbound. An unsupported or unfinished probe
            // may still represent a working exit, so it prevents whole-tunnel recovery.
            return Results.Any(r => r.Phase == ExitProbePhase.Failed) &&
                Results.All(r => r.Phase == ExitProbePhase.Failed || r.Phase == ExitProbePhase.Unavailable)
                ? (bool?)false : null;
        }
    }
    public bool IsFreshFor(IEnumerable<VpnProfile> profiles,string networkKey,string coreKey,DateTime now) {
        if(string.IsNullOrEmpty(networkKey)||string.IsNullOrEmpty(coreKey)||NetworkKey!=networkKey||CoreKey!=coreKey)return false;
        var catalog=profiles.ToArray();
        return Results.All(r=>r.IsFreshFor(catalog.SingleOrDefault(p=>p.Id==r.Exit.ProfileId),now));
    }
    public string Summary => "HTTPS через назначенные серверы: "+Results.Count(r=>r.HasRecentSuccess(DateTime.UtcNow))+" из "+Results.Count+" подтверждены.";
}

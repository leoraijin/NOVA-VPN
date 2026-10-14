using System;
using System.Threading;
using System.Threading.Tasks;
using NovaVpn;

public static class StartupSelectorFixture
{
    public static Task<VpnProfile> Run(VpnProfile[] profiles, string preferred, bool lastWorks, bool allFail)
    {
        return StartupProfileSelector.SelectAsync(profiles, preferred, p => Task.FromResult(new ServerMapHealth {
            Success = !allFail && (p.Id != preferred || lastWorks), LatencyMs = p.Name == "Fast" ? 10 : 150,
            CheckedUtc = DateTime.UtcNow
        }), CancellationToken.None);
    }
    public static bool Cancellation(VpnProfile[] profiles)
    {
        var token = new CancellationTokenSource(); token.Cancel();
        try { RunCancelled(profiles, token.Token).GetAwaiter().GetResult(); return false; }
        catch (OperationCanceledException) { return true; }
    }
    private static Task<VpnProfile> RunCancelled(VpnProfile[] profiles, CancellationToken token)
    {
        return StartupProfileSelector.SelectAsync(profiles, profiles[0].Id, p => Task.FromResult(new ServerMapHealth()), token);
    }
}

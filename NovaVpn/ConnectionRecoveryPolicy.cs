using System;
using System.Diagnostics;

namespace NovaVpn;

// Completed-probe identity, freshness and restart cooldown share one monotonic clock.
public sealed class ConnectionRecoveryPolicy
{
    readonly Func<long> clock;
    readonly long freshnessTicks, cooldownTicks, settleTicks;
    long lastObservation = -1, lastClock = -1, lastRestart = -1;
    public int FailedProbes { get; private set; }
    public ConnectionRecoveryPolicy() : this(() => Stopwatch.IsHighResolution ? Stopwatch.GetTimestamp() : -1, Stopwatch.Frequency) { }
    public ConnectionRecoveryPolicy(Func<long> clock, long frequency)
    {
        if (clock == null) throw new ArgumentNullException(nameof(clock));
        if (frequency <= 0 || frequency > Int64.MaxValue / 120) throw new ArgumentOutOfRangeException(nameof(frequency));
        this.clock = clock; freshnessTicks = frequency * 120; cooldownTicks = frequency * 60; settleTicks = frequency * 3;
    }
    // Resume/network/manual resets do not waive an existing restart cooldown.
    public long Timestamp { get { return clock(); } }
    public bool NetworkSettled(long changedTimestamp)
    {
        if (changedTimestamp < 0) return true;
        long now = clock();
        return now >= changedTimestamp && now - changedTimestamp >= settleTicks;
    }
    public void Reset()
    {
        FailedProbes = 0;
        long cutoff = clock();
        if (cutoff >= 0) lastObservation = Math.Max(lastObservation, cutoff);
    }

    public bool Observe(long completedTimestamp, bool healthy, bool connectionRequested, bool automatic,
        bool operationBusy, bool networkAvailable)
    {
        if (!connectionRequested || !automatic) { Reset(); return false; }
        long now = clock();
        if (now < 0 || lastClock >= 0 && now < lastClock) {
            Reset(); lastClock = now; return false;
        }
        lastClock = now;
        if (operationBusy || !networkAvailable) { Reset(); return false; }
        if (completedTimestamp < 0 || completedTimestamp <= lastObservation ||
            completedTimestamp > now || now - completedTimestamp > freshnessTicks) return false;
        if (lastObservation >= 0 && completedTimestamp - lastObservation > freshnessTicks) FailedProbes = 0;
        lastObservation = completedTimestamp;
        if (healthy) { FailedProbes = 0; return false; }
        FailedProbes = Math.Min(2, FailedProbes + 1);
        if (FailedProbes < 2 || lastRestart >= 0 && (now < lastRestart || now - lastRestart < cooldownTicks)) return false;
        lastRestart = now; FailedProbes = 0;
        return true;
    }
}

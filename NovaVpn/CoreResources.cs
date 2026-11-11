using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public enum CorePowerMode : byte { Automatic, Eco, ExplicitNormal, Unavailable }

public sealed class CoreResourceSnapshot
{
    public int HostId { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public long Timestamp { get; set; }
    public long Frequency { get; set; }
    public int LogicalProcessors { get; set; }
    public long CpuTicks { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public CorePowerMode PowerMode { get; set; }
    public int PowerError { get; set; }
    public void Validate()
    {
        if (HostId < 1 || ProcessId < 1 || HostId == ProcessId || ProcessStartUtcTicks <= 0 || ProcessStartUtcTicks > DateTime.MaxValue.Ticks ||
            Timestamp < 0 || Frequency <= 0 || LogicalProcessors < 1 || LogicalProcessors > 65536 || CpuTicks < 0 || WorkingSetBytes < 0 || PrivateBytes < 0 ||
            (byte)PowerMode > 3 || PowerError < 0 || PowerMode == CorePowerMode.Unavailable && PowerError == 0 || PowerMode != CorePowerMode.Unavailable && PowerError != 0)
            throw new InvalidDataException("Invalid owned-core resource snapshot.");
    }
    public byte[] Encode()
    {
        Validate(); using (var data = new MemoryStream()) using (var writer = new BinaryWriter(data))
        {
            writer.Write((byte)1); writer.Write(HostId); writer.Write(ProcessId); writer.Write(ProcessStartUtcTicks); writer.Write(Timestamp); writer.Write(Frequency); writer.Write(LogicalProcessors);
            writer.Write(CpuTicks); writer.Write(WorkingSetBytes); writer.Write(PrivateBytes); writer.Write((byte)PowerMode); writer.Write(PowerError); return data.ToArray();
        }
    }
    public static CoreResourceSnapshot Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 66 || bytes[0] != 1) throw new InvalidDataException("Invalid owned-core resource frame.");
        using (var reader = new BinaryReader(new MemoryStream(bytes)))
        {
            reader.ReadByte(); var result = new CoreResourceSnapshot { HostId = reader.ReadInt32(), ProcessId = reader.ReadInt32(), ProcessStartUtcTicks = reader.ReadInt64(), Timestamp = reader.ReadInt64(), Frequency = reader.ReadInt64(),
                LogicalProcessors = reader.ReadInt32(), CpuTicks = reader.ReadInt64(), WorkingSetBytes = reader.ReadInt64(), PrivateBytes = reader.ReadInt64(), PowerMode = (CorePowerMode)reader.ReadByte(), PowerError = reader.ReadInt32() };
            result.Validate(); return result;
        }
    }
}

public sealed class CoreResourceSampler
{
    private CoreResourceSnapshot previous;
    public double? Observe(CoreResourceSnapshot current)
    {
        current.Validate(); var before = previous;
        // Own a value copy: a caller cannot alter a prior sample retrospectively.
        previous = CoreResourceSnapshot.Decode(current.Encode());
        if (before == null || before.HostId != current.HostId || before.ProcessId != current.ProcessId || before.ProcessStartUtcTicks != current.ProcessStartUtcTicks || before.Frequency != current.Frequency ||
            before.LogicalProcessors != current.LogicalProcessors || current.Timestamp <= before.Timestamp || current.CpuTicks < before.CpuTicks) return null;
        double seconds = (current.Timestamp - before.Timestamp) / (double)current.Frequency;
        if (seconds < 0.25 || seconds > 10) return null;
        double cpu = 100.0 * ((current.CpuTicks - before.CpuTicks) / (double)TimeSpan.TicksPerSecond) / seconds / current.LogicalProcessors;
        // Sampling is not atomic across the two Windows counters; allow small
        // boundary jitter, but do not invent a plausible number for bad data.
        return cpu < 0 || cpu > 101 ? (double?)null : Math.Min(100, cpu);
    }
    public void Reset() { previous = null; }
}

internal static class CoreResourceNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct PowerState { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(IntPtr process, int kind, ref PowerState value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int kind, ref PowerState value, uint size);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
    internal static bool ReadPower(Process child, out PowerState state, out int error)
    {
        state = new PowerState { Version = 1 }; error = 0;
        try { if (!GetProcessInformation(child.Handle, 4, ref state, 12)) { error = Math.Max(1, Marshal.GetLastWin32Error()); return false; } return state.Version == 1 || Fail(out error); }
        catch (EntryPointNotFoundException) { error = 120; return false; }
    }
    private static bool Fail(out int error) { error = 87; return false; }
    internal static bool SetPower(Process child, bool eco, PowerState baseline, out int error)
    {
        if (!ReadPower(child, out var before, out error)) return false;
        var wanted = before; wanted.Version = 1;
        wanted.ControlMask = (before.ControlMask & ~1u) | (eco ? 1u : baseline.ControlMask & 1u);
        wanted.StateMask = (before.StateMask & ~1u) | (eco ? 1u : baseline.StateMask & 1u);
        if (!SetProcessInformation(child.Handle, 4, ref wanted, 12)) { error = Math.Max(1, Marshal.GetLastWin32Error()); return false; }
        if (ReadPower(child, out var actual, out error) && (actual.ControlMask & 1) == (wanted.ControlMask & 1) && ((wanted.ControlMask & 1) == 0 || (actual.StateMask & 1) == (wanted.StateMask & 1))) return true;
        int failure = error == 0 ? 13 : error;
        SetProcessInformation(child.Handle, 4, ref before, 12); error = failure; return false;
    }
    internal static CoreResourceSnapshot Read(Process child)
    {
        child.Refresh(); if (child.HasExited || !OwnedCoreProcess.Contains(child)) throw new InvalidOperationException("Owned core exited.");
        long start = child.StartTime.ToUniversalTime().Ticks, cpu = child.TotalProcessorTime.Ticks, working = child.WorkingSet64, memory = child.PrivateMemorySize64;
        bool power = ReadPower(child, out var state, out int error); uint processors = GetActiveProcessorCount(ushort.MaxValue);
        return new CoreResourceSnapshot { HostId = Process.GetCurrentProcess().Id, ProcessId = child.Id, ProcessStartUtcTicks = start, CpuTicks = cpu, WorkingSetBytes = working, PrivateBytes = memory,
            Timestamp = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency, LogicalProcessors = checked((int)processors), PowerMode = !power ? CorePowerMode.Unavailable : (state.ControlMask & 1) == 0 ? CorePowerMode.Automatic : (state.StateMask & 1) != 0 ? CorePowerMode.Eco : CorePowerMode.ExplicitNormal, PowerError = error };
    }
}

internal sealed class CoreResourceServer : IDisposable
{
    private readonly GuardControlPipe pipe;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Task loop;
    private readonly Process child;
    private readonly CoreResourceNative.PowerState baseline;
    private readonly bool baselineKnown;
    internal CoreResourceServer(OwnedCoreSpec spec, Process child)
    {
        this.child = child; baselineKnown = CoreResourceNative.ReadPower(child, out baseline, out _);
        if (spec.EcoMode && baselineKnown) CoreResourceNative.SetPower(child, true, baseline, out _);
        if (!CoreResourceClient.ValidName(spec.ControlPipe)) throw new InvalidDataException("Invalid owned-core control name.");
        string user; using (var identity = WindowsIdentity.GetCurrent()) user = identity.User.Value;
        pipe = new GuardControlPipe(spec.ControlPipe, user, user, 2000, (request, peer) => {
            if (peer.ProcessId != (uint)spec.ParentId) return new GuardControlFrame((byte)GuardControlResult.Denied, request.RequestId, new byte[0]);
            if (!request.Body.SequenceEqual(new byte[] { 0x4e, 0x52, 1 }) || request.Kind < 1 || request.Kind > 3) return new GuardControlFrame((byte)GuardControlResult.Invalid, request.RequestId, new byte[0]);
            if (child.HasExited || !OwnedCoreProcess.Contains(child)) return new GuardControlFrame((byte)GuardControlResult.NotReady, request.RequestId, new byte[0]);
            if (request.Kind != 1)
            {
                int error = 120;
                if (!baselineKnown || !CoreResourceNative.SetPower(child, request.Kind == 2, baseline, out error)) return new GuardControlFrame((byte)GuardControlResult.Failed, request.RequestId, BitConverter.GetBytes(error));
            }
            return new GuardControlFrame((byte)GuardControlResult.Success, request.RequestId, CoreResourceNative.Read(child).Encode());
        });
        loop = pipe.RunAsync(stop.Token);
    }
    public void Dispose()
    {
        stop.Cancel(); try { loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } finally { pipe.Dispose(); stop.Dispose(); }
    }
}

public static class CoreResourceClient
{
    internal const string Prefix = "NovaVPN.Core.Resources.";
    internal static bool ValidName(string name) => name != null && name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(Prefix.Length), "N", out _);
    public static Task<CoreResourceSnapshot> ReadAsync(Process host, CancellationToken token) => ExchangeAsync(host, 1, token);
    public static Task<CoreResourceSnapshot> SetEcoAsync(Process host, bool eco, CancellationToken token) => ExchangeAsync(host, eco ? (byte)2 : (byte)3, token);
    private static async Task<CoreResourceSnapshot> ExchangeAsync(Process host, byte command, CancellationToken token)
    {
        if (host == null || host.HasExited) throw new InvalidOperationException("Ядро не запущено.");
        // Only accept the spec returned by Wrap for this parent. No caller-supplied
        // process ID, command-line lookup or external job handle is used.
        var spec = new JavaScriptSerializer().Deserialize<OwnedCoreSpec>(Encoding.UTF8.GetString(Convert.FromBase64String(host.StartInfo.Arguments)));
        using (var caller = Process.GetCurrentProcess())
            if (spec == null || spec.ParentId != caller.Id || spec.ParentStartTicks != caller.StartTime.ToUniversalTime().Ticks || !ValidName(spec.ControlPipe)) throw new UnauthorizedAccessException("Not an owned controlled core.");
        int id = host.Id; long birth = host.StartTime.ToUniversalTime().Ticks; _ = host.Handle;
        string image = Path.Combine(Path.GetDirectoryName(typeof(OwnedCoreProcess).Assembly.Location), "NOVA Core Host.exe");
        var response = await GuardControlPipe.ExchangeAsync(spec.ControlPipe, new GuardControlFrame(command, Guid.NewGuid(), new byte[] { 0x4e, 0x52, 1 }), pipe => {
            string user; using (var identity = WindowsIdentity.GetCurrent()) user = identity.User.Value;
            if (host.HasExited || host.StartTime.ToUniversalTime().Ticks != birth || !GuardControlPipe.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != (uint)id ||
                !pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)).Equals(new SecurityIdentifier(user)) || !MatchesImage(host, image)) throw new UnauthorizedAccessException("Untrusted core control server.");
        }, 2500, token).ConfigureAwait(false);
        if (response.Kind != (byte)GuardControlResult.Success)
        {
            if (response.Kind == (byte)GuardControlResult.Failed && response.Body.Length == 4) throw new Win32Exception(BitConverter.ToInt32(response.Body, 0));
            throw new InvalidOperationException("Режим или измерение ядра не подтверждены.");
        }
        var snapshot = CoreResourceSnapshot.Decode(response.Body);
        if (snapshot.HostId != id || host.HasExited || host.StartTime.ToUniversalTime().Ticks != birth) throw new InvalidDataException("Core lifecycle changed.");
        if (command == 2 && snapshot.PowerMode != CorePowerMode.Eco || command == 3 && snapshot.PowerMode == CorePowerMode.Unavailable) throw new InvalidOperationException("Режим EcoQoS не подтверждён Windows.");
        return snapshot;
    }
    private static bool MatchesImage(Process host, string expected)
    {
        var path = new StringBuilder(32768); uint count = (uint)path.Capacity;
        return QueryFullProcessImageName(host.Handle, 0, path, ref count) && string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Short-lived, metadata-only UDP observation. Own ETW session, no packet capture,
// no changes to network settings, no attachment to another tracing session.
public sealed class GameUdpDiscovery : IDisposable
{
    private static readonly Guid UdpProvider = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");
    private readonly HashSet<string> paths;
    private readonly Dictionary<string, GameEndpoint> endpoints = new Dictionary<string, GameEndpoint>();
    private readonly string name, marker;
    private readonly EventCallback callback;
    private IntPtr properties, loggerName;
    private ulong session, consumer = ulong.MaxValue;
    private Task reader;
    private Timer deadline;
    private int disposed;
    public string Error { get; private set; }
    public bool Active => session != 0 && Volatile.Read(ref disposed) == 0;
    public GameUdpDiscovery(InstalledGame game, string runtime)
    {
        paths = new HashSet<string>(game.Executables, StringComparer.OrdinalIgnoreCase);
        callback = OnEvent;
        name = "NOVA-GameUdp-" + Guid.NewGuid().ToString("N");
        marker = Path.Combine(runtime, "udp-observer-" + name.Substring(13) + ".json");
        try {
            EnableSystemProfilePrivilege();
            Directory.CreateDirectory(runtime);
            using (var owner = Process.GetCurrentProcess()) {
                string json = new JavaScriptSerializer().Serialize(new { name, pid = owner.Id, started = owner.StartTime.ToUniversalTime().Ticks });
                using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
            }
            properties = MakeProperties(name);
            uint status = StartTrace(out session, name, properties);
            if (status != 0) { session = 0; throw new System.ComponentModel.Win32Exception((int)status); }
            loggerName = Marshal.StringToHGlobalUni(name);
            var source = new TraceLogfile { LoggerName = loggerName, ProcessTraceMode = 0x100 | 0x10000000,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback) };
            consumer = OpenTrace(ref source);
            if (consumer == ulong.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ulong handle = consumer;
            reader = Task.Run(() => { ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); GC.KeepAlive(callback); });
            deadline = new Timer(_ => Dispose(), null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
        } catch (Exception ex) {
            Error = ex.GetType().Name + (ex is System.ComponentModel.Win32Exception native ? ":" + native.NativeErrorCode : ""); Dispose();
        }
    }
    public List<GameEndpoint> Snapshot() {
        lock (endpoints) return endpoints.Values.OrderByDescending(e => e.Bytes).Take(12)
            .Select(e => new GameEndpoint { Address = e.Address, Port = e.Port, Network = e.Network, Bytes = e.Bytes }).ToList();
    }
    private void OnEvent(ref EventRecord record)
    {
        // Header.ProcessId can belong to a kernel worker. Attribution uses payload PID.
        if (Volatile.Read(ref disposed) != 0 || record.Header.ProviderId != UdpProvider || record.Header.Descriptor.Version != 2) return;
        byte opcode = record.Header.Descriptor.Opcode;
        if (opcode != 10 && opcode != 26 || record.UserDataLength < (opcode == 10 ? 20 : 44)) return;
        try {
            int pid = Marshal.ReadInt32(record.UserData);
            if (!paths.Contains(GameProcessIdentity.ImagePath(pid))) return;
            int length = opcode == 10 ? 4 : 16;
            var address = new byte[length]; Marshal.Copy(IntPtr.Add(record.UserData, 8), address, 0, length);
            int offset = 8 + length * 2;
            int port = (Marshal.ReadByte(record.UserData, offset) << 8) | Marshal.ReadByte(record.UserData, offset + 1);
            var ip = new IPAddress(address);
            if (port == 0 || port == 53 || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal) return;
            string key = ip + ":" + port;
            lock (endpoints) {
                if (!endpoints.TryGetValue(key, out var endpoint)) {
                    if (endpoints.Count >= 128) return;
                    endpoints[key] = endpoint = new GameEndpoint { Address = ip.ToString(), Port = port, Network = "udp" };
                }
                endpoint.Bytes = Math.Min(long.MaxValue - uint.MaxValue, endpoint.Bytes) + unchecked((uint)Marshal.ReadInt32(record.UserData, 4));
            }
        } catch (Exception) { /* No managed exception may cross an ETW callback boundary. */ }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        deadline?.Dispose();
        uint stopped = session == 0 ? 0 : ControlTrace(session, name, properties, 1);
        session = 0;
        if (consumer != ulong.MaxValue) { CloseTrace(consumer); consumer = ulong.MaxValue; }
        if (stopped == 0 || stopped == 4201) { try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        IntPtr props = properties, logger = loggerName; properties = loggerName = IntPtr.Zero;
        Action release = () => { if (props != IntPtr.Zero) Marshal.FreeHGlobal(props); if (logger != IntPtr.Zero) Marshal.FreeHGlobal(logger); GC.KeepAlive(callback); };
        if (reader == null || reader.IsCompleted) release(); else reader.ContinueWith(_ => release(), TaskScheduler.Default);
    }
    public static void Recover(string runtime)
    {
        if (!Directory.Exists(runtime) || (File.GetAttributes(runtime) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string marker in Directory.EnumerateFiles(runtime, "udp-observer-*.json").Take(64)) try {
            if (new FileInfo(marker).Length > 4096) continue;
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(marker));
            if (data == null || !data.ContainsKey("name") || !data.ContainsKey("pid") || !data.ContainsKey("started")) continue;
            string name = data["name"] as string;
            if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^NOVA-GameUdp-[a-f0-9]{32}$")) continue;
            if (!Path.GetFileName(marker).Equals("udp-observer-" + name.Substring(13) + ".json", StringComparison.OrdinalIgnoreCase)) continue;
            int pid = Convert.ToInt32(data["pid"]); long started = Convert.ToInt64(data["started"]);
            if (pid <= 0 || started <= 0) continue;
            bool alive = false;
            try { using (var owner = Process.GetProcessById(pid)) alive = !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == started; }
            catch (ArgumentException) { } catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { continue; } // Unknown owner is never stopped.
            if (alive) continue;
            IntPtr properties = MakeProperties(name);
            uint status;
            try { status = ControlTrace(0, name, properties, 1); } finally { Marshal.FreeHGlobal(properties); }
            if (status == 0 || status == 4201) File.Delete(marker);
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (FormatException) { } catch (OverflowException) { } catch (KeyNotFoundException) { }
    }
    private static IntPtr MakeProperties(string name)
    {
        int offset = Marshal.SizeOf(typeof(TraceProperties)); int size = offset + (name.Length + 1) * 2;
        IntPtr memory = Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size], 0, memory, size);
        var value = new TraceProperties { Header = new Wnode { BufferSize = (uint)size, Flags = 0x20000, ClientContext = 1,
            Guid = Guid.ParseExact(name.Substring(13), "N") }, BufferSize = 64, MinimumBuffers = 2, MaximumBuffers = 8,
            LogFileMode = 0x100 | 0x02000000 | 0x00400000, FlushTimer = 1, EnableFlags = 0x10010000, LoggerNameOffset = (uint)offset };
        Marshal.StructureToPtr(value, memory, false); byte[] text = Encoding.Unicode.GetBytes(name + "\0"); Marshal.Copy(text, 0, IntPtr.Add(memory, offset), text.Length);
        return memory;
    }
    private static void EnableSystemProfilePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out IntPtr token)) return;
        try {
            if (!LookupPrivilegeValue(null, "SeSystemProfilePrivilege", out var luid)) return;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        } finally { CloseHandle(token); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);
    [StructLayout(LayoutKind.Sequential)] private struct Wnode { public uint BufferSize, ProviderId; public ulong HistoricalContext; public long TimeStamp; public Guid Guid; public uint ClientContext, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct TraceProperties { public Wnode Header; public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags, AgeLimit, NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost; public IntPtr LoggerThreadId; public uint LogFileNameOffset, LoggerNameOffset; }
    [StructLayout(LayoutKind.Sequential)] private struct Descriptor { public ushort Id; public byte Version, Channel, Level, Opcode; public ushort Task; public ulong Keyword; }
    [StructLayout(LayoutKind.Sequential)] private struct EventHeader { public ushort Size, HeaderType, Flags, EventProperty; public uint ThreadId, ProcessId; public long TimeStamp; public Guid ProviderId; public Descriptor Descriptor; public ulong ProcessorTime; public Guid ActivityId; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferContext { public byte ProcessorNumber, Alignment; public ushort LoggerId; }
    [StructLayout(LayoutKind.Sequential)] private struct EventRecord { public EventHeader Header; public BufferContext Buffer; public ushort ExtendedDataCount, UserDataLength; public IntPtr ExtendedData, UserData, UserContext; }
    [StructLayout(LayoutKind.Sequential)] private struct LegacyHeader { public ushort Size, FieldTypeFlags; public uint Version, ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public ulong ProcessorTime; }
    [StructLayout(LayoutKind.Sequential)] private struct LegacyEvent { public LegacyHeader Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TimeZone { public int Bias; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName; public SystemTime StandardDate; public int StandardBias; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName; public SystemTime DaylightDate; public int DaylightBias; }
    [StructLayout(LayoutKind.Sequential)] private struct LogHeader { public uint BufferSize, Version, ProviderVersion, NumberOfProcessors; public long EndTime; public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten; public Guid LogInstanceGuid; public IntPtr LoggerName, LogFileName; public TimeZone TimeZone; public long BootTime, PerfFreq, StartTime; public uint ReservedFlags, BuffersLost; }
    [StructLayout(LayoutKind.Sequential)] private struct TraceLogfile { public IntPtr LogFileName, LoggerName; public long CurrentTime; public uint BuffersRead, ProcessTraceMode; public LegacyEvent CurrentEvent; public LogHeader LogfileHeader; public IntPtr BufferCallback; public uint BufferSize, Filled, EventsLost; public IntPtr EventRecordCallback; public uint IsKernelTrace; public IntPtr Context; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EventCallback(ref EventRecord record);
    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)] private static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)] private static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint control);
    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)] private static extern ulong OpenTrace(ref TraceLogfile source);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
}

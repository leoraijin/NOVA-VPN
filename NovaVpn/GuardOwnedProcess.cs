using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NovaVpn;

internal sealed class GuardProcessIdentity
{
    internal uint ProcessId { get; }
    internal long CreationFileTime { get; }
    internal string Image { get; }
    internal string UserSid { get; }
    internal bool Resumed { get; }
    internal GuardProcessIdentity(uint id, long creation, string image, string sid, bool resumed)
    { ProcessId = id; CreationFileTime = creation; Image = image; UserSid = sid; Resumed = resumed; }
}

// A process authority is a retained kernel handle, never a caller-supplied PID.
// This unprivileged primitive runs with its caller's primary token. The service
// core wrapper separately requires the actual registered SYSTEM service.
internal sealed class GuardOwnedProcess : IDisposable
{
    internal const uint StopExitCode = 0xE04F574E;
    readonly object sync = new object();
    Handle process, thread, job, input;
    readonly OutputBuffer outputBuffer = new OutputBuffer();
    bool resumed, disposed;
    internal Task<uint> Completion { get; private set; }
    internal Task OutputCompletion { get; private set; }
    internal string OutputTail => outputBuffer.Read();
    GuardOwnedProcess() { }
    internal static GuardOwnedProcess CreateSuspended(string image, string arguments, string directory)
        => CreateSuspendedCore(image, arguments, directory, IntPtr.Zero);
    internal static GuardOwnedProcess CreateSuspendedWithLease(string image, string arguments, string directory, MachineTunnelLease lease)
    {
        if (lease == null) throw new ArgumentNullException(nameof(lease));
        return lease.StartOwnedChild(handle => CreateSuspendedCore(image, arguments, directory, handle));
    }
    private static GuardOwnedProcess CreateSuspendedCore(string image, string arguments, string directory, IntPtr tunnelLease)
    {
        if (string.IsNullOrWhiteSpace(image) || !System.IO.Path.IsPathRooted(image) || image.IndexOf('"') >= 0 || image.IndexOf('\0') >= 0 || arguments == null || arguments.IndexOf('\0') >= 0) throw new ArgumentException("Invalid owned process command.");
        var owned = new GuardOwnedProcess(); Handle read = null, output = null, outputRead = null;
        IntPtr attributes = IntPtr.Zero, inherited = IntPtr.Zero, jobs = IntPtr.Zero;
        bool initialized = false;
        try
        {
            owned.job = CreateJobObject(IntPtr.Zero, null); DemandHandle(owned.job);
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE; no breakaway.
            Demand(SetInformationJobObject(owned.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            Demand(CreatePipe(out IntPtr r, out IntPtr w, ref security, 0));
            read = new Handle(r); owned.input = new Handle(w);
            Demand(SetHandleInformation(owned.input, 1, 0)); // The service's writer is never inherited.
            Demand(CreatePipe(out IntPtr logRead, out IntPtr logWrite, ref security, 0));
            outputRead = new Handle(logRead); output = new Handle(logWrite);
            Demand(SetHandleInformation(outputRead, 1, 0)); // Parent reader never enters a child.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size.ToInt64() <= 0 || size.ToInt64() > 65536) throw new InvalidOperationException("Invalid process attribute size.");
            attributes = Marshal.AllocHGlobal(size); Demand(InitializeProcThreadAttributeList(attributes, 2, 0, ref size)); initialized = true;
            int inheritedCount = tunnelLease == IntPtr.Zero ? 2 : 3;
            inherited = Marshal.AllocHGlobal(inheritedCount * IntPtr.Size);
            Marshal.WriteIntPtr(inherited, read.DangerousGetHandle()); Marshal.WriteIntPtr(inherited, IntPtr.Size, output.DangerousGetHandle());
            if (tunnelLease != IntPtr.Zero) Marshal.WriteIntPtr(inherited, 2 * IntPtr.Size, tunnelLease);
            // Explicit HANDLE_LIST: stdin/output writer plus only the optional retained
            // tunnel mutex. No job/config/image/token/control handles leak.
            Demand(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, inherited, (IntPtr)(inheritedCount * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            // Windows10+: attach atomically in CreateProcess, so owner death
            // between creation and managed handle setup cannot orphan even a
            // suspended core. Unsupported platforms fail before creation.
            jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, owned.job.DangerousGetHandle());
            Demand(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000D, jobs, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupEx { Attributes = attributes };
            startup.Startup.Size = (uint)Marshal.SizeOf<StartupEx>(); startup.Startup.Flags = 0x100;
            startup.Startup.Input = read.DangerousGetHandle(); startup.Startup.Output = startup.Startup.Error = output.DangerousGetHandle();
            var command = new StringBuilder(AppUpdateService.QuoteWindowsArgument(image) + " " + arguments);
            if (command.Length >= 32767) throw new ArgumentException("Owned process command is too long.");
            Demand(CreateProcess(image, command, IntPtr.Zero, IntPtr.Zero, true, 4u | 0x80000u | 0x08000000u, IntPtr.Zero, directory, ref startup, out ProcessInfo info));
            owned.process = new Handle(info.Process); owned.thread = new Handle(info.Thread);
            Demand(IsProcessInJob(owned.process, owned.job, out bool contained));
            if (!contained) throw new InvalidOperationException("Creation did not bind the owned job.");
            // The reader must not retain owned/job/lease. Otherwise its blocked
            // ReadFile would prevent GC from closing an abandoned owner's job.
            Handle capturedReader = outputRead; OutputBuffer capturedBuffer = owned.outputBuffer;
            owned.OutputCompletion = Task.Factory.StartNew(() => DrainOutput(capturedReader, capturedBuffer), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            outputRead = null; // Reader task owns it through EOF, including launch failure cleanup.
            Handle observer; Demand(DuplicateHandle(GetCurrentProcess(), owned.process, GetCurrentProcess(), out observer, 0, false, 2));
            var weak = new WeakReference<GuardOwnedProcess>(owned);
            try
            {
                owned.Completion = Task.Factory.StartNew(() =>
                {
                    using (observer)
                    {
                        if (WaitForSingleObject(observer, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                        Demand(GetExitCodeProcess(observer, out uint code));
                        if (weak.TryGetTarget(out var target)) target.RootExited();
                        return code;
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch { observer.Dispose(); throw; }
            return owned;
        }
        catch
        {
            try
            {
                // Also handles failure during managed setup. This is the held
                // CreateProcess handle, not a PID looked up after the failure.
                if (owned.process != null && !owned.process.IsInvalid && WaitForSingleObject(owned.process, 0) == 258)
                { Demand(TerminateProcess(owned.process, StopExitCode)); WaitForSingleObject(owned.process, 10000); }
            }
            finally { owned.Dispose(); }
            throw;
        }
        finally
        {
            read?.Dispose(); output?.Dispose(); outputRead?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (inherited != IntPtr.Zero) Marshal.FreeHGlobal(inherited);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
        }
    }
    static void DrainOutput(Handle reader, OutputBuffer buffer)
    {
        using (reader)
        {
            var bytes = new byte[4096];
            while (ReadFile(reader, bytes, (uint)bytes.Length, out uint count, IntPtr.Zero))
            {
                if (count == 0) break;
                buffer.Append(bytes, checked((int)count));
            }
        }
    }
    sealed class OutputBuffer
    {
        const int Capacity = 8192;
        readonly byte[] bytes = new byte[Capacity];
        readonly object sync = new object();
        int count; bool truncated;
        internal void Append(byte[] source, int length)
        {
            lock (sync)
            {
                int retain = Math.Min(count, Capacity - length);
                if (retain != count) truncated = true;
                Buffer.BlockCopy(bytes, count - retain, bytes, 0, retain);
                Buffer.BlockCopy(source, 0, bytes, retain, length); count = retain + length;
            }
        }
        internal string Read()
        {
            lock (sync)
            {
                string text = Encoding.UTF8.GetString(bytes, 0, count);
                // A cut credential line has lost its redaction prefix. Never
                // expose that partial first line, even when it fills the tail.
                if (truncated) { int line = text.IndexOf('\n'); if (line < 0) return "[output truncated]"; text = text.Substring(line + 1); }
                text = LogSanitizer.Sanitize(text);
                if (text.Length > Capacity)
                {
                    int start = text.Length - Capacity;
                    if (char.IsLowSurrogate(text[start])) start++;
                    text = text.Substring(start);
                }
                return text;
            }
        }
    }
    internal GuardProcessIdentity Inspect()
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GuardOwnedProcess));
            if (WaitForSingleObject(process, 0) != 258) throw new InvalidOperationException("Owned core exited.");
            Demand(IsProcessInJob(process, job, out bool member)); if (!member) throw new InvalidOperationException("Owned core escaped its job.");
            var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
            Demand(QueryFullProcessImageName(process, 0, path, ref size));
            Demand(GetProcessTimes(process, out long created, out _, out _, out _));
            Demand(OpenProcessToken(process, 8, out Handle token)); string sid;
            using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) sid = identity.User.Value;
            uint pid = GetProcessId(process); if (pid == 0 || created <= 0 || WaitForSingleObject(process, 0) != 258) throw new InvalidOperationException("Owned core identity is unavailable.");
            return new GuardProcessIdentity(pid, created, path.ToString(), sid, resumed);
        }
    }
    internal void Resume()
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GuardOwnedProcess));
            if (resumed) throw new InvalidOperationException("Owned core already resumed.");
            Inspect(); uint previous = ResumeThread(thread);
            if (previous != 1) throw new InvalidOperationException("Unexpected primary thread suspension state: " + previous);
            resumed = true; thread.Dispose(); thread = null;
        }
    }
    void StopTree()
    {
        if (job == null || job.IsInvalid || job.IsClosed) return;
        Demand(TerminateJobObject(job, StopExitCode));
        var timer = Stopwatch.StartNew();
        while (true)
        {
            Demand(QueryInformationJobObject(job, 1, out Accounting state, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero));
            if (state.ActiveProcesses == 0) break;
            if (timer.ElapsedMilliseconds >= 10000) throw new TimeoutException("Owned process tree did not exit.");
            Thread.Sleep(20);
        }
    }
    void RootExited()
    {
        lock (sync)
        {
            if (disposed) return;
            try { StopTree(); } finally { job.Dispose(); }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            try { StopTree(); }
            finally { job?.Dispose(); input?.Dispose(); thread?.Dispose(); process?.Dispose(); }
        }
    }
    static void Demand(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static void DemandHandle(Handle handle) { if (handle == null || handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle() : base(true) { }
        internal Handle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] struct Startup
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; public uint LimitFlags { get => Flags; set => Flags = value; } }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] struct Accounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern Handle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(Handle job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool IsProcessInJob(Handle process, Handle job, out bool member);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(Handle job, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(Handle job, int kind, out Accounting accounting, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadFile(Handle file, byte[] buffer, uint length, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetHandleInformation(Handle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern Handle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string image, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(Handle thread);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(Handle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(Handle process, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(Handle process, out uint code);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(Handle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(Handle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint GetProcessId(Handle process);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(Handle process, uint access, out Handle token);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DuplicateHandle(IntPtr source, Handle handle, IntPtr target, out Handle duplicate, uint access, bool inherit, uint options);
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NovaVpn;

public sealed class OwnedCoreSpec
{
    public int ParentId;
    public long ParentStartTicks;
    public string FileName;
    public string Arguments;
    public string WorkingDirectory;
    public string CleanupFile;
}

// Dedicated host owns a Windows job, not the UI process: browsers/updaters/games never inherit it.
public static class OwnedCoreProcess
{
    private static IntPtr job; // Intentionally held until OS process teardown, non-inheritable.
    public static ProcessStartInfo Wrap(ProcessStartInfo original, string cleanupFile = null)
    {
        using (var parent = Process.GetCurrentProcess()) {
            var spec = new OwnedCoreSpec { ParentId = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
                FileName = Path.GetFullPath(original.FileName), Arguments = original.Arguments,
                WorkingDirectory = original.WorkingDirectory, CleanupFile = cleanupFile };
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(spec)));
            string host = Path.Combine(Path.GetDirectoryName(typeof(OwnedCoreProcess).Assembly.Location), "NOVA Core Host.exe");
            if (!File.Exists(host)) throw new FileNotFoundException("Не найден компонент владения ядром. Переустановите NOVA.", host);
            return new ProcessStartInfo(host, payload) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = original.RedirectStandardInput,
                RedirectStandardOutput = original.RedirectStandardOutput, RedirectStandardError = original.RedirectStandardError,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = original.WorkingDirectory
            };
        }
    }

    public static int RunHost(string payload)
    {
        Process parent = null, child = null;
        OwnedCoreSpec spec = null;
        int finished = 0;
        try {
            if (payload.Length > 32768) throw new ArgumentException("Core host payload too large");
            spec = new JavaScriptSerializer().Deserialize<OwnedCoreSpec>(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            parent = Process.GetProcessById(spec.ParentId);
            _ = parent.Handle; // Cache the process handle before validating time; PID reuse cannot retarget the watcher.
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != spec.ParentStartTicks) return 1;
            CreateOwnedJob(); // Host joins before starting a child, so inheritance is atomic.
            Process owner = parent;
            var watcher = new Thread(() => {
                try { owner.WaitForExit(); } catch { }
                if (Volatile.Read(ref finished) != 0) return;
                Cleanup(spec.CleanupFile);
                Environment.Exit(1); // Last job handle closes and all native descendants are killed.
            }) { IsBackground = true };
            watcher.Start();
            child = new Process { StartInfo = new ProcessStartInfo(spec.FileName, spec.Arguments) {
                WorkingDirectory = spec.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
            child.OutputDataReceived += (s, e) => { if (e.Data != null) Console.Out.WriteLine(e.Data); };
            child.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
            child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
            var active = child;
            _ = Task.Run(() => {
                try { Console.OpenStandardInput().CopyTo(active.StandardInput.BaseStream); } catch (IOException) { } catch (InvalidOperationException) { }
                finally { try { active.StandardInput.Close(); } catch { } }
            });
            child.WaitForExit(); return child.ExitCode;
        } catch (Exception ex) { Console.Error.WriteLine("Core ownership failed: " + ex.GetType().Name); return 1; }
        finally { Interlocked.Exchange(ref finished, 1); Cleanup(spec?.CleanupFile); child?.Dispose(); parent?.Dispose(); GC.KeepAlive(job); }
    }
    private static void Cleanup(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try {
            string runtime = Path.Combine(StateStore.DirectoryPath, "runtime");
            if (Directory.Exists(runtime) && (File.GetAttributes(runtime) & FileAttributes.ReparsePoint) != 0) return;
            if (GameDiscoveryService.IsInside(path, runtime) && Path.GetExtension(path) == ".json") File.Delete(path);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void CreateOwnedJob()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE; no breakaway.
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)) || !AssignProcessToJobObject(job, GetCurrentProcess()))
            throw new System.ComponentModel.Win32Exception();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
        public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr handle, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}

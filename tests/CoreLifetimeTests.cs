using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NovaVpn;

internal static class CoreLifetimeTests
{
    static string Q(string value) => AppUpdateService.QuoteWindowsArgument(value);
    static Process Start(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
    public static int Main(string[] args)
    {
        if (args[0] == "owner") {
            var original = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var host = Process.Start(OwnedCoreProcess.Wrap(original))) {
                string first = host.StandardOutput.ReadLine(), second = host.StandardOutput.ReadLine();
                File.WriteAllText(args[2], host.Id + "|" + first + "|" + second);
                Thread.Sleep(120000);
            }
            return 0;
        }
        Process owner = null, foreign = null; Process[] tracked = null;
        string marker = Path.Combine(args[1], "core-pids.txt");
        try {
            if (File.Exists(marker)) File.Delete(marker);
            foreign = Start(args[0], "grandchild");
            owner = Start(typeof(CoreLifetimeTests).Assembly.Location, "owner " + Q(args[0]) + " " + Q(marker));
            for (int i = 0; i < 100 && !File.Exists(marker) && !owner.HasExited; i++) Thread.Sleep(100);
            if (!File.Exists(marker)) throw new Exception("Owned host did not publish fixture process identities");
            string[] ids = File.ReadAllText(marker).Split('|');
            if (ids.Length != 3) throw new Exception("Invalid child fixture output");
            tracked = ids.Select(id => Process.GetProcessById(int.Parse(id))).ToArray();
            owner.Kill(); owner.WaitForExit(3000);
            foreach (var process in tracked) if (!process.WaitForExit(7000)) throw new Exception("Core/descendant survived owner crash: " + process.Id);
            if (foreign.HasExited) throw new Exception("Unrelated process was terminated");
            Console.WriteLine("PASS: abrupt owner termination killed its host, native core fixture and grandchild; unrelated same-image process survived. No real VPN started."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            foreach (var process in new[] { owner, foreign }.Concat(tracked ?? new Process[0])) if (process != null) {
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch { } finally { process.Dispose(); }
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Threading;
internal static class CoreTreeFixture
{
    public static void Main(string[] args)
    {
        if (args.Length == 0) {
            var child = Process.Start(new ProcessStartInfo(typeof(CoreTreeFixture).Assembly.Location, "grandchild") { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine(Process.GetCurrentProcess().Id); Console.WriteLine(child.Id);
        }
        Thread.Sleep(120000);
    }
}

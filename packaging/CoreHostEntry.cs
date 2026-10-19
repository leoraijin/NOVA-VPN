using System;
internal static class CoreHostEntry
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 1;
        return NovaVpn.OwnedCoreProcess.RunHost(args[0]);
    }
}

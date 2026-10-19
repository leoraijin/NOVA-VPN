using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace NovaVpn;

// Read-only fallback when no NOVA TUN is active. Never installs a capture driver or changes routes.
public static class GameSocketDiscovery
{
    public static List<GameEndpoint> Read(InstalledGame game)
    {
        var allowed = new HashSet<string>(game.Executables, StringComparer.OrdinalIgnoreCase);
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcesses()) using (process) {
            try { if (allowed.Contains(GameProcessIdentity.ImagePath(process.Id))) pids.Add(process.Id); }
            catch (InvalidOperationException) { }
        }
        var result = new List<GameEndpoint>();
        if (pids.Count == 0) return result;
        ReadFamily(2, pids, result);
        ReadFamily(23, pids, result);
        return result.GroupBy(e => e.Address + ":" + e.Port).Select(g => g.First()).Take(12).ToList();
    }
    private static void ReadFamily(int family, HashSet<int> pids, List<GameEndpoint> result)
    {
        int size = 0;
        int rowSize = family == 2 ? 24 : 56;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0) != 122 || size < 4 || size > 16 * 1024 * 1024) return;
        IntPtr table = Marshal.AllocHGlobal(size);
        try {
            if (GetExtendedTcpTable(table, ref size, false, family, 5, 0) != 0) return;
            int count = Marshal.ReadInt32(table);
            if (count < 0 || count > (size - 4) / rowSize) return;
            for (int index = 0; index < count; index++) {
                IntPtr row = IntPtr.Add(table, 4 + index * rowSize);
                if (Marshal.ReadInt32(row, family == 2 ? 0 : 48) != 5 || !pids.Contains(Marshal.ReadInt32(row, family == 2 ? 20 : 52))) continue;
                byte[] address = new byte[family == 2 ? 4 : 16];
                Marshal.Copy(IntPtr.Add(row, family == 2 ? 12 : 24), address, 0, address.Length);
                var ip = new IPAddress(address); byte[] port = BitConverter.GetBytes(Marshal.ReadInt32(row, family == 2 ? 16 : 44));
                int remotePort = (port[0] << 8) | port[1];
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || remotePort == 53 || remotePort == 0) continue;
                result.Add(new GameEndpoint { Address = ip.ToString(), Port = remotePort, Network = "tcp" });
            }
        } finally { Marshal.FreeHGlobal(table); }
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}

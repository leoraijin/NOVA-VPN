using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace NovaVpn;

// Read-only IP Helper route snapshot. No route/service/adapter mutations.
public static class WindowsRouteInventory
{
    const int RowSize = 104, RowOffset = 8, MaxRows = 65536;
    [DllImport("iphlpapi.dll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern void FreeMibTable(IntPtr table);
    static uint U32(byte[] data, int offset)
    {
        return (uint)data[offset] | (uint)data[offset+1]<<8 | (uint)data[offset+2]<<16 | (uint)data[offset+3]<<24;
    }
    // Byte-array seam keeps malformed-table tests outside native pointer access.
    public static TunnelNetworkPreflight.ConnectedPrefix[] Decode(byte[] data)
    {
        if (data == null || data.Length < RowOffset) throw new InvalidDataException("Неполная таблица маршрутов Windows.");
        uint count = U32(data, 0);
        if (count > MaxRows || data.Length != RowOffset + (long)count * RowSize) throw new InvalidDataException("Некорректный размер таблицы маршрутов Windows.");
        var result = new List<TunnelNetworkPreflight.ConnectedPrefix>();
        for (int i = 0; i < count; i++)
        {
            int row = RowOffset + i * RowSize, prefix = row + 12;
            int family = data[prefix] | data[prefix+1]<<8;
            int bits = data[prefix+28], size = family == 2 ? 4 : family == 23 ? 16 : 0;
            if (size == 0 || bits > size * 8) throw new InvalidDataException("Некорректный префикс маршрута Windows.");
            var address = new byte[size]; Array.Copy(data, prefix + (family == 2 ? 4 : 8), address, 0, size);
            if (bits == 0) {
                if (Array.Exists(address, b => b != 0)) throw new InvalidDataException("Некорректный маршрут по умолчанию.");
                continue; // A default gateway does not reserve every private subnet.
            }
            result.Add(new TunnelNetworkPreflight.ConnectedPrefix("маршрут интерфейса №" + U32(data, row+8), new IPAddress(address) + "/" + bits));
        }
        return result.ToArray();
    }
    static string AddressKey(byte[] data, int start, int bits = -1)
    {
        int family = data[start] | data[start+1]<<8;
        if (family == 0 && bits < 0) return "unspecified";
        int size = family == 2 ? 4 : family == 23 ? 16 : 0;
        if (size == 0) throw new InvalidDataException("Некорректное семейство адреса маршрута.");
        var bytes = new byte[size]; Array.Copy(data, start + (family == 2 ? 4 : 8), bytes, 0, size);
        if (bits >= 0)
            for (int i = 0; i < size; i++) {
                int remaining = bits - i * 8;
                bytes[i] &= (byte)(remaining <= 0 ? 0 : remaining >= 8 ? 255 : 255 << (8 - remaining));
            }
        return family + ":" + BitConverter.ToString(bytes) + (bits >= 0 ? "/" + bits : family == 23 ? "%" + U32(data,start+24) : "");
    }
    public static string Fingerprint(byte[] data)
    {
        Decode(data); // Validate the whole native record set before trusting any row.
        int count = (int)U32(data,0);
        var rows = new string[count];
        for (int i = 0; i < count; i++)
        {
            int row = RowOffset + i * RowSize;
            rows[i] = U32(data,row) + ":" + U32(data,row+4) + ":" + U32(data,row+8) + "|" +
                AddressKey(data,row+12,data[row+40]) + "|" + AddressKey(data,row+44) + "|" +
                data[row+72] + ":" + U32(data,row+84) + ":" + U32(data,row+88) + ":" +
                data[row+92] + ":" + data[row+93] + ":" + data[row+94] + ":" + data[row+95] + ":" + U32(data,row+100);
        }
        // Exclude padding, age and expiring lifetime counters. Entry removal still
        // changes the set; duplicates/order alone do not change routing context.
        string canonical = String.Join("\n",rows.Distinct(StringComparer.Ordinal).OrderBy(p=>p,StringComparer.Ordinal));
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
    public static string CaptureFingerprint() { return Fingerprint(ReadTable()); }
    public static TunnelNetworkPreflight.ConnectedPrefix[] Capture() { return Decode(ReadTable()); }
    static byte[] ReadTable()
    {
        IntPtr table;
        uint code = GetIpForwardTable2(0, out table);
        if (code != 0) throw new IOException("Не удалось прочитать маршруты Windows.", new System.ComponentModel.Win32Exception((int)code));
        if (table == IntPtr.Zero) throw new IOException("Windows вернула пустой указатель таблицы маршрутов.");
        try
        {
            uint count = unchecked((uint)Marshal.ReadInt32(table));
            if (count > MaxRows) throw new IOException("Слишком много маршрутов Windows.");
            if (count == 0) return new byte[RowOffset];
            var data = new byte[checked(RowOffset + (int)count * RowSize)];
            Marshal.Copy(table, data, 0, data.Length);
            return data;
        }
        finally { FreeMibTable(table); }
    }
}

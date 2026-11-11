using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Read-only connected/route-prefix preflight. Not a NAT/DNS transaction.
public static class TunnelNetworkPreflight
{
    public const int HostRefusedExitCode = unchecked((int)0xE14E4E57);
    public const string HostRefusedMessage = "Сеть Windows изменилась или недоступна для проверки. Ядро VPN не запущено. Повторите подключение.";

    public sealed class ConnectedPrefix
    {
        internal readonly byte[] Bytes;
        internal readonly int Bits;
        public string Adapter { get; private set; }
        public string Prefix { get; private set; }
        public ConnectedPrefix(string adapter, string prefix)
        {
            IPAddress address; int bits;
            Parse(prefix, out address, out bits);
            Adapter = new string((adapter ?? "сетевой адаптер").Where(c => !Char.IsControl(c)).Take(120).ToArray());
            Prefix = prefix; Bytes = address.GetAddressBytes(); Bits = bits;
        }
    }
    static void Parse(string prefix, out IPAddress address, out int bits)
    {
        address = null; bits = -1;
        var parts = (prefix ?? "").Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out address) ||
            address.IsIPv4MappedToIPv6 || !Int32.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bits) ||
            bits < 0 || bits > address.GetAddressBytes().Length * 8)
            throw new InvalidDataException("Некорректная подсеть сетевого интерфейса.");
    }
    public static ConnectedPrefix[] Capture()
    {
        try
        {
            var result = new List<ConnectedPrefix>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork && address.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (result.Count >= 8192) throw new IOException("Слишком много адресов сетевых интерфейсов.");
                    result.Add(new ConnectedPrefix(adapter.Name, address.Address + "/" + address.PrefixLength));
                }
            result.AddRange(WindowsRouteInventory.Capture());
            if (result.Count > 8192) throw new IOException("Слишком много сетей и маршрутов Windows.");
            return result.ToArray();
        }
        catch (NetworkInformationException error) { throw new IOException("Не удалось проверить подсети Windows перед подключением VPN.", error); }
    }
    static IEnumerable<object> Values(object value)
    {
        if (value == null) return new object[0];
        if (value is string || value is Dictionary<string, object>) return new[] { value };
        var list = value as IEnumerable;
        if (list == null) throw new InvalidDataException("Некорректный список интерфейсов или адресов.");
        return list.Cast<object>();
    }
    static bool Overlaps(byte[] a, int aBits, byte[] b, int bBits)
    {
        if (a.Length != b.Length) return false;
        int bits = Math.Min(aBits, bBits);
        for (int i = 0; bits > 0; i++, bits -= 8)
        {
            int mask = 255 << Math.Max(0, 8 - bits) & 255;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }
    public sealed class ManagedConfiguration
    {
        readonly string[] addresses;
        public string Json { get; private set; }
        public string[] Addresses { get { return (string[])addresses.Clone(); } }
        internal ManagedConfiguration(string json, string[] addresses) { Json = json; this.addresses = (string[])addresses.Clone(); }
    }
    static IEnumerable<string> Candidates(bool ipv6)
    {
        yield return ipv6 ? "fdfe:dcba:9876::1/126" : "172.19.0.1/30";
        for (int i = 0; i < 256; i++)
            yield return ipv6 ? "fd00:" + (0x4e4f + i).ToString("x") + ":" + (0x5641 + i * 31).ToString("x") + "::1/126" :
                "10." + (i * 31 % 256) + "." + (i * 17 % 256) + ".1/30";
        if (!ipv6) {
            for (int i = 0; i < 256; i++) yield return "172." + (16 + i % 16) + "." + (i / 16 * 16) + ".1/30";
            for (int i = 0; i < 256; i++) yield return "192.168." + i + ".1/30";
        }
    }
    public static ManagedConfiguration PrepareManaged(string config, IEnumerable<ConnectedPrefix> connected, bool ipv6)
    {
        if (connected == null) throw new ArgumentNullException(nameof(connected));
        var snapshot = connected.Take(8193).ToArray();
        if (snapshot.Length > 8192 || snapshot.Any(p => p == null)) throw new InvalidDataException("Некорректный снимок подсетей.");
        var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 };
        var root = serializer.Deserialize<Dictionary<string, object>>(config);
        object inboundValue;
        if (root == null || !root.TryGetValue("inbounds", out inboundValue)) throw new InvalidDataException("Не найден управляемый TUN.");
        var tuns = Values(inboundValue).OfType<Dictionary<string, object>>().Where(p => p.ContainsKey("type") && Equals(p["type"], "tun")).ToArray();
        object addressValue;
        string[] original = ipv6 ? new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" } : new[] { "172.19.0.1/30" };
        if (tuns.Length != 1 || !tuns[0].ContainsKey("tag") || !Equals(tuns[0]["tag"], "tun-in") ||
            !tuns[0].TryGetValue("address", out addressValue) || !Values(addressValue).SequenceEqual(original.Cast<object>()))
            throw new InvalidDataException("Адреса автоматического TUN не соответствуют управляемой конфигурации.");
        var selected = new List<string>();
        foreach (bool family in ipv6 ? new[] { false, true } : new[] { false })
        {
            string chosen = Candidates(family).FirstOrDefault(candidate => {
                var prefix = new ConnectedPrefix("VPN", candidate);
                return !snapshot.Any(p => Overlaps(prefix.Bytes, prefix.Bits, p.Bytes, p.Bits));
            });
            if (chosen == null) throw new IOException("Не найдена свободная частная подсеть для VPN. Подключение остановлено до изменения маршрутов.");
            selected.Add(chosen);
        }
        tuns[0]["address"] = selected.ToArray();
        string json = serializer.Serialize(root);
        Validate(json, snapshot);
        return new ManagedConfiguration(json, selected.Select(p => p.Split('/')[0]).ToArray());
    }
    public static void Validate(string config, IEnumerable<ConnectedPrefix> connected)
    {
        if (connected == null) throw new ArgumentNullException(nameof(connected));
        var snapshot = connected.Take(8193).ToArray();
        if (snapshot.Length > 8192 || snapshot.Any(p => p == null)) throw new InvalidDataException("Некорректный снимок подсетей.");
        var root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Deserialize<Dictionary<string, object>>(config);
        object inbounds;
        if (root == null) throw new InvalidDataException("Пустая конфигурация.");
        // FakeIP is a routed synthetic address space, not a TUN interface address.
        // Validate it even for a proxy-only config before any native process starts.
        object dnsValue;
        if (root.TryGetValue("dns", out dnsValue) && dnsValue is Dictionary<string, object> dns) {
            object serverValue;
            if (dns.TryGetValue("servers", out serverValue)) {
                foreach (var server in Values(serverValue).OfType<Dictionary<string, object>>()) {
                    object type; if (!server.TryGetValue("type", out type) || !Equals(type, "fakeip")) continue;
                    foreach (string key in new[] { "inet4_range", "inet6_range" }) {
                        object value; if (!server.TryGetValue(key, out value)) continue;
                        if (!(value is string)) throw new InvalidDataException("Некорректный диапазон FakeIP.");
                        var proposed = new ConnectedPrefix("FakeIP", (string)value);
                        if (proposed.Bytes.Length != (key == "inet4_range" ? 4 : 16)) throw new InvalidDataException("Некорректное семейство диапазона FakeIP.");
                        var conflict = snapshot.FirstOrDefault(p => Overlaps(proposed.Bytes, proposed.Bits, p.Bytes, p.Bits));
                        if (conflict != null) throw new IOException("Диапазон FakeIP пересекается с подключённой сетью или маршрутом Windows. VPN не запущен. Измените конфликтующую сеть и повторите подключение.");
                    }
                }
            }
        }
        if (!root.TryGetValue("inbounds", out inbounds)) return;
        var tun = new List<ConnectedPrefix>();
        foreach (var value in Values(inbounds))
        {
            var inbound = value as Dictionary<string, object>; object type;
            if (inbound == null) throw new InvalidDataException("Некорректный входящий интерфейс.");
            if (!inbound.TryGetValue("type", out type) || !Equals(type, "tun")) continue;
            foreach (string key in new[] { "address", "inet4_address", "inet6_address" })
            {
                object addresses; if (!inbound.TryGetValue(key, out addresses)) continue;
                foreach (var item in Values(addresses))
                {
                    if (!(item is string) || tun.Count >= 64) throw new InvalidDataException("Некорректные адреса VPN-интерфейса.");
                    tun.Add(new ConnectedPrefix("VPN", (string)item));
                }
            }
        }
        foreach (var proposed in tun)
        {
            var conflict = snapshot.FirstOrDefault(p => Overlaps(proposed.Bytes, proposed.Bits, p.Bytes, p.Bits));
            if (conflict != null)
                throw new IOException("Подсеть VPN " + proposed.Prefix + " пересекается с сетью «" + conflict.Adapter + "» (" + conflict.Prefix + "). Подключение остановлено до изменения маршрутов. Измените подсеть виртуальной сети или конфигурации VPN.");
        }
    }
}

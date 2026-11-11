using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Immutable allowlisted snapshot, never an arbitrary outbound/config dictionary.
// Proxy mode is also used by real-profile health checks; attested TUN is separate.
internal sealed class GuardServiceConnection
{
    readonly VpnProfile snapshot;
    internal string Address => snapshot.Server;
    internal int Port => snapshot.Port;
    internal string Network => snapshot.Protocol == "hysteria2" || snapshot.Protocol == "tuic" ? "udp" : "tcp";
    private GuardServiceConnection(VpnProfile snapshot) { this.snapshot = snapshot; }
    static string Text(string value, int max, bool controls = false)
    {
        value = value ?? "";
        if (value.Length > max || value.IndexOf('\0') >= 0 || !controls && value.Any(char.IsControl)) throw new FormatException("Invalid bounded connection field.");
        return value;
    }
    static string Name(string value)
    {
        value = Text(value, 253);
        if (value.Length == 0) return value;
        if (IPAddress.TryParse(value, out var address)) { if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) throw new FormatException("Scoped TLS name."); return address.ToString(); }
        string ascii = new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant();
        if (ascii.Length == 0 || ascii.Length > 253 || ascii.Split('.').Any(label => label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-' || label.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')))) throw new FormatException("Invalid server name.");
        return ascii;
    }
    static string HeaderHost(string value)
    {
        value = Text(value, 512);
        if (value.Length == 0) return value;
        if (value.Any(c => c <= ' ' || c > '~' || "\\/@?#\"".IndexOf(c) >= 0) || !Uri.TryCreate("https://" + value, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new FormatException("Invalid transport host.");
        return value;
    }
    internal static GuardServiceConnection FromProfile(VpnProfile source, string address)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (!string.IsNullOrWhiteSpace(source.RawJson) || !string.IsNullOrWhiteSpace(source.EffectivePortableOutboundJson) || source.CorporateProxy?.Enabled == true || source.HasMissingCorporateBase) throw new NotSupportedException("Privileged connections require typed parameters, not saved JSON or proxy chains.");
        source = new VpnProfile { Protocol = source.Protocol, Server = source.Server, Port = source.Port, UserId = source.UserId, Password = source.Password, Method = source.Method, Transport = source.Transport, Security = source.Security, Flow = source.Flow, Sni = source.Sni, Host = source.Host, Path = source.Path, PublicKey = source.PublicKey, ShortId = source.ShortId, Fingerprint = source.Fingerprint, Obfs = source.Obfs, ObfsPassword = source.ObfsPassword };
        address = Text(address, 128);
        string protocol = Text(source.Protocol, 32).ToLowerInvariant();
        if (!new[] { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic" }.Contains(protocol)) throw new NotSupportedException("Unsupported typed service protocol.");
        if (!IPAddress.TryParse(address, out var ip)) throw new FormatException("A resolved numeric endpoint is required.");
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] bytes = ip.GetAddressBytes();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) || ip.IsIPv6Multicast || bytes.Length == 4 && bytes[0] >= 224 || bytes.Length == 16 && ip.ScopeId != 0 || source.Port < 1 || source.Port > 65535) throw new FormatException("Invalid exact endpoint.");
        string transport = Text(source.Transport, 32).ToLowerInvariant();
        bool datagram = protocol == "hysteria2" || protocol == "tuic";
        if (datagram)
        { if (!new[] { "", "udp", "quic" }.Contains(transport)) throw new NotSupportedException("Unsupported datagram transport."); transport = ""; }
        else if (!new[] { "", "tcp", "ws", "websocket", "grpc", "http", "h2", "httpupgrade" }.Contains(transport)) throw new NotSupportedException("Unsupported typed service transport.");
        if (protocol == "shadowsocks" && transport != "" && transport != "tcp") throw new NotSupportedException("Shadowsocks plugins require separate privileged validation.");
        string security = Text(source.Security, 32).ToLowerInvariant();
        if (!new[] { "", "none", "tls", "reality" }.Contains(security) || security == "reality" && protocol != "vless" || protocol == "shadowsocks" && security != "" && security != "none") throw new NotSupportedException("Unsupported typed TLS mode.");
        if (protocol == "trojan" && security != "tls") throw new NotSupportedException("Typed Trojan requires TLS.");
        string flow = Text(source.Flow, 64);
        if (flow.Length != 0 && (protocol != "vless" || flow != "xtls-rprx-vision" || security != "reality" && security != "tls")) throw new NotSupportedException("Unsupported typed flow.");
        if (flow.Length != 0 && transport != "" && transport != "tcp") throw new NotSupportedException("Vision requires a TCP transport.");
        string id = Text(source.UserId, 64);
        if (protocol == "vless" || protocol == "vmess" || protocol == "tuic") { if (!Guid.TryParse(id, out var uuid) || uuid == Guid.Empty) throw new FormatException("Invalid connection UUID."); id = uuid.ToString("D"); }
        string password = Text(source.Password, 4096, true);
        if (new[] { "trojan", "shadowsocks", "hysteria2", "tuic" }.Contains(protocol) && password.Length == 0) throw new FormatException("Connection credential required.");
        string method = Text(source.Method, 64);
        if (method.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) || protocol == "shadowsocks" && method.Length == 0) throw new FormatException("Invalid typed cipher.");
        string obfs = Text(source.Obfs, 32), obfsPassword = Text(source.ObfsPassword, 4096, true);
        if (obfs.Length != 0 && (protocol != "hysteria2" || obfs != "salamander" || obfsPassword.Length == 0)) throw new NotSupportedException("Unsupported typed obfuscation.");
        string originalName = Name(source.Server), sni = Name(source.Sni);
        if (sni.Length == 0) sni = originalName.Length == 0 ? ip.ToString() : originalName;
        string host = HeaderHost(source.Host), path = Text(source.Path, 2048);
        if (path.Length > 0 && transport != "grpc" && path[0] != '/') throw new FormatException("Transport path must be absolute.");
        if (host.Length == 0 && new[] { "ws", "websocket", "http", "h2", "httpupgrade" }.Contains(transport)) host = originalName.IndexOf(':') >= 0 ? "[" + originalName + "]" : originalName;
        string key = Text(source.PublicKey, 128), shortId = Text(source.ShortId, 16), fingerprint = Text(source.Fingerprint, 32);
        if (fingerprint.Any(c => !(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_'))) throw new FormatException("Invalid TLS fingerprint token.");
        if (security == "reality")
        {
            if (key.Length != 43 || key.Any(c => !(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_')) || Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + "=").Length != 32 || shortId.Length % 2 != 0 || shortId.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("Invalid Reality key parameters.");
        }
        var snapshot = new VpnProfile { Protocol = protocol, Server = ip.ToString(), Port = source.Port, UserId = id, Password = password, Method = method, Transport = transport, Security = security, Flow = flow, Sni = sni, Host = host, Path = path, PublicKey = key, ShortId = shortId, Fingerprint = fingerprint, Obfs = obfs, ObfsPassword = obfsPassword };
        return new GuardServiceConnection(snapshot);
    }
    internal string Render(int port, string user, string password)
    {
        if (port < 1 || port > 65535 || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) throw new ArgumentException("Private loopback credentials required.");
        return ConfigBuilder.BuildProbe(snapshot, port, user, password);
    }
    internal string RenderTunnel(int port, string user, string password, Guid session)
    {
        var serializer=new JavaScriptSerializer();
        var config=serializer.Deserialize<Dictionary<string,object>>(Render(port,user,password));
        var inbound=new Dictionary<string,object>{{"type","tun"},{"tag","tun-in"},{"interface_name",GuardTunnelIntent.NameFor(session)},
            {"address",new[]{"172.19.0.1/30","fdfe:dcba:9876::1/126"}},{"mtu",1500},{"auto_route",false},{"strict_route",false},{"stack","mixed"}};
        var inbounds=((System.Collections.IEnumerable)config["inbounds"]).Cast<object>().ToList();inbounds.Insert(0,inbound);config["inbounds"]=inbounds.ToArray();
        return serializer.Serialize(config);
    }
    internal GuardControlEndpoint[] Endpoints()
    {
        var primary = new GuardControlEndpoint { Core = GuardCoreKind.SingBox, Address = Address, Port = Port, Network = Network };
        return snapshot.Protocol == "shadowsocks" ? new[] { primary, new GuardControlEndpoint { Core = GuardCoreKind.SingBox, Address = Address, Port = Port, Network = "udp" } } : new[] { primary };
    }
    internal string RenderBoundTunnel(int port,string user,string password,Guid session,WindowsEndpointRoute upstream)
    {
        if(upstream==null||!upstream.Endpoint.Equals(IPAddress.Parse(Address)))throw new ArgumentException("Matching kernel-selected upstream required.");
        upstream.DemandBeforeLaunch();
        var serializer=new JavaScriptSerializer();var config=serializer.Deserialize<Dictionary<string,object>>(RenderTunnel(port,user,password,session));
        var outbounds=((System.Collections.IEnumerable)config["outbounds"]).Cast<object>().OfType<Dictionary<string,object>>().Where(o=>o.ContainsKey("tag")&&(string)o["tag"]=="proxy").ToArray();
        if(outbounds.Length!=1||!outbounds[0].ContainsKey("server")||(string)outbounds[0]["server"]!=Address||outbounds[0].ContainsKey("detour"))throw new InvalidOperationException("Unexpected typed proxy projection.");
        outbounds[0]["bind_interface"]=upstream.InterfaceName;
        return serializer.Serialize(config);
    }
}

// Service-created, installation-bound, process-local capability. No path/session
// is supplied by a client. TUN staging never enables default routes or grants.
internal sealed class GuardServiceProxyConfiguration : IDisposable
{
    readonly object sync = new object();
    readonly string scope, owner;
    readonly Guid installation;
    readonly GuardServiceConnection connection;
    FileStream configurationLease, ownerLease;
    Process creator;
    MachineTunnelLease tunnelLease;
    readonly long creation;
    bool disposed;
    internal Guid Session { get; }
    internal string Hash { get; }
    internal int LoopbackPort { get; }
    internal string ProxyUser { get; }
    internal string ProxyPassword { get; }
    internal GuardTunnelIntent Tunnel { get; }
    internal WindowsEndpointRoute UpstreamRoute { get; }
    internal MachineTunnelLease TunnelLease { get { lock(sync){if(disposed)throw new ObjectDisposedException(nameof(GuardServiceProxyConfiguration));return tunnelLease;} } }
    internal GuardCoreKind Kind => GuardCoreKind.SingBox;
    private GuardServiceProxyConfiguration(string scope, GuardOwnerPolicy owner, GuardServiceConnection connection, Guid session, string hash, int port, string user, string password, FileStream configLease, FileStream ownerLease, Process creator, GuardTunnelIntent tunnel, MachineTunnelLease tunnelLease,WindowsEndpointRoute upstreamRoute)
    {
        this.scope = scope; this.owner = owner.OwnerSid; installation = owner.InstallationId; this.connection = connection;
        Session = session; Hash = hash; LoopbackPort = port; ProxyUser = user; ProxyPassword = password;
        configurationLease = configLease; this.ownerLease = ownerLease; this.creator = creator; creation = creator.StartTime.ToUniversalTime().ToFileTimeUtc();
        Tunnel=tunnel;this.tunnelLease=tunnelLease;UpstreamRoute=upstreamRoute;
    }
    static string Secret()
    { var bytes = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes); return Convert.ToBase64String(bytes); }
    internal static void CleanupOrphans(GuardFileRecoveryJournal journalLease)
    {
        // Only at startup, before a runtime/core exists, with the exclusive
        // machine journal lease. Never accept a directory from IPC or config.
        TrafficGuardServiceIdentity.Demand();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN"), journal = Path.Combine(root, "TrafficGuard");
        if (journalLease == null) throw new ArgumentNullException(nameof(journalLease));
        journalLease.DemandMachineLease(journal);
        foreach (string path in new[] { root, journal }) GuardMachineJournalSecurity.Validate(path, true);
        string sessions = Path.Combine(journal, "CoreSessions");
        if (!Directory.Exists(sessions)) { if (File.Exists(sessions)) throw new IOException("Unexpected core session root."); return; }
        GuardMachineJournalSecurity.Validate(sessions, true);
        var scopes = Directory.EnumerateFileSystemEntries(sessions).Take(1025).ToArray();
        if (scopes.Length > 1024) throw new IOException("Too many orphaned core sessions.");
        var leases = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Validate the entire set before deleting anything. Unexpected
            // files/reparse points remain intact for explicit recovery.
            foreach (string scope in scopes)
            {
                Guid id; string name = Path.GetFileName(scope);
                if (!Guid.TryParseExact(name, "N", out id) || id == Guid.Empty || id.ToString("N") != name || !Directory.Exists(scope)) throw new IOException("Unexpected core session entry.");
                GuardMachineJournalSecurity.Validate(scope, true);
                string config = Path.Combine(scope, "config.json");
                var entries = Directory.EnumerateFileSystemEntries(scope).Take(2).ToArray();
                if (entries.Length > 1 || entries.Length == 1 && !string.Equals(entries[0], config, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected orphan session contents.");
                if (entries.Length == 1)
                {
                    GuardMachineJournalSecurity.Validate(config, false);
                    // Conflicts with a live session's read lease. No bytes are
                    // read; the held handle prevents a fresh config consumer.
                    leases.Add(scope, new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Delete));
                }
            }
            foreach (string scope in scopes)
            {
                FileStream lease;
                if (leases.TryGetValue(scope, out lease)) { File.Delete(Path.Combine(scope, "config.json")); lease.Dispose(); leases.Remove(scope); }
                Directory.Delete(scope, false);
            }
        }
        finally { foreach (var lease in leases.Values) lease.Dispose(); }
    }
    internal static GuardServiceProxyConfiguration Create(GuardOwnerPolicy expected, GuardServiceConnection connection)
        => CreateCore(expected,connection,false);
    internal static GuardServiceProxyConfiguration CreateTunnel(GuardOwnerPolicy expected, GuardServiceConnection connection)
        => CreateCore(expected,connection,true);
    private static GuardServiceProxyConfiguration CreateCore(GuardOwnerPolicy expected, GuardServiceConnection connection, bool tunnel)
    {
        TrafficGuardServiceIdentity.Demand();
        if (expected == null || connection == null) throw new ArgumentNullException("Service owner/connection required.");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOVA VPN"), journal = Path.Combine(root, "TrafficGuard");
        foreach (string path in new[] { root, journal }) GuardMachineJournalSecurity.Validate(path, true);
        FileStream ownerLease = null, configLease = null; Process creator = null; string scope = null;
        MachineTunnelLease tunnelLease=null;
        try
        {
            ownerLease = new FileStream(Path.Combine(root, "owner-policy.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = TrafficGuardServiceIdentity.ReadOwnerPolicy(journal);
            if (actual == null || actual.OwnerSid != expected.OwnerSid || actual.InstallationId != expected.InstallationId) throw new UnauthorizedAccessException("Installed owner changed.");
            creator = Process.GetCurrentProcess(); if (creator.Handle == IntPtr.Zero) throw new InvalidOperationException("Missing service process handle.");
            TrafficGuardServiceIdentity.ValidateServerProcess((uint)creator.Id);
            if(tunnel){tunnelLease=MachineTunnelLease.TryCreateForService();if(tunnelLease==null)throw new TunnelLeaseBusyException();}
            var upstream=tunnel?WindowsEndpointRoute.Capture(IPAddress.Parse(connection.Address)):null;
            var session = Guid.NewGuid(); string sessions = Path.Combine(journal, "CoreSessions"); GuardMachineJournalSecurity.PrepareDirectory(sessions);
            string candidateScope = Path.Combine(sessions, session.ToString("N")); if (Directory.Exists(candidateScope) || File.Exists(candidateScope)) throw new IOException("Session collision.");
            GuardMachineJournalSecurity.PrepareDirectory(candidateScope); scope = candidateScope;
            int port; var socket = new TcpListener(IPAddress.Loopback, 0);
            try { socket.Start(); port = ((IPEndPoint)socket.LocalEndpoint).Port; } finally { socket.Stop(); }
            string user = Secret(), password = Secret(), configuration = Path.Combine(scope, "config.json");
            string json=connection.Render(port,user,password);GuardTunnelIntent intent=null;
            if(tunnel)
            {
                var managed=TunnelNetworkPreflight.PrepareManaged(connection.RenderBoundTunnel(port,user,password,session,upstream),TunnelNetworkPreflight.Capture(),true);
                json=managed.Json;intent=GuardTunnelIntent.Capture(session,managed.Addresses);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            string hash; using (var sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(bytes));
            using (var file = GuardMachineJournalSecurity.Open(configuration, FileMode.CreateNew, FileShare.None, FileOptions.WriteThrough)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            configLease = new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var sha = SHA256.Create()) if (Convert.ToBase64String(sha.ComputeHash(configLease)) != hash) throw new InvalidDataException("Generated configuration changed.");
            configLease.Position = 0;
            var result = new GuardServiceProxyConfiguration(scope, actual, connection, session, hash, port, user, password, configLease, ownerLease, creator,intent,tunnelLease,upstream);
            scope = null; configLease = ownerLease = null; creator = null; tunnelLease=null; return result;
        }
        finally
        {
            try{configLease?.Dispose(); ownerLease?.Dispose(); creator?.Dispose();if (scope != null && Directory.Exists(scope)) DeleteScope(scope);}
            finally{tunnelLease?.Dispose();}
        }
    }
    internal void DemandCurrent(GuardOwnerPolicy expected)
    {
        TrafficGuardServiceIdentity.Demand();
        lock (sync)
        {
            if (disposed || expected == null || expected.OwnerSid != owner || expected.InstallationId != installation || !configurationLease.CanRead || !ownerLease.CanRead) throw new UnauthorizedAccessException("Generated session authority ended.");
            using (var current = Process.GetCurrentProcess()) if (creator.HasExited || creator.Id != current.Id || creation != current.StartTime.ToUniversalTime().ToFileTimeUtc()) throw new UnauthorizedAccessException("Generated session process changed.");
            UpstreamRoute?.DemandCurrent();
        }
    }
    internal GuardControlEndpoint[] Endpoints() { lock (sync) { if (disposed) throw new ObjectDisposedException(nameof(GuardServiceProxyConfiguration)); return connection.Endpoints(); } }
    static void DeleteScope(string scope)
    {
        GuardMachineJournalSecurity.Validate(scope, true); string path = Path.Combine(scope, "config.json");
        if (File.Exists(path)) { GuardMachineJournalSecurity.Validate(path, false); File.Delete(path); }
        Directory.Delete(scope, false); // Never recursively delete an unexpected object.
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            try { configurationLease?.Dispose(); DeleteScope(scope); }
            finally { try{ownerLease?.Dispose(); creator?.Dispose();}finally{tunnelLease?.Dispose();tunnelLease=null;} }
        }
    }
}

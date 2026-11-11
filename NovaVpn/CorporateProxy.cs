using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class CorporateProxySettings
{
    [DataMember] public bool Enabled;
    [DataMember] public string Kind = "http";
    [DataMember] public string Server = "";
    [DataMember] public int Port = 8080;
    [DataMember] public string Username = "";
    [DataMember] public string Password = "";
    [DataMember] public string TlsServerName = "";
    [DataMember] public string CertificatePath = "";
    [DataMember] public string PacScript = "";
    [DataMember] public bool PacAllowDirect;
    public CorporateProxySettings Copy() => new CorporateProxySettings { Enabled = Enabled, Kind = Kind, Server = Server, Port = Port,
        Username = Username, Password = Password, TlsServerName = TlsServerName, CertificatePath = CertificatePath,
        PacScript = PacScript, PacAllowDirect = PacAllowDirect };
    public void Validate()
    {
        if (!Enabled) return;
        if (Kind == "pac")
        {
            _ = new CorporatePacQuery(PacScript, "https://validation.invalid/");
            if (!string.IsNullOrEmpty(Server) || !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password) ||
                !string.IsNullOrEmpty(TlsServerName) || !string.IsNullOrEmpty(CertificatePath))
                throw new ArgumentException("PAC пока использует только анонимные HTTP/HTTPS-прокси. Удалите ручные реквизиты прокси.");
            return;
        }
        if (!string.IsNullOrEmpty(PacScript) || PacAllowDirect) throw new ArgumentException("PAC-политика не применяется к ручному прокси.");
        if (Kind != "http" && Kind != "https" && Kind != "socks5") throw new NotSupportedException("Выберите HTTP CONNECT, HTTPS CONNECT, SOCKS5 или PAC. Вход Windows пока не поддерживается.");
        if (string.IsNullOrEmpty(Server) || Server != Server.Trim() || Server.Length > 253 || Server.Any(char.IsControl) ||
            Server.Contains("%") || Uri.CheckHostName(Server) == UriHostNameType.Unknown || Port < 1 || Port > 65535)
            throw new ArgumentException("Укажите имя или IP прокси без URL, логина и пути, а также порт от 1 до 65535.");
        string user = Username ?? "", password = Password ?? "";
        if ((user.Length == 0) != (password.Length == 0) || user.Contains(":") || user.Any(char.IsControl) || password.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(user) > 255 || Encoding.UTF8.GetByteCount(password) > 255)
            throw new ArgumentException("Укажите логин и пароль вместе либо оставьте оба пустыми. До 255 байт UTF-8, без управляющих символов и двоеточия в логине.");
        if (!string.IsNullOrEmpty(TlsServerName) && (TlsServerName.Length > 253 || Uri.CheckHostName(TlsServerName) == UriHostNameType.Unknown))
            throw new ArgumentException("Некорректное имя проверки TLS прокси.");
        if (!string.IsNullOrEmpty(CertificatePath) && (!System.IO.Path.IsPathRooted(CertificatePath) || CertificatePath.Any(char.IsControl)))
            throw new ArgumentException("Путь к доверенным сертификатам прокси должен быть абсолютным.");
        if (Kind != "https" && (!string.IsNullOrEmpty(TlsServerName) || !string.IsNullOrEmpty(CertificatePath)))
            throw new ArgumentException("Параметры сертификата задаются только для HTTPS-прокси.");
    }
}

public static class CorporateProxy
{
    public const string RequiredOutboundType = "nova-corporate-proxy-required";
    public static bool Enabled(VpnProfile profile) => profile?.CorporateProxy?.Enabled == true;
    // A standalone outbound cannot express the complete dependency. Old clients
    // must fail native validation rather than silently connect without the proxy.
    public static string ExportMarker() => "{\"type\":\"" + RequiredOutboundType + "\"}";
    private static readonly string[] DialFields = { "detour", "bind_interface", "inet4_bind_address", "inet6_bind_address", "bind_address_no_port", "routing_mark", "reuse_addr", "netns",
        "connect_timeout", "tcp_fast_open", "tcp_multi_path", "disable_tcp_keep_alive", "tcp_keep_alive", "tcp_keep_alive_interval", "udp_fragment", "network_strategy", "network_type", "fallback_network_type", "fallback_delay", "domain_strategy" };
    internal static void ValidateTcpOutbound(Dictionary<string, object> outbound)
    {
        string type = Convert.ToString(outbound["type"]);
        if (!new[] { "vless", "trojan", "vmess", "shadowsocks", "socks", "http" }.Contains(type) ||
            outbound.TryGetValue("transport", out object transport) && transport is Dictionary<string, object> tr && tr.TryGetValue("type", out object kind) && Equals(kind, "quic"))
            throw new NotSupportedException("Этот протокол или транспорт нельзя подключить через TCP-прокси без изменения его условий.");
    }
    public static void Apply(Dictionary<string, object> root, VpnProfile profile, string primaryTag)
    {
        if (!Enabled(profile)) return;
        var settings = profile.CorporateProxy; settings.Validate();
        if (profile.Protocol == "sing-box") throw new NotSupportedException("Корпоративный прокси настраивается для отдельного профиля, а не полного конфига.");
        var outbounds = ((IEnumerable)root["outbounds"]).Cast<object>().ToList();
        var matches = outbounds.OfType<Dictionary<string, object>>().Where(o => o.TryGetValue("tag", out object tag) && Equals(tag, primaryTag)).ToArray();
        string upstreamTag = primaryTag + "-corporate";
        if (matches.Length != 1 || outbounds.OfType<Dictionary<string, object>>().Any(o => o.TryGetValue("tag", out object tag) && Equals(tag, upstreamTag)))
            throw new InvalidOperationException("Конфликт выходов корпоративного прокси.");
        var outbound = matches[0]; ValidateTcpOutbound(outbound);
        if (DialFields.Any(outbound.ContainsKey))
            throw new NotSupportedException("Профиль уже содержит параметры соединения или цепочку выходов. Корпоративный прокси не заменяет их автоматически.");
        if (outbound.TryGetValue("domain_resolver", out object resolver))
        {
            if (!(resolver is string name) || name != "local-dns" && name != "bootstrap")
                throw new NotSupportedException("Собственная политика DNS сервера не заменяется корпоративным прокси.");
            outbound.Remove("domain_resolver");
        }
        outbound["detour"] = upstreamTag;
        var route = (Dictionary<string, object>)root["route"];
        if (!route.TryGetValue("default_domain_resolver", out object bootstrap) || !(bootstrap is string dnsTag) || dnsTag != "local-dns" && dnsTag != "bootstrap")
            throw new NotSupportedException("Неизвестный DNS начального соединения корпоративного прокси.");
        if (settings.Kind == "pac")
        {
            // Valid for native --check, but cannot connect to the real upstream.
            // Only the owned host replaces this endpoint after descriptor validation.
            outbounds.Add(new Dictionary<string, object> { ["type"] = "http", ["tag"] = upstreamTag,
                ["server"] = "127.0.0.1", ["server_port"] = 1 });
            root["outbounds"] = outbounds.ToArray(); return;
        }
        var upstream = new Dictionary<string, object> { ["type"] = settings.Kind == "socks5" ? "socks" : "http", ["tag"] = upstreamTag,
            ["server"] = settings.Server, ["server_port"] = settings.Port, ["domain_resolver"] = dnsTag };
        if (settings.Kind == "socks5") { upstream["version"] = "5"; upstream["network"] = "tcp"; }
        if (!string.IsNullOrEmpty(settings.Username)) { upstream["username"] = settings.Username; upstream["password"] = settings.Password; }
        if (settings.Kind == "https")
        {
            var tls = new Dictionary<string, object> { ["enabled"] = true, ["server_name"] = string.IsNullOrEmpty(settings.TlsServerName) ? settings.Server : settings.TlsServerName };
            if (!string.IsNullOrEmpty(settings.CertificatePath)) tls["certificate_path"] = settings.CertificatePath;
            upstream["tls"] = tls;
        }
        outbounds.Add(upstream); root["outbounds"] = outbounds.ToArray();
    }
    internal static string Apply(string json, VpnProfile profile, string primaryTag)
    {
        if (!Enabled(profile)) return json;
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = serializer.Deserialize<Dictionary<string, object>>(json); Apply(root, profile, primaryTag); return serializer.Serialize(root);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

namespace NovaVpn;

[DataContract]
public sealed class LocalProxyEndpoint
{
    [DataMember] public string Id = Guid.NewGuid().ToString("N");
    [DataMember] public string Name = "Локальный прокси";
    [DataMember] public string ProfileId;
    [DataMember] public int Port = 1081;
    [DataMember] public string InboundType = "mixed";
    [DataMember] public string Username = "nova-" + Guid.NewGuid().ToString("N").Substring(0, 8);
    [DataMember] public string Password = Guid.NewGuid().ToString("N");
}

public static class LocalProxyConfiguration
{
    public const int MaxRunning = 8;
    public static void Normalize(AppState state)
    {
        if (state.LocalProxies == null) state.LocalProxies = new List<LocalProxyEndpoint>();
        state.LocalProxies.RemoveAll(p => p == null);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in state.LocalProxies)
            if (string.IsNullOrWhiteSpace(endpoint.Id) || !ids.Add(endpoint.Id)) { endpoint.Id = Guid.NewGuid().ToString("N"); ids.Add(endpoint.Id); }
    }
    public static string Validate(LocalProxyEndpoint endpoint, VpnProfile profile)
    {
        if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.Id) || endpoint.Id.Length > 100) return "Некорректный идентификатор прокси.";
        if (string.IsNullOrWhiteSpace(endpoint.Name) || endpoint.Name.Length > 80) return "Название должно содержать от 1 до 80 символов.";
        if (endpoint.Port < 1 || endpoint.Port > 65535) return "Порт должен быть от 1 до 65535.";
        if (endpoint.InboundType != "mixed" && endpoint.InboundType != "socks" && endpoint.InboundType != "http") return "Выберите HTTP, SOCKS или смешанный вход.";
        if (string.IsNullOrWhiteSpace(endpoint.Username) || endpoint.Username.Contains(":") || endpoint.Username.Any(char.IsControl) || Encoding.UTF8.GetByteCount(endpoint.Username) > 255 ||
            string.IsNullOrEmpty(endpoint.Password) || endpoint.Password.Any(char.IsControl) || Encoding.UTF8.GetByteCount(endpoint.Password) > 255) return "Логин и пароль обязательны; длина каждого — не более 255 байт UTF-8. Двоеточие в логине запрещено.";
        if (profile == null || profile.Id != endpoint.ProfileId || profile.RetiredFromSubscription) return "Назначенный профиль отсутствует или удалён из подписки.";
        if (profile.Protocol == "sing-box") return "Выберите отдельный профиль вместо полного конфига.";
        return null;
    }

    public static string Build(LocalProxyEndpoint endpoint, VpnProfile profile) => Build(endpoint, profile, VpnEngine.SingBox);

    public static string Build(LocalProxyEndpoint endpoint, VpnProfile profile, VpnEngine engine)
    {
        if (engine != VpnEngine.SingBox && engine != VpnEngine.Reality) throw new ArgumentOutOfRangeException(nameof(engine));
        string error = Validate(endpoint, profile); if (error != null) throw new InvalidOperationException(error);
        if (engine == VpnEngine.Reality && CorporateProxy.Enabled(profile)) throw new NotSupportedException("Для подключения через корпоративный прокси выберите sing-box. Reality Core пока не поддерживает такую цепочку.");
        var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        var outbound = json.Deserialize<Dictionary<string, object>>(ConfigBuilder.ExportBaseOutbound(profile));
        if (!outbound.TryGetValue("type", out object type) || !new[] { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "socks", "http" }.Contains(Convert.ToString(type)))
            throw new NotSupportedException("Этот профиль не поддерживается локальными прокси.");
        // ExportOutbound normally references the full tunnel's local-dns. This
        // isolated process owns only its bootstrap resolver, never the TUN DNS.
        if (outbound.TryGetValue("domain_resolver", out object resolver))
        {
            if (!(resolver is string name) || name != "local-dns" && name != "bootstrap")
                throw new NotSupportedException("Профиль с отдельной политикой DNS нельзя перенести в изолированный прокси без изменения этой политики.");
            outbound["domain_resolver"] = "bootstrap";
        }
        if (engine == VpnEngine.Reality)
        {
            if (!Equals(type, "vless") && !Equals(type, "trojan"))
                throw new NotSupportedException("Reality Core принимает только профили VLESS и Trojan.");
            if (!outbound.TryGetValue("tls", out object tlsValue) || !(tlsValue is Dictionary<string, object> tls) ||
                !tls.TryGetValue("enabled", out object enabled) || !Equals(enabled, true) ||
                tls.TryGetValue("insecure", out object insecure) && !Equals(insecure, false) ||
                outbound.TryGetValue("allow_insecure", out object plaintext) && !Equals(plaintext, false))
                throw new NotSupportedException("Для сравнения с Reality Core требуется TLS или Reality с проверкой сертификата. Небезопасный режим автоматически не включается.");
            // Reality resolves the server itself. Its strict native --check must
            // still accept every other field; no TLS/transport field is dropped.
            outbound.Remove("domain_resolver");
        }
        outbound["tag"] = "workload";
        var root = Obj("log", Obj("level", "error"),
            "dns", Obj("servers", new object[] { Obj("type", "local", "tag", "bootstrap") }),
            "inbounds", new object[] { Obj("type", endpoint.InboundType, "tag", "workload-in", "listen", "127.0.0.1", "listen_port", endpoint.Port,
                "users", new object[] { Obj("username", endpoint.Username, "password", endpoint.Password) }) },
            "outbounds", new object[] { outbound },
            "route", Obj("final", "workload", "auto_detect_interface", true, "default_domain_resolver", "bootstrap"));
        if (engine == VpnEngine.Reality) ((Dictionary<string, object>)root["route"]).Remove("default_domain_resolver");
        CorporateProxy.Apply(root, profile, "workload");
        return json.Serialize(root);
    }

    private static Dictionary<string, object> Obj(params object[] values)
    {
        var result = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1]; return result;
    }
}

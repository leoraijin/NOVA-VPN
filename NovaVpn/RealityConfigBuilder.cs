using System;
using System.Collections.Generic;
using System.Collections;
using System.Web.Script.Serialization;

namespace NovaVpn;

public static class RealityConfigBuilder
{
    // Native --check remains mandatory. Never discard unsupported routing policies.
    public static string Build(VpnProfile profile, RoutingSettings routing)
    {
        if (profile == null) throw new InvalidOperationException("Сначала выберите сервер.");
        if (CorporateProxy.Enabled(profile)) throw new NotSupportedException("Корпоративный прокси пока поддерживается только ядром sing-box.");
        if (routing == null) throw new ArgumentNullException(nameof(routing));
        if (routing.MultiServer != null) throw new NotSupportedException("Назначения серверов требуют sing-box.");
        if (routing.MigratedPolicy != null) throw new NotSupportedException("Перенесённые правила пока требуют sing-box; их нельзя молча отбросить в Reality Core.");
        if (profile.Protocol != "vless" && profile.Protocol != "trojan")
            throw new NotSupportedException("Для этого профиля выберите стандартное ядро sing-box. Экспериментальное ядро принимает профили VLESS и Trojan.");
        if (routing.BypassPrivateDns || string.Equals(routing.DnsMode,"System",StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Reality Core не поддерживает системный DNS внутри TUN. Выберите защищённый DNS и отключите обход DNS локальных имён либо используйте sing-box.");
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = serializer.Deserialize<Dictionary<string, object>>(ConfigBuilder.Build(profile,routing));
        var route = (Dictionary<string, object>)root["route"];
        // This core ignores sing-box reverse_mapping. Until Windows fake-IP
        // adaptation is validated, domain policies must not silently degrade.
        if (ContainsDomainPolicy(route))
            throw new NotSupportedException("Правила сайтов и блокировка рекламы пока не поддерживаются экспериментальным ядром Windows. Выберите sing-box, чтобы сохранить маршрутизацию сайтов.");
        MoveResolver(route,"default_domain_resolver");
        foreach (object value in (System.Collections.IEnumerable)root["outbounds"])
            MoveResolver((Dictionary<string, object>)value,"domain_resolver");
        var dns = (Dictionary<string, object>)root["dns"];
        var servers = new List<object>();
        foreach (object value in (System.Collections.IEnumerable)dns["servers"])
        {
            var server = (Dictionary<string, object>)value;
            if (server.TryGetValue("tag",out object tag) && Equals(tag,"local-dns")) continue;
            if (server.TryGetValue("type",out object kind) && Equals(kind,"https") &&
                server.TryGetValue("tls",out object tlsValue))
            {
                var tls = (Dictionary<string, object>)tlsValue;
                if (tls.TryGetValue("server_name",out object sni) && sni is string host && !string.IsNullOrWhiteSpace(host))
                {
                    var uri = new UriBuilder("https",host,
                        server.TryGetValue("server_port",out object port) ? Convert.ToInt32(port) : 443,
                        server.TryGetValue("path",out object path) ? Convert.ToString(path) : "/dns-query");
                    server["address"] = uri.Uri.AbsoluteUri;
                    server.Remove("type"); server.Remove("server"); server.Remove("server_port");
                    server.Remove("path"); server.Remove("tls");
                }
            }
            servers.Add(server);
        }
        servers.Add(new Dictionary<string,object> {
            {"type","udp"}, {"tag","nova-bootstrap"}, {"server","1.1.1.1"},
            {"server_port",53}, {"detour","direct"}
        });
        dns["servers"] = servers.ToArray();
        dns["bootstrap"] = "nova-bootstrap";
        return serializer.Serialize(root);
    }

    // Apply after building the managed policy and inserting the per-exit probe listeners.
    internal static string AdaptMultiServer(string config)
    {
        const string bootstrap = "nova-bootstrap-dns";
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = serializer.Deserialize<Dictionary<string, object>>(config);
        var route = (Dictionary<string, object>)root["route"];
        RemoveManagedResolver(route, "default_domain_resolver", bootstrap);
        foreach (Dictionary<string, object> outbound in (IEnumerable)root["outbounds"])
            RemoveManagedResolver(outbound, "domain_resolver", bootstrap);
        var dns = (Dictionary<string, object>)root["dns"];
        foreach (Dictionary<string, object> server in (IEnumerable)dns["servers"])
        {
            RemoveManagedResolver(server, "domain_resolver", bootstrap);
            if (Equals(server["tag"], bootstrap))
            {
                if (!Equals(server["type"], "local"))
                    throw new NotSupportedException("Неизвестный тип начального DNS Reality Core.");
                server["type"] = "udp";
                server["server"] = "1.1.1.1";
                server["server_port"] = 53;
                server["detour"] = "nova-direct";
                continue;
            }
            if (!Equals(server["type"], "https")) continue;
            var tls = (Dictionary<string, object>)server["tls"];
            if (!tls.TryGetValue("enabled", out object enabled) || !Equals(enabled, true) ||
                tls.TryGetValue("insecure", out object insecure) && !Equals(insecure, false))
                throw new NotSupportedException("Reality Core требует проверку сертификата DNS.");
            var uri = new UriBuilder("https", (string)tls["server_name"],
                server.TryGetValue("server_port", out object port) ? Convert.ToInt32(port) : 443,
                server.TryGetValue("path", out object path) ? Convert.ToString(path) : "/dns-query");
            server["address"] = uri.Uri.AbsoluteUri;
            foreach (string key in new[] { "type", "server", "server_port", "path", "tls" })
                server.Remove(key);
        }
        dns["bootstrap"] = bootstrap;
        return serializer.Serialize(root);
    }

    private static void RemoveManagedResolver(Dictionary<string, object> item, string key, string expected)
    {
        if (!item.TryGetValue(key, out object value)) return;
        if (!Equals(value, expected))
            throw new NotSupportedException("Неизвестный начальный DNS в конфигурации Reality Core.");
        item.Remove(key);
    }

    private static bool ContainsDomainPolicy(object value)
    {
        if (value is Dictionary<string, object> item)
        {
            foreach (var pair in item)
            {
                if (pair.Key == "domain" || pair.Key == "domain_suffix" || pair.Key == "domain_keyword"
                    || pair.Key == "domain_regex" || pair.Key == "geosite" || pair.Key == "rule_set") return true;
                if (ContainsDomainPolicy(pair.Value)) return true;
            }
        }
        else if (value is IEnumerable list && !(value is string))
            foreach (object child in list) if (ContainsDomainPolicy(child)) return true;
        return false;
    }

    private static void MoveResolver(Dictionary<string, object> value, string key)
    {
        if (!value.TryGetValue(key,out object resolver)) return;
        if (!(resolver is string name) || name != "local-dns")
            throw new NotSupportedException("Нестандартный DNS подключения: используйте sing-box.");
        value.Remove(key);
    }
}

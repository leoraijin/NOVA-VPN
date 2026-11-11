using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace NovaVpn;

public static class ExternalProfileImport
{
    private const int MaxProfiles = 2000;
    private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 64 };

    public static bool TryRead(string text, string subscriptionUrl, out ImportResult result)
    {
        result = new ImportResult(new List<VpnProfile>(), subscriptionUrl);
        string trimmed = (text ?? "").TrimStart();
        object document;
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try { document = Json().DeserializeObject(text); }
            catch (ArgumentException) { throw new FormatException("Некорректный JSON экспорта."); }
        }
        else if (trimmed.Contains("proxies:") || trimmed.StartsWith("---"))
        {
            try { document = ReadYaml(text); }
            catch (YamlException) { throw new FormatException("Некорректный YAML экспорта."); }
        }
        else return false;

        var root = document as Dictionary<string, object>;
        if (root != null && root.TryGetValue("proxies", out object proxies))
        {
            ReadEntries(Items(proxies), "Clash/Mihomo", ReadMihomo, result);
            if (root.ContainsKey("rules")) result.RoutingPreviews.Add(ForeignRoutingPreview.Read(root, true));
            AddContainerWarnings(root, result);
        }
        else if (root != null && root.TryGetValue("outbounds", out object outbounds) && Items(outbounds).OfType<Dictionary<string, object>>().Any(o => o.ContainsKey("protocol")))
        {
            ReadEntries(Items(outbounds), "Xray", ReadXray, result);
            if (root.ContainsKey("routing")) result.RoutingPreviews.Add(ForeignRoutingPreview.Read(root, false));
            AddContainerWarnings(root, result);
        }
        else if (document is object[] array)
        {
            int index = 0;
            foreach (var entry in array)
            {
                index++;
                try
                {
                    if (entry is string link) Add(result, LinkParser.ParseSingle(link));
                    else if (entry is Dictionary<string, object> item && item.ContainsKey("protocol"))
                        foreach (var p in ReadXray(item)) Add(result, p);
                    else if (entry is Dictionary<string, object> config && config.ContainsKey("outbounds"))
                    {
                        if (TryRead(Json().Serialize(config), subscriptionUrl, out var nested))
                        { foreach (var p in nested.Profiles) Add(result, p); result.Warnings.AddRange(nested.Warnings); result.RoutingPreviews.AddRange(nested.RoutingPreviews); }
                        else Add(result, LinkParser.ParseSingle(Json().Serialize(config)));
                    }
                    else throw new NotSupportedException("Неизвестная схема записи");
                }
                catch (Exception ex) when (ex is FormatException || ex is NotSupportedException || ex is ArgumentException)
                { result.Warnings.Add("JSON, запись " + index + ": " + SafeReason(ex)); }
            }
        }
        else return false; // A full sing-box configuration keeps its original semantics.
        if (result.Profiles.Count == 0) throw new FormatException("В экспорте нет поддерживаемых профилей. " + string.Join("; ", result.Warnings.Take(3)));
        foreach (var p in result.Profiles) { p.SubscriptionUrl = subscriptionUrl ?? ""; p.ProviderName = p.Name; }
        return true;
    }

    private static void ReadEntries(IEnumerable<object> entries, string format, Func<Dictionary<string, object>, IEnumerable<VpnProfile>> read, ImportResult result)
    {
        int index = 0;
        foreach (var entry in entries)
        {
            index++;
            try
            {
                if (!(entry is Dictionary<string, object> map)) throw new FormatException("Запись должна быть объектом");
                foreach (var profile in read(map)) Add(result, profile);
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException || ex is ArgumentException)
            { result.Warnings.Add(format + ", запись " + index + ": " + SafeReason(ex)); }
        }
    }
    private static string SafeReason(Exception ex) => ex is NotSupportedException ? ex.Message : "некорректные параметры сервера";
    private static void Add(ImportResult result, VpnProfile profile)
    {
        if (result.Profiles.Count >= MaxProfiles) throw new InvalidOperationException("В экспорте больше 2000 профилей.");
        result.Profiles.Add(profile);
    }
    private static void AddContainerWarnings(Dictionary<string, object> root, ImportResult result)
    {
        if (root.Keys.Any(k => new[] { "rules", "routing", "dns", "inbounds", "proxy-groups", "tun" }.Contains(k)))
            result.Warnings.Add("Перенесены серверы. Правила, DNS, группы и локальные входы другого клиента требуют отдельного сопоставления и автоматически не применяются.");
    }

    private static IEnumerable<VpnProfile> ReadMihomo(Dictionary<string, object> node)
    {
        string type = Required(node, "type").ToLowerInvariant();
        if (type == "ss") type = "shadowsocks";
        if (!new[] { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic" }.Contains(type)) throw new NotSupportedException("протокол не поддерживается выбранными ядрами");
        RejectUnknown(node, "name,type,server,port,uuid,password,cipher,alterId,tls,servername,sni,skip-cert-verify,client-fingerprint,alpn,network,ws-opts,grpc-opts,h2-opts,http-opts,reality-opts,flow,udp,obfs,obfs-password,congestion-controller");
        if (node.ContainsKey("udp") && !Boolean(node["udp"])) throw new NotSupportedException("ограничение udp=false требует отдельного правила маршрутизации");
        if (Number(node, "alterId", 0) != 0) throw new NotSupportedException("устаревший VMess alterId не поддерживается");
        var outbound = Object("type", type, "server", Required(node, "server"), "server_port", Port(node, "port"));
        if (type == "vless" || type == "vmess" || type == "tuic") outbound["uuid"] = Required(node, "uuid");
        if (type == "shadowsocks" || type == "trojan" || type == "hysteria2" || type == "tuic") outbound["password"] = Required(node, "password");
        if (type == "shadowsocks") outbound["method"] = Required(node, "cipher");
        if (type == "vmess") outbound["security"] = Text(node, "cipher", "auto");
        if (type == "vless" && !string.IsNullOrWhiteSpace(Text(node, "flow"))) outbound["flow"] = Text(node, "flow");
        if (type == "tuic") outbound["congestion_control"] = Text(node, "congestion-controller", "bbr");
        if (type == "hysteria2" && !string.IsNullOrWhiteSpace(Text(node, "obfs")))
            outbound["obfs"] = Object("type", Text(node, "obfs"), "password", Required(node, "obfs-password"));
        bool tls = type == "trojan" || type == "hysteria2" || type == "tuic" || node.ContainsKey("tls") && Boolean(node["tls"]) || node.ContainsKey("reality-opts");
        if (tls)
        {
            var security = Object("enabled", true, "server_name", Text(node, "servername", Text(node, "sni", Text(node, "server"))),
                "insecure", node.ContainsKey("skip-cert-verify") && Boolean(node["skip-cert-verify"]));
            if (node.ContainsKey("alpn")) security["alpn"] = Items(node["alpn"]).Select(Convert.ToString).ToArray();
            string fingerprint = Text(node, "client-fingerprint");
            if (fingerprint != "") security["utls"] = Object("enabled", true, "fingerprint", fingerprint);
            if (node.TryGetValue("reality-opts", out var realityValue))
            {
                var reality = Map(realityValue); RejectUnknown(reality, "public-key,short-id");
                security["reality"] = Object("enabled", true, "public_key", Required(reality, "public-key"), "short_id", Text(reality, "short-id"));
            }
            outbound["tls"] = security;
        }
        string network = Text(node, "network", "tcp").ToLowerInvariant();
        if (network == "ws")
        {
            var options = OptionalMap(node, "ws-opts"); RejectUnknown(options, "path,headers,max-early-data,early-data-header-name");
            var transport = Object("type", "ws", "path", Text(options, "path", "/"));
            if (options.ContainsKey("headers")) transport["headers"] = StringMap(options["headers"]);
            if (options.ContainsKey("max-early-data")) transport["max_early_data"] = Number(options, "max-early-data", 0);
            if (options.ContainsKey("early-data-header-name")) transport["early_data_header_name"] = Text(options, "early-data-header-name");
            outbound["transport"] = transport;
        }
        else if (network == "grpc")
        {
            var options = OptionalMap(node, "grpc-opts"); RejectUnknown(options, "grpc-service-name");
            outbound["transport"] = Object("type", "grpc", "service_name", Text(options, "grpc-service-name"));
        }
        else if (network == "h2")
        {
            var options = OptionalMap(node, "h2-opts"); RejectUnknown(options, "host,path");
            outbound["transport"] = Object("type", "http", "host", Items(Value(options, "host")).Select(Convert.ToString).ToArray(), "path", Text(options, "path", "/"));
        }
        else if (network != "tcp") throw new NotSupportedException("транспорт требует отдельной реализации: " + SafeToken(network));
        if (node.ContainsKey("http-opts")) throw new NotSupportedException("HTTP-маскировка TCP не переносится в обычный TCP");
        yield return Profile(outbound, Text(node, "name", Text(node, "server")), "mihomo");
    }

    private static IEnumerable<VpnProfile> ReadXray(Dictionary<string, object> node)
    {
        string protocol = Required(node, "protocol").ToLowerInvariant();
        if (!new[] { "vless", "vmess", "trojan", "shadowsocks" }.Contains(protocol)) throw new NotSupportedException("служебный или неподдерживаемый outbound");
        RejectUnknown(node, "tag,protocol,settings,streamSettings,mux");
        var mux = OptionalMap(node, "mux");
        if (mux.ContainsKey("enabled") && Boolean(mux["enabled"])) throw new NotSupportedException("Xray mux требует совместимого ядра");
        var stream = OptionalMap(node, "streamSettings");
        RejectUnknown(stream, "network,security,tlsSettings,realitySettings,tcpSettings,rawSettings,wsSettings,grpcSettings,httpSettings,httpupgradeSettings");
        var settings = Map(Value(node, "settings"));
        string listName = protocol == "vless" || protocol == "vmess" ? "vnext" : "servers";
        RejectUnknown(settings, listName);
        var servers = Items(Value(settings, listName)).ToArray();
        if (servers.Length == 0) throw new FormatException("Нет адресов серверов");
        foreach (var entry in servers)
        {
            var server = Map(entry);
            RejectUnknown(server, listName == "vnext" ? "address,port,users" : "address,port,password,method,email,level");
            var users = listName == "vnext" ? Items(Value(server, "users")).Select(Map).ToArray() : new[] { server };
            if (users.Length == 0) throw new FormatException("Нет параметров авторизации");
            foreach (var user in users)
            {
                if (listName == "vnext") RejectUnknown(user, "id,alterId,security,encryption,flow,email,level");
                if (Number(user, "alterId", 0) != 0) throw new NotSupportedException("устаревший VMess alterId не поддерживается");
                if (Text(user, "encryption", "none") != "none") throw new NotSupportedException("схема шифрования VLESS не поддерживается");
                var outbound = Object("type", protocol, "server", Required(server, "address"), "server_port", Port(server, "port"));
                if (listName == "vnext") outbound["uuid"] = Required(user, "id"); else outbound["password"] = Required(user, "password");
                if (protocol == "shadowsocks") outbound["method"] = Required(user, "method");
                if (protocol == "vmess") outbound["security"] = Text(user, "security", "auto");
                if (protocol == "vless" && Text(user, "flow") != "") outbound["flow"] = Text(user, "flow");
                ApplyXrayStream(outbound, stream);
                yield return Profile(outbound, Text(node, "tag", Text(server, "address")), "xray");
            }
        }
    }

    private static void ApplyXrayStream(Dictionary<string, object> outbound, Dictionary<string, object> stream)
    {
        string security = Text(stream, "security", "none").ToLowerInvariant();
        if (security == "tls" || security == "reality")
        {
            var source = OptionalMap(stream, security == "tls" ? "tlsSettings" : "realitySettings");
            RejectUnknown(source, security == "tls" ? "serverName,allowInsecure,alpn,fingerprint" : "serverName,fingerprint,publicKey,shortId,spiderX,show");
            if (Text(source, "spiderX", "/") != "/") throw new NotSupportedException("нестандартный Reality spiderX не поддерживается");
            var tls = Object("enabled", true, "server_name", Text(source, "serverName", Text(outbound, "server")));
            if (source.ContainsKey("allowInsecure")) tls["insecure"] = Boolean(source["allowInsecure"]);
            if (source.ContainsKey("alpn")) tls["alpn"] = Items(source["alpn"]).Select(Convert.ToString).ToArray();
            if (Text(source, "fingerprint") != "") tls["utls"] = Object("enabled", true, "fingerprint", Text(source, "fingerprint"));
            if (security == "reality") tls["reality"] = Object("enabled", true, "public_key", Required(source, "publicKey"), "short_id", Text(source, "shortId"));
            outbound["tls"] = tls;
        }
        else if (security != "none") throw new NotSupportedException("режим TLS не поддерживается");
        string network = Text(stream, "network", "tcp").ToLowerInvariant();
        if (network == "tcp" || network == "raw")
        {
            if (OptionalMap(stream, "tcpSettings").Count > 0 || OptionalMap(stream, "rawSettings").Count > 0)
                throw new NotSupportedException("параметры TCP-маскировки требуют отдельной поддержки");
            return;
        }
        if (network == "ws")
        {
            var options = OptionalMap(stream, "wsSettings"); RejectUnknown(options, "path,headers");
            var transport = Object("type", "ws", "path", Text(options, "path", "/"));
            if (options.ContainsKey("headers")) transport["headers"] = StringMap(options["headers"]);
            outbound["transport"] = transport; return;
        }
        if (network == "grpc")
        {
            var options = OptionalMap(stream, "grpcSettings"); RejectUnknown(options, "serviceName,multiMode");
            if (options.ContainsKey("multiMode") && Boolean(options["multiMode"])) throw new NotSupportedException("gRPC multiMode не поддерживается");
            outbound["transport"] = Object("type", "grpc", "service_name", Text(options, "serviceName")); return;
        }
        if (network == "http" || network == "h2")
        {
            var options = OptionalMap(stream, "httpSettings"); RejectUnknown(options, "host,path");
            outbound["transport"] = Object("type", "http", "host", Items(Value(options, "host")).Select(Convert.ToString).ToArray(), "path", Text(options, "path", "/")); return;
        }
        if (network == "httpupgrade")
        {
            var options = OptionalMap(stream, "httpupgradeSettings"); RejectUnknown(options, "host,path");
            outbound["transport"] = Object("type", "httpupgrade", "host", Text(options, "host"), "path", Text(options, "path", "/")); return;
        }
        throw new NotSupportedException("транспорт требует совместимого ядра: " + SafeToken(network));
    }

    private static VpnProfile Profile(Dictionary<string, object> outbound, string name, string format)
    {
        string json = Json().Serialize(outbound), type = Text(outbound, "type");
        string hash;
        using (var sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(json)));
        var tls = OptionalMap(outbound, "tls"); var reality = OptionalMap(tls, "reality");
        var transport = OptionalMap(outbound, "transport");
        return new VpnProfile { Protocol = type, Server = Text(outbound, "server"), Port = Port(outbound, "server_port"), Name = name,
            ProviderName = name, PortableOutboundJson = json, Source = "import:" + format + ":" + hash,
            UserId = Text(outbound, "uuid"), Password = Text(outbound, "password"), Method = Text(outbound, "method"),
            Security = tls.Count == 0 ? "none" : reality.Count > 0 ? "reality" : tls.ContainsKey("insecure") && Boolean(tls["insecure"]) ? "insecure" : "tls",
            Sni = Text(tls, "server_name"), PublicKey = Text(reality, "public_key"), ShortId = Text(reality, "short_id"),
            Transport = Text(transport, "type", "tcp"), Path = Text(transport, "path", Text(transport, "service_name")) };
    }

    private static object ReadYaml(string text)
    {
        var parser = new Parser(new StringReader(text)); int depth = 0, events = 0;
        while (parser.MoveNext())
        {
            if (++events > 100000) throw new FormatException("Слишком много элементов YAML.");
            if (parser.Current is MappingStart || parser.Current is SequenceStart) { if (++depth > 48) throw new FormatException("Слишком глубокая вложенность YAML."); }
            if (parser.Current is MappingEnd || parser.Current is SequenceEnd) depth--;
        }
        var yaml = new YamlStream();
        try { yaml.Load(new StringReader(text)); }
        catch (YamlException) { throw new FormatException("Некорректный YAML экспорта."); }
        if (yaml.Documents.Count != 1) throw new FormatException("Импортируйте один YAML-документ за раз.");
        int visited = 0, bytes = 0;
        return ConvertYaml(yaml.Documents[0].RootNode, 0, ref visited, ref bytes);
    }
    private static object ConvertYaml(YamlNode node, int depth, ref int visited, ref int bytes)
    {
        if (depth > 48 || ++visited > 100000 || bytes > 4 * 1024 * 1024) throw new FormatException("YAML содержит слишком много раскрытых ссылок или вложенных данных.");
        if (node is YamlScalarNode scalar)
        {
            string value = scalar.Value ?? ""; bytes += Encoding.UTF8.GetByteCount(value);
            if (bytes > 4 * 1024 * 1024) throw new FormatException("Раскрытый YAML больше 4 МБ.");
            return value;
        }
        if (node is YamlSequenceNode sequence)
        {
            var list = new List<object>(); foreach (var child in sequence.Children) list.Add(ConvertYaml(child, depth + 1, ref visited, ref bytes)); return list.ToArray();
        }
        if (node is YamlMappingNode mapping)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (!(pair.Key is YamlScalarNode key) || string.IsNullOrEmpty(key.Value) || map.ContainsKey(key.Value)) throw new FormatException("Некорректный или повторяющийся ключ YAML.");
                bytes += Encoding.UTF8.GetByteCount(key.Value); map.Add(key.Value, ConvertYaml(pair.Value, depth + 1, ref visited, ref bytes));
            }
            return map;
        }
        throw new FormatException("Неизвестная структура YAML.");
    }

    private static void RejectUnknown(Dictionary<string, object> map, string allowed)
    {
        var known = new HashSet<string>(allowed.Split(','), StringComparer.Ordinal);
        if (map.Keys.Any(k => !known.Contains(k))) throw new NotSupportedException("есть параметры, которые нельзя перенести без изменения смысла");
    }
    private static string SafeToken(string text) => new string((text ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(24).ToArray());
    private static Dictionary<string, object> Object(params object[] values) { var map = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) map.Add((string)values[i], values[i + 1]); return map; }
    private static Dictionary<string, object> Map(object value) => value as Dictionary<string, object> ?? throw new FormatException("Ожидался объект");
    private static object Value(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;
    private static Dictionary<string, object> OptionalMap(Dictionary<string, object> map, string key) => !map.TryGetValue(key, out var value) || value == null ? new Dictionary<string, object>() : Map(value);
    private static Dictionary<string, string> StringMap(object value) => Map(value).ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture));
    private static string Text(Dictionary<string, object> map, string key, string fallback = "") => map.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
    private static string Required(Dictionary<string, object> map, string key) => !string.IsNullOrWhiteSpace(Text(map, key)) ? Text(map, key) : throw new FormatException("Отсутствует обязательное поле");
    private static int Number(Dictionary<string, object> map, string key, int fallback) => !map.ContainsKey(key) ? fallback : int.TryParse(Text(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : throw new FormatException("Некорректное число");
    private static int Port(Dictionary<string, object> map, string key) { int value = Number(map, key, 0); return value > 0 && value <= 65535 ? value : throw new FormatException("Некорректный порт"); }
    private static bool Boolean(object value) => value is bool flag ? flag : bool.TryParse(Convert.ToString(value), out bool parsed) ? parsed : throw new FormatException("Некорректный логический параметр");
    private static IEnumerable<object> Items(object value) => value == null ? Enumerable.Empty<object>() : value is IEnumerable items && !(value is string) && !(value is IDictionary) ? items.Cast<object>() : new[] { value };
}

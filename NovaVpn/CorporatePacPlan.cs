using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace NovaVpn;

// Freeze before the first await, deriving the endpoint from the exact prepared
// config rather than rereading a profile after a subscription update.
public sealed class CorporatePacPlan
{
    private readonly CorporatePacQuery query;
    private readonly string tag;
    private readonly bool allowDirect;
    private CorporatePacPlan(CorporatePacQuery query, string tag, bool allowDirect)
    { this.query = query; this.tag = tag; this.allowDirect = allowDirect; }
    public static CorporatePacPlan Freeze(VpnProfile profile, string config, string primaryTag = "proxy")
    {
        var settings = profile?.CorporateProxy?.Copy();
        if (settings?.Enabled != true || settings.Kind != "pac") return null;
        settings.Validate();
        var root = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(config);
        var outbounds = ((IEnumerable)root["outbounds"]).Cast<object>().OfType<Dictionary<string, object>>().ToArray();
        var primary = outbounds.Where(o => o.TryGetValue("tag", out object value) && Equals(value, primaryTag)).Single();
        CorporateProxy.ValidateTcpOutbound(primary);
        if (!primary.TryGetValue("detour", out object detour) || !Equals(detour, primaryTag + "-corporate") ||
            !primary.TryGetValue("server", out object host) || !(host is string server) ||
            Uri.CheckHostName(server) == UriHostNameType.Unknown ||
            !primary.TryGetValue("server_port", out object number) || !int.TryParse(Convert.ToString(number), out int port) || port < 1 || port > 65535)
            throw new ArgumentException("PAC не удалось связать с TCP-сервером конфигурации.");
        bool tls = primary.TryGetValue("tls", out object raw) && raw is Dictionary<string, object> options &&
            options.TryGetValue("enabled", out object enabled) && Equals(enabled, true);
        return new CorporatePacPlan(new CorporatePacQuery(settings.PacScript, new UriBuilder(tls ? "https" : "http", server, port, "/").Uri.AbsoluteUri),
            primaryTag + "-corporate", settings.PacAllowDirect);
    }
    public string Prepare(string configPath) => CorporatePacHostSession.Prepare(configPath, query, tag, allowDirect);
}

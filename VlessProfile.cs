using System.Net;
using System.Text.Json.Nodes;

namespace QarttzVPN;

public enum ProfileKind { Vless, Hysteria2, Awg }

/// <summary>
/// Разбор vless:// и hysteria2:// ссылок.
/// vless → xray-outbound (reality и т.п.); hysteria2 → sing-box-outbound (ведётся напрямую через sing-box).
/// Имя профиля берётся из #fragment ссылки.
/// </summary>
public class VlessProfile
{
    public string Name = "";
    public string Raw = "";
    public string Host = "";
    public int Port;
    public string Uuid = "";          // для hysteria2 здесь лежит пароль авторизации (userinfo)
    public ProfileKind Kind = ProfileKind.Vless;
    public JsonObject? AwgEndpoint;   // для Awg: готовый sing-box `awg` endpoint
    private Dictionary<string, string> _q = new(StringComparer.OrdinalIgnoreCase);

    public static VlessProfile Parse(string link)
    {
        link = link.Trim();
        // AmneziaWG: ссылка vpn:// / awg:// (base64 INI) или INI-блок, вставленный напрямую.
        var ini = AwgConfig.FromLink(link);
        if (ini == null && AwgConfig.LooksLikeIni(link)) ini = link;
        if (ini != null) return FromAwgIni(ini, link);

        var uri = new Uri(link);
        var kind = uri.Scheme.ToLowerInvariant() switch
        {
            "vless" => ProfileKind.Vless,
            "hysteria2" or "hy2" => ProfileKind.Hysteria2,
            _ => throw new FormatException("ссылка должна быть vless:// или hysteria2://"),
        };
        var p = new VlessProfile
        {
            Raw = link,
            Kind = kind,
            Uuid = Uri.UnescapeDataString(uri.UserInfo),
            Host = uri.Host,
            Port = uri.Port,
        };
        p.Name = string.IsNullOrEmpty(uri.Fragment)
            ? $"{p.Host}:{p.Port}"
            : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        p._q = ParseQuery(uri.Query);
        if (string.IsNullOrEmpty(p.Uuid) || string.IsNullOrEmpty(p.Host) || p.Port <= 0)
            throw new FormatException("в ссылке нет uuid/host/port");
        return p;
    }

    /// <summary>AmneziaWG INI → профиль с готовым sing-box `awg` endpoint (ведётся напрямую через sing-box).</summary>
    private static VlessProfile FromAwgIni(string ini, string raw)
    {
        var ep = AwgConfig.ParseIni(ini, "awg")
            ?? throw new FormatException("не удалось разобрать конфиг AmneziaWG");
        var peer = (ep["peers"] as JsonArray) is { Count: > 0 } arr ? arr[0]!.AsObject() : null;
        return new VlessProfile
        {
            Raw = raw,
            Kind = ProfileKind.Awg,
            Host = (string?)peer?["address"] ?? "",
            Port = peer?["port"] is JsonNode pn ? (int)pn : 0,
            Name = "AmneziaWG",
            AwgEndpoint = ep,
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) d[Uri.UnescapeDataString(part)] = "";
            else d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return d;
    }

    private string Q(string k) => _q.TryGetValue(k, out var v) ? v : "";

    /// <summary>CIDR-адреса сервера (для обхода туннеля). Домен резолвится в IP.</summary>
    public IEnumerable<string> ServerCidrs()
    {
        if (IPAddress.TryParse(Host, out var ip))
        {
            yield return ip.ToString() + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32");
            yield break;
        }
        IPAddress[] addrs;
        try { addrs = Dns.GetHostAddresses(Host); } catch { yield break; }
        foreach (var a in addrs)
            yield return a.ToString() + (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32");
    }

    public JsonObject BuildXrayOutbound()
    {
        string net = Q("type"); if (net == "") net = "tcp";
        string sec = Q("security"); if (sec == "") sec = "none";
        string flow = Q("flow");
        if (net != "tcp") flow = ""; // flow (vision) только с raw tcp

        var stream = new JsonObject { ["network"] = net, ["security"] = sec };

        if (sec == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = Q("sni"),
                ["fingerprint"] = Q("fp") == "" ? "chrome" : Q("fp"),
                ["publicKey"] = Q("pbk"),
                ["shortId"] = Q("sid"),
                ["spiderX"] = Q("spx"),
            };
        }
        else if (sec == "tls")
        {
            var tls = new JsonObject
            {
                ["serverName"] = Q("sni"),
                ["fingerprint"] = Q("fp") == "" ? "chrome" : Q("fp"),
            };
            var alpn = Q("alpn");
            if (alpn != "")
            {
                var a = new JsonArray();
                foreach (var x in alpn.Split(',', StringSplitOptions.RemoveEmptyEntries)) a.Add(x);
                tls["alpn"] = a;
            }
            stream["tlsSettings"] = tls;
        }

        switch (net)
        {
            case "grpc":
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = Q("serviceName"),
                    ["multiMode"] = Q("mode") == "multi"
                };
                break;
            case "ws":
                stream["wsSettings"] = new JsonObject
                {
                    ["path"] = Q("path") == "" ? "/" : Q("path"),
                    ["headers"] = new JsonObject { ["Host"] = Q("host") }
                };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject
                {
                    ["path"] = Q("path") == "" ? "/" : Q("path"),
                    ["host"] = Q("host")
                };
                break;
            case "xhttp":
                stream["xhttpSettings"] = new JsonObject
                {
                    ["path"] = Q("path") == "" ? "/" : Q("path"),
                    ["host"] = Q("host"),
                    ["mode"] = Q("mode") == "" ? "auto" : Q("mode")
                };
                break;
            case "tcp":
            default:
                break; // raw tcp
        }

        var user = new JsonObject { ["id"] = Uuid, ["encryption"] = "none" };
        if (flow != "") user["flow"] = flow;

        return new JsonObject
        {
            ["protocol"] = "vless",
            ["tag"] = "proxy",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = Host,
                        ["port"] = Port,
                        ["users"] = new JsonArray { user }
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    /// <summary>sing-box hysteria2-outbound (ведётся напрямую через sing-box, без xray).</summary>
    public JsonObject BuildSingboxOutbound()
    {
        var tls = new JsonObject { ["enabled"] = true };
        var sni = Q("sni");
        if (sni != "") tls["server_name"] = sni;
        if (Q("insecure") == "1" || Q("allowInsecure") == "1") tls["insecure"] = true;

        var o = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = Host,
            ["server_port"] = Port,
            ["password"] = Uuid,
            ["tls"] = tls,
        };

        if (string.Equals(Q("obfs"), "salamander", StringComparison.OrdinalIgnoreCase))
        {
            o["obfs"] = new JsonObject
            {
                ["type"] = "salamander",
                ["password"] = Q("obfs-password"),
            };
        }
        return o;
    }
}

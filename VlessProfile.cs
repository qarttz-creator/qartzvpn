using System.Net;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// Разбор vless:// ссылки и генерация xray-outbound.
/// Имя профиля берётся из #fragment ссылки.
/// </summary>
public class VlessProfile
{
    public string Name = "";
    public string Raw = "";
    public string Host = "";
    public int Port;
    public string Uuid = "";
    private Dictionary<string, string> _q = new(StringComparer.OrdinalIgnoreCase);

    public static VlessProfile Parse(string link)
    {
        link = link.Trim();
        var uri = new Uri(link);
        if (!string.Equals(uri.Scheme, "vless", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("ссылка должна начинаться с vless://");
        var p = new VlessProfile
        {
            Raw = link,
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
}

using System.Net;
using System.Net.Http;

namespace QarttzVPN;

/// <summary>Tiny helper to tell whether the tunnel is up by probing the local SOCKS (machine-wide),
/// used by the GUI to show status when the connection is owned by the SYSTEM autostart task.</summary>
static class Net
{
    private const int SocksPort = 24808;

    public static bool TunnelUp()
    {
        try
        {
            using var http = new HttpClient(SocksHandler(TimeSpan.FromSeconds(4))) { Timeout = TimeSpan.FromSeconds(5) };
            var r = http.GetAsync("http://www.gstatic.com/generate_204").GetAwaiter().GetResult();
            return (int)r.StatusCode == 204 || r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static SocketsHttpHandler SocksHandler(TimeSpan connect) => new()
    {
        Proxy = new WebProxy($"socks5://127.0.0.1:{SocksPort}"),
        UseProxy = true,
        ConnectTimeout = connect,
        AllowAutoRedirect = false,
    };

    /// <summary>GET через локальный SOCKS (= через туннель). null, если 2xx не получен или тело — HTML.</summary>
    public static string? FetchViaTunnel(string url, int timeoutMs = 8000)
    {
        try
        {
            using var http = new HttpClient(SocksHandler(TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 6000))))
            { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var r = http.GetAsync(url).GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode) return null;
            var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var t = body.TrimStart();
            if (t.Length == 0 || t.StartsWith("<")) return null;   // HTML / captive-portal
            return body;
        }
        catch { return null; }
    }

    /// <summary>POST JSON через локальный SOCKS (= через туннель). Best-effort.</summary>
    public static void PostViaTunnel(string url, string json, int timeoutMs = 6000)
    {
        try
        {
            using var http = new HttpClient(SocksHandler(TimeSpan.FromMilliseconds(5000)))
            { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            _ = http.PostAsync(url, content).GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }
}

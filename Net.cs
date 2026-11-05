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
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{SocksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(4),
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var r = http.GetAsync("http://www.gstatic.com/generate_204").GetAwaiter().GetResult();
            return (int)r.StatusCode == 204 || r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

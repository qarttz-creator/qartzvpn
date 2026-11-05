using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>Posts a presence heartbeat to the control plane so the admin sees this client online.</summary>
static class Heartbeat
{
    public static void Send(string controlBase, string token, string version, string gateway)
    {
        if (string.IsNullOrEmpty(controlBase) || string.IsNullOrEmpty(token)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var body = new JsonObject
            {
                ["token"] = token,
                ["version"] = version,
                ["gateway"] = gateway ?? "",
                ["platform"] = "windows",
            }.ToJsonString();
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            http.PostAsync(controlBase.TrimEnd('/') + "/hb", content).GetAwaiter().GetResult();
        }
        catch { /* offline / DPI — ignore */ }
    }
}

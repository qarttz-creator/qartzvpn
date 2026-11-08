using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>Posts a presence heartbeat to the control plane so the admin sees this client online,
/// along with per-route health (which gateways are up) — ЧЕРЕЗ ТУННЕЛЬ, как и подписка.</summary>
static class Heartbeat
{
    public static void Send(string controlBase, string token, string version, string gateway,
                            IEnumerable<(string name, bool up)> routes)
    {
        if (string.IsNullOrEmpty(controlBase) || string.IsNullOrEmpty(token)) return;
        var routesArr = new JsonArray();
        foreach (var (name, up) in routes)
            routesArr.Add(new JsonObject { ["name"] = name, ["up"] = up });

        var body = new JsonObject
        {
            ["token"] = token,
            ["version"] = version,
            ["gateway"] = gateway ?? "",
            ["platform"] = "windows",
            ["routes"] = routesArr,
        }.ToJsonString();

        // по туннелю: /hb host обычно закрыт в открытом интернете на враждебной сети
        Net.PostViaTunnel(controlBase.TrimEnd('/') + "/hb", body);
    }
}

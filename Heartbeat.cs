using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>Posts a presence heartbeat to the control plane so the admin sees this client online,
/// along with per-route health (which gateways are up) — ЧЕРЕЗ ТУННЕЛЬ, как и подписка.</summary>
static class Heartbeat
{
    /// <summary>Отправляет хартбит и возвращает серверную эпоху ре-пулла (resub_at), либо 0, если
    /// сигнала/ответа нет. Вызывающий сравнивает её со своим resub_seen: серверная новее ⇒ перечитать /sub.</summary>
    public static long Send(string controlBase, string token, string version, string gateway,
                            IEnumerable<(string name, bool up)> routes)
    {
        if (string.IsNullOrEmpty(controlBase) || string.IsNullOrEmpty(token)) return 0;
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
        var resp = Net.PostViaTunnelResp(controlBase.TrimEnd('/') + "/hb", body);
        if (string.IsNullOrWhiteSpace(resp)) return 0;
        try
        {
            var node = JsonNode.Parse(resp);
            return node?["resub_at"]?.GetValue<long>() ?? 0;
        }
        catch { return 0; }
    }
}

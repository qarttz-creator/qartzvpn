using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// Настройки маршрутизации, хранятся в settings.json рядом с exe.
/// Routing — мастер-выключатель: если выключен, никакие фильтры не применяются
/// (весь трафик в туннель). IpFilter/AppsFilter работают только при Routing=true.
/// </summary>
public class Settings
{
    public bool Routing = true;
    public bool IpFilter = false;
    public bool AppsFilter = false;
    public bool DomainFilter = false; // route a user-defined list of domains straight out (bypass VPN)
    public bool RuDirect = true;   // route RU domains (.ru/.рф/.su) straight out, bypassing the VPN
    public string SubUrl = "";   // subscription link from the admin (https://…/sub?token=…)
    public string DeviceId = ""; // стабильный id установки (генерится один раз) — для учёта устройств в панели

    private static string PathOf => System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathOf))
            {
                var o = JsonNode.Parse(File.ReadAllText(PathOf))!.AsObject();
                return new Settings
                {
                    Routing    = (bool?)o["routing"]    ?? true,
                    IpFilter   = (bool?)o["ipFilter"]   ?? false,
                    AppsFilter = (bool?)o["appsFilter"] ?? false,
                    DomainFilter = (bool?)o["domainFilter"] ?? false,
                    RuDirect   = (bool?)o["ruDirect"]   ?? true,
                    SubUrl     = (string?)o["subUrl"]   ?? "",
                    DeviceId   = (string?)o["deviceId"] ?? "",
                };
            }
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        var o = new JsonObject
        {
            ["routing"]    = Routing,
            ["ipFilter"]   = IpFilter,
            ["appsFilter"] = AppsFilter,
            ["domainFilter"] = DomainFilter,
            ["ruDirect"]   = RuDirect,
            ["subUrl"]     = SubUrl,
            ["deviceId"]   = DeviceId,
        };
        try { File.WriteAllText(PathOf, o.ToJsonString(Json.Opts)); } catch { }
    }

    /// <summary>Стабильный id этой установки для учёта устройств в панели. Генерится один раз и
    /// сохраняется в settings.json; дальше не меняется (переживает смену IP/сети).</summary>
    public static string GetOrCreateDeviceId()
    {
        var s = Load();
        if (string.IsNullOrEmpty(s.DeviceId))
        {
            s.DeviceId = Guid.NewGuid().ToString("N");
            s.Save();
        }
        return s.DeviceId;
    }

    /// <summary>Token embedded in the subscription URL (?token=…), used for heartbeats.</summary>
    public string Token()
    {
        try
        {
            var q = new Uri(SubUrl).Query.TrimStart('?');
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.IndexOf('=');
                if (i > 0 && Uri.UnescapeDataString(part[..i]) == "token")
                    return Uri.UnescapeDataString(part[(i + 1)..]);
            }
        }
        catch { }
        return "";
    }

    /// <summary>Control-plane base URL (scheme://host:port) derived from the subscription URL.</summary>
    public string ControlBase()
    {
        try { var u = new Uri(SubUrl); return $"{u.Scheme}://{u.Authority}"; }
        catch { return ""; }
    }
}

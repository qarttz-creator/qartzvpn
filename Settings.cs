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
    public string SubUrl = "";   // subscription link from the admin (https://…/sub?token=…)

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
                    SubUrl     = (string?)o["subUrl"]   ?? "",
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
            ["subUrl"]     = SubUrl,
        };
        try { File.WriteAllText(PathOf, o.ToJsonString(Json.Opts)); } catch { }
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

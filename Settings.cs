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
        };
        try { File.WriteAllText(PathOf, o.ToJsonString(Json.Opts)); } catch { }
    }
}

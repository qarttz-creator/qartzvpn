using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// Настройки маршрутизации, хранятся в settings.json рядом с exe.
/// Routing — мастер-выключатель: если выключен, никакие фильтры не применяются
/// (весь трафик в туннель). IpFilter/AppsFilter работают только при Routing=true.
/// </summary>
public class Settings
{
    public bool Routing = false;
    public bool IpFilter = false;
    public bool AppsFilter = false;
    public bool DomainFilter = false; // route a user-defined list of domains straight out (bypass VPN)
    public bool RuDirect = false;   // route RU domains (.ru/.рф/.su) straight out, bypassing the VPN
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
                    Routing    = (bool?)o["routing"]    ?? false,
                    IpFilter   = (bool?)o["ipFilter"]   ?? false,
                    AppsFilter = (bool?)o["appsFilter"] ?? false,
                    DomainFilter = (bool?)o["domainFilter"] ?? false,
                    RuDirect   = (bool?)o["ruDirect"]   ?? false,
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

    /// <summary>Авто-миграция адреса центра (например, со старого 222 на Токио): если сервер в /hb
    /// отдал новый sub_base, переписываем scheme://host:port в SubUrl, сохраняя путь и ?token=…</summary>
    public static bool MigrateSubBase(string newBase)
    {
        if (string.IsNullOrWhiteSpace(newBase)) return false;
        try
        {
            var nb = new Uri(newBase.Trim().TrimEnd('/'));
            if (nb.Scheme != "http" && nb.Scheme != "https") return false;
            var st = Load();
            if (string.IsNullOrWhiteSpace(st.SubUrl)) return false;
            var cur = new Uri(st.SubUrl);
            var curBase = $"{cur.Scheme}://{cur.Authority}";
            var targetBase = $"{nb.Scheme}://{nb.Authority}";
            if (string.Equals(curBase, targetBase, StringComparison.OrdinalIgnoreCase)) return false;
            st.SubUrl = targetBase + cur.PathAndQuery;
            st.Save();
            return true;
        }
        catch { return false; }
    }
}

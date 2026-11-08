using System.Net.Http;

namespace QarttzVPN;

/// <summary>Fetches the subscription from the control plane and writes it to profiles.txt.</summary>
static class Subscription
{
    private static string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");

    public static string Fetch(string subUrl)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return http.GetStringAsync(subUrl).GetAwaiter().GetResult();
    }

    /// <summary>Fetch and save. Returns the count of parseable servers, or -1 on error (err set).</summary>
    public static int Refresh(string subUrl, out string err)
    {
        err = "";
        try
        {
            var body = Fetch(subUrl);
            return Save(body, out err);
        }
        catch (Exception ex) { err = ex.Message; return -1; }
    }

    /// <summary>
    /// Authoritative refresh ЧЕРЕЗ ТУННЕЛЬ (локальный SOCKS). /sub на враждебной сети часто закрыт в
    /// открытом интернете — весь смысл туннеля; по туннелю он достижим через выход. Возвращает число
    /// серверов или -1 (err задан). Требует уже поднятого туннеля.
    /// </summary>
    public static int RefreshViaTunnel(string subUrl, out string err)
    {
        err = "";
        var body = Net.FetchViaTunnel(subUrl);
        if (string.IsNullOrWhiteSpace(body)) { err = "нет ответа по туннелю"; return -1; }
        return Save(body, out err);
    }

    private static int Save(string body, out string err)
    {
        err = "";
        if (string.IsNullOrWhiteSpace(body)) { err = "пустой ответ"; return -1; }
        File.WriteAllText(ProfilesPath, body);
        int n = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            try { VlessProfile.Parse(line); n++; } catch { }
        }
        return n;
    }
}

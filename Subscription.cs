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
        // панель собирает /sub опросом всех шлюзов — 8с по умолчанию бывает мало, подписка молча не обновлялась
        var body = Net.FetchViaTunnel(subUrl, 30000);
        if (string.IsNullOrWhiteSpace(body)) { err = "нет ответа по туннелю"; return -1; }
        return Save(body, out err);
    }

    /// <summary>Умное обновление: если туннель поднят — сначала тянет по туннелю (с fallback напрямую),
    /// иначе — напрямую. Подходит для ручной кнопки и окна активации.</summary>
    public static int RefreshAuto(string subUrl, out string err)
    {
        if (Net.TunnelUp())
        {
            int n = RefreshViaTunnel(subUrl, out err);
            if (n > 0) return n;
            int direct = Refresh(subUrl, out var err2);
            if (direct > 0) { err = ""; return direct; }
            if (string.IsNullOrEmpty(err)) err = err2;
            return direct;
        }
        return Refresh(subUrl, out err);
    }

    private static int Save(string body, out string err)
    {
        err = "";
        if (string.IsNullOrWhiteSpace(body)) { err = "пустой ответ"; return -1; }
        int n = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            try { VlessProfile.Parse(line); n++; } catch { }
        }
        if (n == 0) { err = "в ответе нет рабочих маршрутов"; return 0; }
        try { File.WriteAllText(ProfilesPath, body); }
        catch (Exception ex) { err = "не удалось сохранить подписку: " + ex.Message; return -1; }
        return n;
    }
}

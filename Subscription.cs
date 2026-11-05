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
        catch (Exception ex) { err = ex.Message; return -1; }
    }
}

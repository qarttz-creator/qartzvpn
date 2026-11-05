namespace QarttzVPN;

/// <summary>Loads parsed server profiles from profiles.txt (shared by the GUI and the headless runner).</summary>
static class Profiles
{
    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "profiles.txt");

    public static List<VlessProfile> Load()
    {
        var list = new List<VlessProfile>();
        if (File.Exists(Path))
        {
            foreach (var raw in File.ReadAllLines(Path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { list.Add(VlessProfile.Parse(line)); }
                catch { /* skip bad line */ }
            }
        }
        return list;
    }
}

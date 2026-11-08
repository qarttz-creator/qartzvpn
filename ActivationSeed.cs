namespace QarttzVPN;

/// <summary>
/// Маршруты активации: ТЕКУЩИЕ входные маршруты, на которых клиент строит первый туннель, когда
/// своих серверов ещё нет (первая активация на заблокированной сети, где /sub напрямую недоступен).
/// Это те же шлюзы, что и в подписке — поднятие туннеля на них и есть проверка работоспособности,
/// после чего своя подписка тянется ПО ТУННЕЛЮ и клиент переключается на неё.
///
/// Порядок: кэш activation.txt рядом с exe (самообновляется по туннелю из /activation) → встроенный
/// в exe seed (assets/activation_seed.txt, запекается при сборке из /activation). Меняются маршруты —
/// выкатывается новая сборка, живые клиенты обновляют кэш сами.
/// </summary>
static class ActivationSeed
{
    private static string CachePath => Path.Combine(AppContext.BaseDirectory, "activation.txt");

    public static string Routes()
    {
        var cached = Filter(ReadCache());
        return cached.Length > 0 ? cached : Filter(ReadSeed());
    }

    public static bool HasAny() => Routes().Length > 0;

    public static void SaveCache(string text)
    {
        try { File.WriteAllText(CachePath, text); } catch { }
    }

    private static string ReadCache()
    {
        try { return File.Exists(CachePath) ? File.ReadAllText(CachePath) : ""; }
        catch { return ""; }
    }

    private static string ReadSeed()
    {
        try
        {
            var asm = typeof(ActivationSeed).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("activation_seed.txt", StringComparison.OrdinalIgnoreCase));
            if (name == null) return "";
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return ""; }
    }

    private static string Filter(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"));
        return string.Join("\n", lines);
    }
}

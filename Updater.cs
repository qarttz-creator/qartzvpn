using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// In-app updater. Reads version.json from the rolling desktop-latest release, compares with this
/// build's version, and (if newer) downloads the installer and runs it. The installer has a fixed
/// AppId, so it upgrades in place and keeps settings/activation.
/// </summary>
static class Updater
{
    private const string VersionUrl =
        "https://github.com/qarttz-creator/qartzvpn/releases/download/desktop-latest/version.json";

    public record Remote(string Version, string Url, string Notes, bool Critical, string MinSupported);

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static Remote? Fetch()
    {
        // GitHub (где лежит version.json) на враждебной сети часто закрыт напрямую — та же беда, что
        // и с подпиской. Если туннель поднят, читаем ПО ТУННЕЛЮ (через выход), иначе — напрямую.
        string? text = null;
        if (Net.TunnelUp()) text = Net.FetchViaTunnel(VersionUrl, 12000);
        if (string.IsNullOrWhiteSpace(text)) text = FetchDirect(VersionUrl);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var o = JsonNode.Parse(text)!.AsObject();
            var ver = (string?)o["version"] ?? "";
            var url = (string?)o["url"] ?? "";
            if (ver.Length == 0 || url.Length == 0) return null;
            var notes = (string?)o["notes"] ?? "";
            var critical = (bool?)o["critical"] ?? false;
            var minSup = (string?)o["min_supported"] ?? "";
            return new Remote(ver, url, notes, critical, minSup);
        }
        catch { return null; }
    }

    /// <summary>Обновление обязательно: помечено critical, либо текущая версия ниже min_supported
    /// (ретайрим сломанные сборки). Тогда в диалоге не будет «Позже».</summary>
    public static bool IsForced(Remote r)
    {
        if (r.Critical) return true;
        if (!string.IsNullOrWhiteSpace(r.MinSupported)
            && TryVer(r.MinSupported, out var min) && TryVer(CurrentVersion, out var cur))
            return cur < min;
        return false;
    }

    private static string? FetchDirect(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    public static bool IsNewer(Remote r) =>
        TryVer(r.Version, out var rv) && TryVer(CurrentVersion, out var cv) && rv > cv;

    private static bool TryVer(string s, out Version v)
    {
        var core = new string((s ?? "").TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int maj = 0, min = 0, bld = 0;
        if (parts.Length > 0) int.TryParse(parts[0], out maj);
        if (parts.Length > 1) int.TryParse(parts[1], out min);
        if (parts.Length > 2) int.TryParse(parts[2], out bld);
        v = new Version(maj, min, bld);
        return true;
    }

    public static string? Download(Remote r)
    {
        try
        {
            // Как и манифест — качаем установщик по туннелю, если он поднят, иначе напрямую.
            byte[]? bytes = null;
            if (Net.TunnelUp()) bytes = Net.DownloadViaTunnel(r.Url);
            if (bytes == null) bytes = DownloadDirect(r.Url);
            if (bytes == null) return null;
            var path = Path.Combine(Path.GetTempPath(), "QarttzVPN-Setup.exe");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch { return null; }
    }

    private static byte[]? DownloadDirect(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    /// <summary>Launch the installer (UAC elevated) and quit so the running exe unlocks for upgrade.
    /// silent=true (для critical) запускает Inno-установщик без мастера: /VERYSILENT /NORESTART.</summary>
    public static void RunInstallerAndExit(string setupPath, bool silent = false)
    {
        var psi = new ProcessStartInfo { FileName = setupPath, UseShellExecute = true };
        if (silent) psi.Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
        Process.Start(psi);
        Application.Exit();
    }
}

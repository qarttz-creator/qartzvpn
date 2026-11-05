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

    public record Remote(string Version, string Url);

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
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var text = http.GetStringAsync(VersionUrl).GetAwaiter().GetResult();
            var o = JsonNode.Parse(text)!.AsObject();
            var ver = (string?)o["version"] ?? "";
            var url = (string?)o["url"] ?? "";
            if (ver.Length == 0 || url.Length == 0) return null;
            return new Remote(ver, url);
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
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = http.GetByteArrayAsync(r.Url).GetAwaiter().GetResult();
            var path = Path.Combine(Path.GetTempPath(), "QarttzVPN-Setup.exe");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch { return null; }
    }

    /// <summary>Launch the installer (UAC elevated) and quit so the running exe unlocks for upgrade.</summary>
    public static void RunInstallerAndExit(string setupPath)
    {
        Process.Start(new ProcessStartInfo { FileName = setupPath, UseShellExecute = true });
        Application.Exit();
    }
}

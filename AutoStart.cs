using System.Diagnostics;

namespace QarttzVPN;

/// <summary>
/// Windows autostart via a Scheduled Task that runs at boot as SYSTEM with highest privileges and
/// launches the headless connector (QarttzVPN.exe --autoconnect). This is the only way to bring the
/// VPN up automatically for a standard (non-admin) domain user: the task runs elevated, machine-wide,
/// before/independent of login. Creating/removing it needs admin (the configuring GUI runs elevated).
/// </summary>
static class AutoStart
{
    private const string TaskName = "QartzVPN Autostart";

    private static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "QarttzVPN.exe");

    private static int Sch(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    public static bool IsEnabled() => Sch("/query", "/tn", TaskName) == 0;

    public static bool Enable()
    {
        // /tr value = the quoted exe path plus the headless flag, as one argument.
        var action = $"\"{ExePath}\" --autoconnect";
        int rc = Sch("/create", "/tn", TaskName, "/tr", action,
                     "/sc", "onstart", "/ru", "SYSTEM", "/rl", "highest", "/f");
        if (rc == 0) Sch("/run", "/tn", TaskName); // connect now too, not only next boot
        return rc == 0;
    }

    public static bool Disable()
    {
        Sch("/end", "/tn", TaskName);
        return Sch("/delete", "/tn", TaskName, "/f") == 0;
    }
}

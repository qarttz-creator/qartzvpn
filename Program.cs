namespace QarttzVPN;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Headless mode, launched by the SYSTEM autostart scheduled task: just connect and stay alive.
        if (args.Length > 0 && args[0] == "--autoconnect")
        {
            RunHeadless();
            return;
        }

        // Один экземпляр (только для GUI).
        using var mutex = new Mutex(true, "QarttzVPN_SingleInstance", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void RunHeadless()
    {
        var mgr = new SingBoxManager();
        var profiles = Profiles.Load();
        if (profiles.Count > 0) mgr.StartAuto(profiles);

        // Heartbeat so autostart clients also show online in the admin.
        var st = Settings.Load();
        using var hb = new System.Threading.Timer(
            _ => Heartbeat.Send(st.ControlBase(), st.Token(), Updater.CurrentVersion, mgr.ActiveName ?? ""),
            null, 15000, 60000);

        // Keep the process alive until the task is ended or Windows shuts down.
        // SingBoxManager hooks ProcessExit to stop the cores cleanly.
        new ManualResetEvent(false).WaitOne();
    }
}

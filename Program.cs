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
        var st = Settings.Load();

        var profiles = Profiles.Load();
        // своих серверов ещё нет, но подписка задана → поднимаемся на маршрутах активации (bootstrap)
        if (profiles.Count == 0 && !string.IsNullOrWhiteSpace(st.SubUrl))
            foreach (var raw in ActivationSeed.Routes().Split('\n'))
            { var l = raw.Trim(); if (l.Length == 0) continue; try { profiles.Add(VlessProfile.Parse(l)); } catch { } }

        if (profiles.Count > 0) mgr.StartAuto(profiles);

        // Heartbeat (с health маршрутов) so autostart clients also show online in the admin.
        using var hb = new System.Threading.Timer(
            _ => Heartbeat.Send(st.ControlBase(), st.Token(), Updater.CurrentVersion, mgr.ActiveName ?? "", mgr.RouteHealth()),
            null, 15000, 60000);

        // Как только туннель встал — тянем свою подписку ПО ТУННЕЛЮ и переключаемся на свои маршруты.
        bool pulled = false;
        using var sub = new System.Threading.Timer(_ =>
        {
            if (pulled || string.IsNullOrWhiteSpace(st.SubUrl) || !Net.TunnelUp()) return;
            pulled = true;
            var before = SafeReadProfiles();
            int n = Subscription.RefreshViaTunnel(st.SubUrl, out _);
            var act = Net.FetchViaTunnel(st.ControlBase() + "/activation");
            if (!string.IsNullOrWhiteSpace(act)) ActivationSeed.SaveCache(act);
            if (n > 0 && SafeReadProfiles().Trim() != before.Trim())
                mgr.StartAuto(Profiles.Load());   // с активации на свои маршруты
        }, null, 8000, 15000);

        // Keep the process alive until the task is ended or Windows shuts down.
        // SingBoxManager hooks ProcessExit to stop the cores cleanly.
        new ManualResetEvent(false).WaitOne();
    }

    private static string SafeReadProfiles()
    {
        try
        {
            var p = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles.txt");
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch { return ""; }
    }
}

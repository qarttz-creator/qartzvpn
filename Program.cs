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

        long resubSeen = 0;
        var subGate = new object();
        bool pulled = false;
        DateTime lastPullUtc = DateTime.MinValue;

        void PullSub(string reason)
        {
            lock (subGate)
            {
                var curSt = Settings.Load();
                if (string.IsNullOrWhiteSpace(curSt.SubUrl) || !Net.TunnelUp()) return;
                var before = NormalizeLines(SafeReadProfiles());
                int n = Subscription.RefreshViaTunnel(curSt.SubUrl, out var err);
                var act = Net.FetchViaTunnel(curSt.ControlBase() + "/activation");
                if (!string.IsNullOrWhiteSpace(act)) ActivationSeed.SaveCache(act);
                if (n <= 0)
                {
                    mgr.LogMessage($"автозапуск: /sub ({reason}) не обновился ({(string.IsNullOrEmpty(err) ? "0 маршрутов" : err)}).");
                    return;
                }
                pulled = true;
                lastPullUtc = DateTime.UtcNow;
                var after = NormalizeLines(SafeReadProfiles());
                if (after != before)
                {
                    mgr.LogMessage($"автозапуск: подписка обновлена ({n} маршрутов, {reason}) — применяю.");
                    mgr.ApplyUpdatedProfiles(Profiles.Load());
                }
            }
        }

        // Heartbeat (с health маршрутов + push resub_at + авто-миграция sub_base).
        using var hb = new System.Threading.Timer(_ =>
        {
            var curSt = Settings.Load();
            long resubAt = Heartbeat.Send(curSt.ControlBase(), curSt.Token(), Settings.GetOrCreateDeviceId(),
                                          Environment.MachineName, Updater.CurrentVersion, mgr.ActiveName ?? "",
                                          mgr.RouteHealth(), mgr.LogMessage);
            if (resubAt <= 0) return;
            if (resubSeen == 0) { resubSeen = resubAt; return; }
            if (resubAt > resubSeen)
            {
                resubSeen = resubAt;
                Task.Run(() => PullSub("push resub_at"));
            }
        }, null, 15000, 60000);

        // Как только туннель встал — тянем свою подписку ПО ТУННЕЛЮ (при ошибке повторяем) и далее раз в 6ч.
        using var sub = new System.Threading.Timer(_ =>
        {
            if (pulled && DateTime.UtcNow - lastPullUtc < TimeSpan.FromHours(6)) return;
            PullSub(pulled ? "6h" : "start");
        }, null, 8000, 30000);

        // Keep the process alive until the task is ended or Windows shuts down.
        // SingBoxManager hooks ProcessExit to stop the cores cleanly.
        new ManualResetEvent(false).WaitOne();
    }

    private static string NormalizeLines(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .OrderBy(l => l, StringComparer.Ordinal);
        return string.Join("\n", lines);
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

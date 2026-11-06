using System.Diagnostics;

namespace QarttzVPN;

public class MainForm : Form
{
    private readonly SingBoxManager _mgr = new();
    private readonly PictureBox _power = new();
    private readonly PictureBox _gear = new();
    private readonly Label _status = new();
    private readonly NotifyIcon _tray = new();
    private readonly List<VlessProfile> _profiles = new();
    private bool _connected;
    private System.Threading.Timer? _heartbeat;
    private bool _autostartMode;
    private System.Windows.Forms.Timer? _poll;

    /// <summary>Lets the settings dialog tear down the GUI's own tunnel before enabling the SYSTEM
    /// autostart task, so the two don't fight over the TUN / port 24808.</summary>
    public static Action? StopActiveTunnel;

    private readonly Image _imgIdle = Img("power_idle.png");
    private readonly Image _imgActive = Img("power_active.png");

    private static string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");

    private static Icon? AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch { return null; }
    }

    private static Image Img(string file)
    {
        var asm = typeof(MainForm).Assembly;
        var name = asm.GetManifestResourceNames()
                      .First(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return Image.FromStream(s);
    }

    public MainForm()
    {
        Text = "QartzVPN";
        ClientSize = new Size(360, 440);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(241, 241, 237);
        BackgroundImage = Img("background.png");
        BackgroundImageLayout = ImageLayout.Stretch;
        var appIcon = AppIcon();
        if (appIcon != null) Icon = appIcon;

        // шестерёнка — настройки (справа вверху)
        _gear.Image = Img("gear.png");
        _gear.SizeMode = PictureBoxSizeMode.Zoom;
        _gear.Size = new Size(34, 34);
        _gear.Location = new Point(ClientSize.Width - 48, 14);
        _gear.BackColor = Color.Transparent;
        _gear.Cursor = Cursors.Hand;
        _gear.Click += (_, _) => OpenSettings();
        var tip = new ToolTip();
        tip.SetToolTip(_gear, "Настройки");

        _status.Text = "Отключено";
        _status.ForeColor = Color.FromArgb(109, 112, 118);
        _status.BackColor = Color.Transparent;
        _status.Font = new Font("Segoe UI", 13);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Location = new Point(30, 200);
        _status.Size = new Size(300, 30);

        _power.Image = _imgIdle;
        _power.SizeMode = PictureBoxSizeMode.Zoom;
        _power.Size = new Size(104, 104);
        _power.Location = new Point((ClientSize.Width - 104) / 2, 250);
        _power.BackColor = Color.Transparent;
        _power.Cursor = Cursors.Hand;
        _power.Click += (_, _) => Toggle();

        Controls.AddRange(new Control[] { _gear, _status, _power });

        _tray.Text = "QartzVPN";
        _tray.Icon = appIcon ?? SystemIcons.Shield;
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать окно", null, (_, _) => ShowWindow());
        menu.Items.Add("Подключить / Отключить", null, (_, _) => Toggle());
        menu.Items.Add("Проверить обновление", null, (_, _) => CheckForUpdate(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _mgr.StateChanged += OnState;
        _mgr.Status += OnStatus;
        _mgr.Log += s => Debug.WriteLine(s);
        StopActiveTunnel = () => { try { _mgr.Stop(); } catch { } };

        FormClosing += OnClosing;
        Shown += OnShown;

        LoadProfiles();
    }

    private void OnShown(object? sender, EventArgs e)
    {
        var st = Settings.Load();
        if (string.IsNullOrWhiteSpace(st.SubUrl) || _profiles.Count == 0)
            RunActivation();

        SilentSubRefresh();
        CheckForUpdate(manual: false);
        RefreshAutostartMode();
    }

    private void RunActivation()
    {
        using var f = new ActivationForm();
        f.ShowDialog(this);
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        _profiles.Clear();
        if (File.Exists(ProfilesPath))
        {
            foreach (var raw in File.ReadAllLines(ProfilesPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { _profiles.Add(VlessProfile.Parse(line)); }
                catch { /* пропускаем битую строку */ }
            }
        }

        if (_connected) return;
        if (_profiles.Count > 0)
        {
            _power.Enabled = true; _power.Cursor = Cursors.Hand;
            if (_status.Text.StartsWith("Нет ") || _status.Text.StartsWith("Активируйте"))
            { _status.Text = "Отключено"; _status.ForeColor = Color.FromArgb(109, 112, 118); }
        }
        else
        {
            _status.Text = "Активируйте приложение (шестерёнка)";
            _status.ForeColor = Color.FromArgb(154, 107, 22);
            _power.Enabled = false; _power.Cursor = Cursors.Default;
        }
    }

    private void OpenSettings()
    {
        using (var f = new SettingsForm()) f.ShowDialog(this);
        LoadProfiles(); // подписка/фильтры могли измениться
        RefreshAutostartMode();
    }

    private void Toggle()
    {
        if (_autostartMode)
        {
            Info("VPN управляется автозапуском (поднимается с Windows).\n" +
                 "Чтобы управлять вручную, отключите «Автозапуск» в настройках.");
            return;
        }
        try
        {
            if (_connected || _mgr.IsRunning)
            {
                _mgr.Stop();
                SetUi(false);
            }
            else
            {
                if (_profiles.Count == 0)
                {
                    RunActivation();
                    if (_profiles.Count == 0) return;
                }
                SetUi(null);
                _mgr.StartAuto(_profiles);   // всегда авто: перебор всех шлюзов + failover
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "QartzVPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetUi(false);
        }
    }

    private void OnState(bool connected)
    {
        if (IsHandleCreated) BeginInvoke(() => SetUi(connected));
    }

    private void OnStatus(string s)
    {
        if (IsHandleCreated) BeginInvoke(() => { _status.Text = s; });
    }

    private void SetUi(bool? connected)
    {
        if (connected is null)
        {
            _status.Text = "Подключение…";
            _status.ForeColor = Color.FromArgb(154, 107, 22);
            _power.Enabled = false;
            _power.Cursor = Cursors.Default;
            return;
        }

        _connected = connected.Value;
        _power.Enabled = true;
        _power.Cursor = Cursors.Hand;

        if (_connected)
        {
            _power.Image = _imgActive;
            _status.Text = "Подключено";
            _status.ForeColor = Color.FromArgb(30, 110, 114);
            _tray.Text = "QartzVPN — подключено";
            StartHeartbeat();
        }
        else
        {
            _power.Image = _imgIdle;
            _status.Text = "Отключено";
            _status.ForeColor = Color.FromArgb(109, 112, 118);
            _tray.Text = "QartzVPN — отключено";
            StopHeartbeat();
        }
    }

    // ---------- подписка / хартбит ----------

    private void SilentSubRefresh()
    {
        var st = Settings.Load();
        if (string.IsNullOrWhiteSpace(st.SubUrl)) return;
        Task.Run(() =>
        {
            int n = Subscription.Refresh(st.SubUrl, out _);
            if (n > 0 && IsHandleCreated) BeginInvoke(() => { if (!_connected) LoadProfiles(); });
        });
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        SendHeartbeat();
        _heartbeat = new System.Threading.Timer(_ => SendHeartbeat(), null, 60000, 60000);
    }

    private void StopHeartbeat()
    {
        try { _heartbeat?.Dispose(); } catch { }
        _heartbeat = null;
    }

    private void SendHeartbeat()
    {
        var st = Settings.Load();
        Heartbeat.Send(st.ControlBase(), st.Token(), Updater.CurrentVersion, _mgr.ActiveName ?? "");
    }

    // ---------- обновление ----------

    private void CheckForUpdate(bool manual)
    {
        Task.Run(() =>
        {
            var r = Updater.Fetch();
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (r == null) { if (manual) Info("Не удалось проверить обновление."); return; }
                if (Updater.IsNewer(r)) PromptUpdate(r);
                else if (manual) Info($"Установлена последняя версия ({Updater.CurrentVersion}).");
            });
        });
    }

    private void PromptUpdate(Updater.Remote r)
    {
        var ans = MessageBox.Show(
            $"Доступна новая версия: {r.Version}\nТекущая: {Updater.CurrentVersion}\n\nОбновить сейчас?",
            "Обновление QartzVPN", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (ans != DialogResult.Yes) return;

        _status.Text = "Загрузка обновления…"; _status.ForeColor = Color.FromArgb(154, 107, 22);
        Task.Run(() =>
        {
            var path = Updater.Download(r);
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (path == null) { Info("Не удалось скачать обновление."); return; }
                _mgr.Stop();
                Updater.RunInstallerAndExit(path);
            });
        });
    }

    private static void Info(string msg) => MessageBox.Show(msg, "QartzVPN");

    // ---------- режим автозапуска (подключением владеет системная задача) ----------

    private void RefreshAutostartMode()
    {
        _autostartMode = AutoStart.IsEnabled();
        if (_autostartMode)
        {
            try { _mgr.Stop(); } catch { } // connection is owned by the SYSTEM task — don't run our own
            _power.Enabled = false; _power.Cursor = Cursors.Default;
            _status.Text = "Автозапуск — проверка…"; _status.ForeColor = Color.FromArgb(154, 107, 22);
            StartPoll();
        }
        else
        {
            StopPoll();
            if (!_connected) { _power.Enabled = _profiles.Count > 0; _power.Cursor = _profiles.Count > 0 ? Cursors.Hand : Cursors.Default; }
        }
    }

    private void StartPoll()
    {
        StopPoll();
        _poll = new System.Windows.Forms.Timer { Interval = 5000 };
        _poll.Tick += (_, _) => PollOnce();
        _poll.Start();
        PollOnce();
    }

    private void StopPoll()
    {
        try { _poll?.Stop(); _poll?.Dispose(); } catch { }
        _poll = null;
    }

    private void PollOnce()
    {
        Task.Run(() =>
        {
            bool up = Net.TunnelUp();
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (!_autostartMode) return;
                if (up)
                {
                    _power.Image = _imgActive;
                    _status.Text = "Подключено (автозапуск)";
                    _status.ForeColor = Color.FromArgb(30, 110, 114);
                }
                else
                {
                    _power.Image = _imgIdle;
                    _status.Text = "Поднимается (автозапуск)…";
                    _status.ForeColor = Color.FromArgb(154, 107, 22);
                }
            });
        });
    }

    // ---------- окно / выход ----------

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private void OnClosing(object? s, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
    }

    private void ExitApp()
    {
        StopPoll();
        StopHeartbeat();
        _mgr.Stop();
        _tray.Visible = false;
        Environment.Exit(0);
    }
}

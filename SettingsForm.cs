namespace QarttzVPN;

public class SettingsForm : Form
{
    private readonly Settings _st = Settings.Load();

    private readonly CheckBox _routing = new();
    private readonly CheckBox _ipF = new();
    private readonly CheckBox _appF = new();
    private readonly CheckBox _autostart = new();
    private readonly Button _ipEdit = new();
    private readonly Button _appEdit = new();
    private bool _initAuto;

    private static string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");
    private static string ExcludePath  => Path.Combine(AppContext.BaseDirectory, "exclude.txt");
    private static string AppsPath     => Path.Combine(AppContext.BaseDirectory, "apps.txt");

    private static readonly Color Bg   = Color.FromArgb(22, 24, 36);
    private static readonly Color Pan  = Color.FromArgb(34, 37, 54);
    private static readonly Color Fg   = Color.Gainsboro;

    public SettingsForm()
    {
        Text = "Настройки — QartzVPN";
        ClientSize = new Size(440, 486);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg;

        // --- Подписка / обновление ---
        var hProf = Header("Подписка", 16);
        var pInfo = new Label
        {
            Text = "Серверы приходят по ссылке активации от администратора. Выбор шлюза автоматический.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(20, 40), AutoSize = false, Size = new Size(400, 34),
            Font = new Font("Segoe UI", 8)
        };
        var pEdit = FlatButton("Сменить подписку…", new Point(20, 74), 195);
        pEdit.Click += (_, _) => ChangeSub();
        var pUpd = FlatButton("Проверить обновление", new Point(223, 74), 197);
        pUpd.Click += (_, _) => CheckUpdate();

        // --- Маршрутизация ---
        var hRoute = Header("Маршрутизация", 122);
        _routing.Text = "Включить фильтрацию трафика";
        _routing.ForeColor = Fg; _routing.BackColor = Color.Transparent;
        _routing.Font = new Font("Segoe UI", 10);
        _routing.Location = new Point(18, 148); _routing.AutoSize = true;
        _routing.Checked = _st.Routing;
        _routing.CheckedChanged += (_, _) => Sync();

        var rHint = new Label
        {
            Text = "Выключено — весь трафик идёт в туннель (кроме самого VPN-сервера). "
                 + "Включите, чтобы задать исключения.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(40, 172), Size = new Size(370, 34), Font = new Font("Segoe UI", 8)
        };

        // IP-фильтр
        _ipF.Text = "Фильтр по IP";
        _ipF.ForeColor = Fg; _ipF.BackColor = Color.Transparent; _ipF.Font = new Font("Segoe UI", 9.5f);
        _ipF.Location = new Point(40, 210); _ipF.AutoSize = true;
        _ipF.Checked = _st.IpFilter;
        var ipSub = Sub("адреса и подсети мимо туннеля", 232);
        _ipEdit = FlatButton("Список IP…", new Point(285, 210), 135);
        _ipEdit.Click += (_, _) => EditExcludes();

        // Фильтр приложений
        _appF.Text = "Фильтр по приложениям";
        _appF.ForeColor = Fg; _appF.BackColor = Color.Transparent; _appF.Font = new Font("Segoe UI", 9.5f);
        _appF.Location = new Point(40, 258); _appF.AutoSize = true;
        _appF.Checked = _st.AppsFilter;
        var appSub = Sub("выбранные .exe идут напрямую (игры и т.п.)", 280);
        _appEdit = FlatButton("Список приложений…", new Point(285, 258), 135);
        _appEdit.Click += (_, _) => EditApps();

        // --- Автозапуск ---
        var hAuto = Header("Автозапуск", 314);
        _autostart.Text = "Запускать с Windows и подключаться (для всех)";
        _autostart.ForeColor = Fg; _autostart.BackColor = Color.Transparent;
        _autostart.Font = new Font("Segoe UI", 9.5f);
        _autostart.Location = new Point(18, 340); _autostart.AutoSize = true;
        _autostart.Checked = AutoStart.IsEnabled();
        _autostart.CheckedChanged += (_, _) => ToggleAutostart();
        var autoSub = new Label
        {
            Text = "Поднимается при старте Windows под системной учётной записью — "
                 + "работает и без прав администратора у пользователя. Нужны права админа для включения.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(40, 362), Size = new Size(385, 44), Font = new Font("Segoe UI", 8)
        };

        var applyHint = new Label
        {
            Text = "Изменения применяются при следующем подключении.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(20, 412), Size = new Size(400, 20), Font = new Font("Segoe UI", 8)
        };

        var save = FlatButton("Сохранить", new Point(228, 440), 95);
        save.BackColor = Color.FromArgb(0, 120, 215); save.ForeColor = Color.White;
        save.Click += (_, _) => { SaveState(); Close(); };
        var cancel = FlatButton("Отмена", new Point(333, 440), 85);
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] {
            hProf, pInfo, pEdit, pUpd, hRoute, _routing, rHint,
            _ipF, ipSub, _ipEdit, _appF, appSub, _appEdit,
            hAuto, _autostart, autoSub, applyHint, save, cancel
        });

        Sync();
    }

    private void Sync()
    {
        bool r = _routing.Checked;
        _ipF.Enabled = r; _appF.Enabled = r;
        _ipEdit.Enabled = r; _appEdit.Enabled = r;
        _ipF.ForeColor = r ? Fg : Color.Gray;
        _appF.ForeColor = r ? Fg : Color.Gray;
    }

    private void ToggleAutostart()
    {
        if (_initAuto) return;
        Cursor = Cursors.WaitCursor;
        bool ok = _autostart.Checked ? AutoStart.Enable() : AutoStart.Disable();
        Cursor = Cursors.Default;
        if (!ok)
        {
            MessageBox.Show("Не удалось изменить автозапуск. Нужны права администратора.", "QartzVPN");
            _initAuto = true; _autostart.Checked = AutoStart.IsEnabled(); _initAuto = false;
            return;
        }
        if (_autostart.Checked)
            MessageBox.Show(
                "Автозапуск включён. VPN будет подниматься при старте Windows для всех пользователей.\n\n" +
                "Серверы берутся из активированной подписки — убедитесь, что приложение активировано.",
                "QartzVPN");
    }

    private void SaveState()
    {
        _st.Routing = _routing.Checked;
        _st.IpFilter = _ipF.Checked;
        _st.AppsFilter = _appF.Checked;
        _st.Save();
    }

    private Label Header(string text, int y) => new()
    {
        Text = text, ForeColor = Color.FromArgb(150, 180, 255), BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        Location = new Point(18, y), AutoSize = true
    };

    private Label Sub(string text, int y) => new()
    {
        Text = text, ForeColor = Color.Gray, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 8), Location = new Point(40, y), Size = new Size(235, 18)
    };

    private Button FlatButton(string text, Point loc, int w)
    {
        var b = new Button
        {
            Text = text, Location = loc, Size = new Size(w, 30),
            FlatStyle = FlatStyle.Flat, ForeColor = Fg, BackColor = Pan,
            Font = new Font("Segoe UI", 9)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 100);
        return b;
    }

    // ---------- редакторы списков ----------

    private TextBox MakeEditor(Form f, string text)
    {
        var tb = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true,
            Font = new Font("Consolas", 10), Location = new Point(10, 10),
            Size = new Size(f.ClientSize.Width - 20, f.ClientSize.Height - 90),
            BackColor = Color.FromArgb(28, 30, 44), ForeColor = Color.White
        };
        tb.Text = text;
        return tb;
    }

    private void ChangeSub()
    {
        using var f = new ActivationForm();
        f.ShowDialog(this);
    }

    private void CheckUpdate()
    {
        Cursor = Cursors.WaitCursor;
        var r = Updater.Fetch();
        Cursor = Cursors.Default;
        if (r == null) { MessageBox.Show("Не удалось проверить обновление.", "QartzVPN"); return; }
        if (!Updater.IsNewer(r))
        { MessageBox.Show($"Установлена последняя версия ({Updater.CurrentVersion}).", "QartzVPN"); return; }

        var ans = MessageBox.Show(
            $"Доступна новая версия: {r.Version}\nТекущая: {Updater.CurrentVersion}\n\nОбновить сейчас?",
            "Обновление QartzVPN", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (ans != DialogResult.Yes) return;

        Cursor = Cursors.WaitCursor;
        var path = Updater.Download(r);
        Cursor = Cursors.Default;
        if (path == null) { MessageBox.Show("Не удалось скачать обновление.", "QartzVPN"); return; }
        Updater.RunInstallerAndExit(path);
    }

    private void EditExcludes()
    {
        var f = EditorForm("Исключения по IP — адреса/подсети мимо туннеля");
        var tb = MakeEditor(f, File.Exists(ExcludePath) ? File.ReadAllText(ExcludePath) : "");
        var hint = EditorHint(f, "IP или подсеть, по одной на строку: 10.0.0.0/8, 192.168.1.0/24 "
                              + "или один адрес 192.168.1.50. Комментарий — после #.");
        var save = EditorSave(f);
        save.Click += (_, _) =>
        {
            var bad = new List<string>(); int n = 0;
            foreach (var raw in tb.Lines)
            {
                n++; var line = raw;
                var h = line.IndexOf('#'); if (h >= 0) line = line.Substring(0, h);
                line = line.Trim(); if (line.Length == 0) continue;
                var test = line.Contains('/') ? line : line + (line.Contains(':') ? "/128" : "/32");
                if (!System.Net.IPNetwork.TryParse(test, out _)) bad.Add($"строка {n}: {Trunc(raw.Trim())}");
            }
            if (bad.Count > 0) { Warn("Проверка исключений",
                "Не сохранено — это не похоже на IP или подсеть:\n\n" + string.Join("\n", bad) +
                "\n\nПример: 10.0.0.0/8, 192.168.1.0/24, 192.168.1.50"); return; }
            Write(ExcludePath, tb.Text); f.Close();
        };
        f.Controls.AddRange(new Control[] { tb, hint, save });
        f.ShowDialog(this);
    }

    private void EditApps()
    {
        var f = EditorForm("Приложения мимо туннеля — имя .exe на строку");
        var tb = MakeEditor(f, File.Exists(AppsPath) ? File.ReadAllText(AppsPath) : "");
        var hint = EditorHint(f, "Трафик этих программ идёт напрямую, мимо VPN (для игр и т.п.). "
                              + "Имя файла без пути, напр. Discord.exe. Комментарий — после #.");
        var save = EditorSave(f);
        save.Click += (_, _) =>
        {
            var bad = new List<string>(); int n = 0;
            foreach (var raw in tb.Lines)
            {
                n++; var line = raw;
                var h = line.IndexOf('#'); if (h >= 0) line = line.Substring(0, h);
                line = line.Trim(); if (line.Length == 0) continue;
                var ok = !line.Contains('\\') && !line.Contains('/')
                         && line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (!ok) bad.Add($"строка {n}: {Trunc(raw.Trim())}");
            }
            if (bad.Count > 0) { Warn("Проверка приложений",
                "Не сохранено — укажите имя .exe без пути:\n\n" + string.Join("\n", bad) +
                "\n\nПример: Discord.exe, cs2.exe"); return; }
            Write(AppsPath, tb.Text); f.Close();
        };
        f.Controls.AddRange(new Control[] { tb, hint, save });
        f.ShowDialog(this);
    }

    // ---------- мелкие хелперы редактора ----------

    private Form EditorForm(string title) => new()
    {
        Text = title, ClientSize = new Size(520, 430),
        StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
        MaximizeBox = false, MinimizeBox = false, BackColor = Bg
    };

    private Label EditorHint(Form f, string text) => new()
    {
        Text = text, ForeColor = Color.Gray, BackColor = Color.Transparent,
        Location = new Point(12, f.ClientSize.Height - 72), Size = new Size(360, 60),
        Font = new Font("Segoe UI", 8)
    };

    private Button EditorSave(Form f)
    {
        var b = new Button
        {
            Text = "Сохранить", Size = new Size(130, 32),
            Location = new Point(f.ClientSize.Width - 142, f.ClientSize.Height - 44),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(0, 120, 215)
        };
        return b;
    }

    private static string Trunc(string s) => s.Length > 52 ? s.Substring(0, 52) + "…" : s;
    private static void Warn(string title, string msg)
        => MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private static void Write(string path, string text)
    {
        try { File.WriteAllText(path, text); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
}

namespace QarttzVPN;

public class SettingsForm : Form
{
    private readonly Settings _st = Settings.Load();

    private readonly CheckBox _routing = new();
    private readonly CheckBox _ipF = new();
    private readonly CheckBox _appF = new();
    private readonly Button _ipEdit = new();
    private readonly Button _appEdit = new();

    private static string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");
    private static string ExcludePath  => Path.Combine(AppContext.BaseDirectory, "exclude.txt");
    private static string AppsPath     => Path.Combine(AppContext.BaseDirectory, "apps.txt");

    private static readonly Color Bg   = Color.FromArgb(22, 24, 36);
    private static readonly Color Pan  = Color.FromArgb(34, 37, 54);
    private static readonly Color Fg   = Color.Gainsboro;

    public SettingsForm()
    {
        Text = "Настройки — QartzVPN";
        ClientSize = new Size(430, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg;

        // --- Профили ---
        var hProf = Header("Серверы", 16);
        var pInfo = new Label
        {
            Text = "Список серверов (vless-ссылки). Выбор сервера — в главном окне.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(20, 40), AutoSize = false, Size = new Size(300, 34),
            Font = new Font("Segoe UI", 8)
        };
        var pEdit = FlatButton("Изменить список серверов…", new Point(20, 74), 200);
        pEdit.Click += (_, _) => EditProfiles();

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
        _ipF.Text = "Фильтр по IP (адреса/подсети мимо туннеля)";
        _ipF.ForeColor = Fg; _ipF.BackColor = Color.Transparent; _ipF.Font = new Font("Segoe UI", 9.5f);
        _ipF.Location = new Point(40, 212); _ipF.AutoSize = true;
        _ipF.Checked = _st.IpFilter;
        _ipEdit = FlatButton("Список IP…", new Point(300, 208), 110);
        _ipEdit.Click += (_, _) => EditExcludes();

        // Фильтр приложений
        _appF.Text = "Фильтр по приложениям (exe мимо туннеля)";
        _appF.ForeColor = Fg; _appF.BackColor = Color.Transparent; _appF.Font = new Font("Segoe UI", 9.5f);
        _appF.Location = new Point(40, 250); _appF.AutoSize = true;
        _appF.Checked = _st.AppsFilter;
        _appEdit = FlatButton("Список приложений…", new Point(300, 246), 110);
        _appEdit.Click += (_, _) => EditApps();

        var applyHint = new Label
        {
            Text = "Изменения применяются при следующем подключении.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(20, 296), Size = new Size(390, 20), Font = new Font("Segoe UI", 8)
        };

        var save = FlatButton("Сохранить", new Point(220, 326), 95);
        save.BackColor = Color.FromArgb(0, 120, 215); save.ForeColor = Color.White;
        save.Click += (_, _) => { SaveState(); Close(); };
        var cancel = FlatButton("Отмена", new Point(325, 326), 85);
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] {
            hProf, pInfo, pEdit, hRoute, _routing, rHint,
            _ipF, _ipEdit, _appF, _appEdit, applyHint, save, cancel
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

    private void EditProfiles()
    {
        var f = EditorForm("Серверы — по одной vless:// ссылке на строку");
        var tb = MakeEditor(f, File.Exists(ProfilesPath) ? File.ReadAllText(ProfilesPath) : "");
        var hint = EditorHint(f, "Вставьте vless:// ссылку из 3x-ui (вкладка «Ссылки»). "
                              + "Имя — из части после # в конце. Комментарий — строкой с # в начале.");
        var save = EditorSave(f);
        save.Click += (_, _) =>
        {
            var bad = new List<string>(); int n = 0;
            foreach (var raw in tb.Lines)
            {
                n++; var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { VlessProfile.Parse(line); }
                catch { bad.Add($"строка {n}: {Trunc(line)}"); }
            }
            if (bad.Count > 0) { Warn("Проверка профилей",
                "Не сохранено — это не похоже на vless:// ссылку:\n\n" + string.Join("\n", bad) +
                "\n\nФормат: vless://UUID@HOST:PORT?...#ИмяСервера"); return; }
            Write(ProfilesPath, tb.Text); f.Close();
        };
        f.Controls.AddRange(new Control[] { tb, hint, save });
        f.ShowDialog(this);
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

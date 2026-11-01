using System.Diagnostics;

namespace QarttzVPN;

public class MainForm : Form
{
    private readonly SingBoxManager _mgr = new();
    private readonly ComboBox _combo = new();
    private readonly Button _btn = new();
    private readonly Button _prof = new();
    private readonly Button _excl = new();
    private readonly Button _apps = new();
    private readonly Label _status = new();
    private readonly NotifyIcon _tray = new();
    private readonly List<VlessProfile> _profiles = new();
    private bool _connected;

    private string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");
    private string ExcludePath  => Path.Combine(AppContext.BaseDirectory, "exclude.txt");
    private string AppsPath     => Path.Combine(AppContext.BaseDirectory, "apps.txt");

    public MainForm()
    {
        Text = "Qarttz VPN";
        ClientSize = new Size(360, 300);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 28);

        var lbl = new Label
        {
            Text = "Сервер:",
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9),
            Location = new Point(20, 14),
            AutoSize = true
        };

        _combo.DropDownStyle = ComboBoxStyle.DropDownList;
        _combo.Location = new Point(20, 36);
        _combo.Width = 320;
        _combo.Font = new Font("Segoe UI", 10);

        _status.Text = "Отключено";
        _status.ForeColor = Color.Gainsboro;
        _status.Font = new Font("Segoe UI", 12);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Location = new Point(20, 78);
        _status.Size = new Size(320, 34);

        _btn.Text = "Подключить";
        _btn.Font = new Font("Segoe UI", 14, FontStyle.Bold);
        _btn.Size = new Size(240, 58);
        _btn.Location = new Point((ClientSize.Width - 240) / 2, 120);
        _btn.FlatStyle = FlatStyle.Flat;
        _btn.FlatAppearance.BorderSize = 0;
        _btn.ForeColor = Color.White;
        _btn.BackColor = Color.FromArgb(0, 120, 215);
        _btn.Click += (_, _) => Toggle();

        void StyleCfgButton(Button b, string text, int y, Action onClick)
        {
            b.Text = text;
            b.Font = new Font("Segoe UI", 9);
            b.Size = new Size(240, 30);
            b.Location = new Point((ClientSize.Width - 240) / 2, y);
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.Gainsboro;
            b.BackColor = Color.FromArgb(45, 45, 52);
            b.Click += (_, _) => onClick();
        }
        StyleCfgButton(_prof, "Профили…", 194, EditProfiles);
        StyleCfgButton(_excl, "Исключения по IP…", 228, EditExcludes);
        StyleCfgButton(_apps, "Приложения мимо туннеля…", 262, EditApps);

        Controls.AddRange(new Control[] { lbl, _combo, _status, _btn, _prof, _excl, _apps });

        _tray.Text = "Qarttz VPN";
        _tray.Icon = SystemIcons.Shield;
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать окно", null, (_, _) => ShowWindow());
        menu.Items.Add("Подключить / Отключить", null, (_, _) => Toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _mgr.StateChanged += OnState;
        _mgr.Log += s => Debug.WriteLine(s);

        FormClosing += OnClosing;

        LoadProfiles();
    }

    private void LoadProfiles()
    {
        _profiles.Clear();
        _combo.Items.Clear();
        if (File.Exists(ProfilesPath))
        {
            foreach (var raw in File.ReadAllLines(ProfilesPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try
                {
                    var p = VlessProfile.Parse(line);
                    _profiles.Add(p);
                    _combo.Items.Add(p.Name);
                }
                catch { /* пропускаем битую строку */ }
            }
        }
        if (_combo.Items.Count > 0)
        {
            _combo.SelectedIndex = 0;
            _btn.Enabled = true;
        }
        else
        {
            _status.Text = "Нет профилей (profiles.txt)";
            _btn.Enabled = false;
        }
    }

    private void Toggle()
    {
        try
        {
            if (_connected || _mgr.IsRunning)
            {
                _mgr.Stop();
                SetUi(false);
            }
            else
            {
                if (_combo.SelectedIndex < 0 || _combo.SelectedIndex >= _profiles.Count)
                {
                    MessageBox.Show("Выберите сервер.", "Qarttz VPN");
                    return;
                }
                SetUi(null);
                _mgr.Start(_profiles[_combo.SelectedIndex]);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Qarttz VPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetUi(false);
        }
    }

    private void OnState(bool connected)
    {
        if (IsHandleCreated) BeginInvoke(() => SetUi(connected));
    }

    private void SetUi(bool? connected)
    {
        if (connected is null)
        {
            _status.Text = "Подключение…";
            _status.ForeColor = Color.Khaki;
            _btn.Enabled = false;
            _combo.Enabled = false;
            return;
        }

        _connected = connected.Value;
        _btn.Enabled = true;

        if (_connected)
        {
            _status.Text = "Подключено";
            _status.ForeColor = Color.LightGreen;
            _btn.Text = "Отключить";
            _btn.BackColor = Color.FromArgb(200, 60, 60);
            _combo.Enabled = false;
            _tray.Text = "Qarttz VPN — подключено";
        }
        else
        {
            _status.Text = "Отключено";
            _status.ForeColor = Color.Gainsboro;
            _btn.Text = "Подключить";
            _btn.BackColor = Color.FromArgb(0, 120, 215);
            _combo.Enabled = true;
            _tray.Text = "Qarttz VPN — отключено";
        }
    }

    private void EditProfiles()
    {
        var f = new Form
        {
            Text = "Профили серверов — по одной vless:// ссылке на строку",
            ClientSize = new Size(560, 420),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var tb = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsReturn = true,
            Font = new Font("Consolas", 9),
            Location = new Point(10, 10),
            Size = new Size(540, 340)
        };
        tb.Text = File.Exists(ProfilesPath) ? File.ReadAllText(ProfilesPath) : "";
        var hint = new Label
        {
            Text = "Вставьте vless:// ссылку из 3x-ui (вкладка «Ссылки»). Имя в списке — "
                 + "из части после # в конце ссылки. Строки, начинающиеся с #, — комментарии.",
            AutoSize = false,
            ForeColor = Color.Gray,
            Location = new Point(12, 356),
            Size = new Size(400, 54)
        };
        var save = new Button
        {
            Text = "Сохранить",
            Size = new Size(140, 34),
            Location = new Point(410, 372)
        };
        save.Click += (_, _) =>
        {
            var bad = new List<string>();
            int n = 0;
            foreach (var raw in tb.Lines)
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { VlessProfile.Parse(line); }
                catch { bad.Add($"строка {n}: {(line.Length > 48 ? line.Substring(0, 48) + "…" : line)}"); }
            }
            if (bad.Count > 0)
            {
                MessageBox.Show(
                    "Не сохранено — это не похоже на vless:// ссылку:\n\n" + string.Join("\n", bad) +
                    "\n\nВставьте ссылку целиком из 3x-ui (вкладка «Ссылки»), вида\n" +
                    "vless://UUID@HOST:PORT?...#ИмяСервера.\nКомментарии — строкой, начинающейся с #.",
                    "Проверка профилей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var keep = _combo.SelectedItem as string;
                File.WriteAllText(ProfilesPath, tb.Text);
                LoadProfiles();
                if (keep != null)
                {
                    var i = _combo.Items.IndexOf(keep);
                    if (i >= 0) _combo.SelectedIndex = i;
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            f.Close();
        };
        f.Controls.AddRange(new Control[] { tb, hint, save });
        f.ShowDialog(this);
    }

    private void EditApps()
    {
        var f = new Form
        {
            Text = "Приложения мимо туннеля — по одному имени .exe на строку",
            ClientSize = new Size(480, 420),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var tb = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsReturn = true,
            Font = new Font("Consolas", 10),
            Location = new Point(10, 10),
            Size = new Size(460, 330)
        };
        tb.Text = File.Exists(AppsPath) ? File.ReadAllText(AppsPath) : "";
        var hint = new Label
        {
            Text = "Трафик этих программ пойдёт НАПРЯМУЮ, мимо VPN (для игр и т.п., у которых "
                 + "адреса неизвестны). Пишите имя исполняемого файла, напр. Discord.exe. "
                 + "Комментарий — после #.",
            AutoSize = false,
            ForeColor = Color.Gray,
            Location = new Point(12, 346),
            Size = new Size(320, 64)
        };
        var save = new Button
        {
            Text = "Сохранить",
            Size = new Size(140, 34),
            Location = new Point(330, 372)
        };
        save.Click += (_, _) =>
        {
            var bad = new List<string>();
            int n = 0;
            foreach (var raw in tb.Lines)
            {
                n++;
                var line = raw;
                var h = line.IndexOf('#');
                if (h >= 0) line = line.Substring(0, h);
                line = line.Trim();
                if (line.Length == 0) continue;
                var okName = !line.Contains('\\') && !line.Contains('/')
                             && line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (!okName) bad.Add($"строка {n}: {raw.Trim()}");
            }
            if (bad.Count > 0)
            {
                MessageBox.Show(
                    "Не сохранено — укажите имя исполняемого файла (.exe), без пути:\n\n" +
                    string.Join("\n", bad) +
                    "\n\nПример: Discord.exe  или  game.exe",
                    "Проверка приложений", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { File.WriteAllText(AppsPath, tb.Text); } catch (Exception ex) { MessageBox.Show(ex.Message); }
            f.Close();
        };
        f.Controls.AddRange(new Control[] { tb, hint, save });
        f.ShowDialog(this);
    }

    private void EditExcludes()
    {
        var f = new Form
        {
            Text = "Исключения — адреса мимо туннеля (IP или CIDR, по одному на строку)",
            ClientSize = new Size(480, 420),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var tb = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsReturn = true,
            Font = new Font("Consolas", 10),
            Location = new Point(10, 10),
            Size = new Size(460, 350)
        };
        tb.Text = File.Exists(ExcludePath) ? File.ReadAllText(ExcludePath) : "";
        var save = new Button
        {
            Text = "Сохранить",
            Size = new Size(140, 34),
            Location = new Point(330, 372)
        };
        save.Click += (_, _) =>
        {
            var bad = new List<string>();
            int n = 0;
            foreach (var raw in tb.Lines)
            {
                n++;
                var line = raw;
                var h = line.IndexOf('#');
                if (h >= 0) line = line.Substring(0, h);
                line = line.Trim();
                if (line.Length == 0) continue;
                var test = line.Contains('/') ? line : line + (line.Contains(':') ? "/128" : "/32");
                if (!System.Net.IPNetwork.TryParse(test, out _))
                    bad.Add($"строка {n}: {raw.Trim()}");
            }
            if (bad.Count > 0)
            {
                MessageBox.Show(
                    "Не сохранено — это не похоже на IP или подсеть:\n\n" + string.Join("\n", bad) +
                    "\n\nВерный формат: 10.0.0.0/8, 192.168.1.0/24 или один адрес 192.168.1.50.\n" +
                    "Комментарий можно писать после #.",
                    "Проверка исключений", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { File.WriteAllText(ExcludePath, tb.Text); } catch (Exception ex) { MessageBox.Show(ex.Message); }
            f.Close();
        };
        var hint = new Label
        {
            Text = "Применится при следующем подключении.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(12, 382)
        };
        f.Controls.AddRange(new Control[] { tb, save, hint });
        f.ShowDialog(this);
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private void OnClosing(object? s, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
    }

    private void ExitApp()
    {
        _mgr.Stop();
        _tray.Visible = false;
        Environment.Exit(0);
    }
}

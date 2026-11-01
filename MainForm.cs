using System.Diagnostics;

namespace QarttzVPN;

public class MainForm : Form
{
    private readonly SingBoxManager _mgr = new();
    private readonly ComboBox _combo = new();
    private readonly Button _btn = new();
    private readonly Button _excl = new();
    private readonly Label _status = new();
    private readonly NotifyIcon _tray = new();
    private readonly List<VlessProfile> _profiles = new();
    private bool _connected;

    private string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");
    private string ExcludePath  => Path.Combine(AppContext.BaseDirectory, "exclude.txt");

    public MainForm()
    {
        Text = "Qarttz VPN";
        ClientSize = new Size(360, 250);
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

        _excl.Text = "Исключения (IP мимо туннеля)…";
        _excl.Font = new Font("Segoe UI", 9);
        _excl.Size = new Size(240, 30);
        _excl.Location = new Point((ClientSize.Width - 240) / 2, 194);
        _excl.FlatStyle = FlatStyle.Flat;
        _excl.ForeColor = Color.Gainsboro;
        _excl.BackColor = Color.FromArgb(45, 45, 52);
        _excl.Click += (_, _) => EditExcludes();

        Controls.AddRange(new Control[] { lbl, _combo, _status, _btn, _excl });

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

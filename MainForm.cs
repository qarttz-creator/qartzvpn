using System.Diagnostics;

namespace QarttzVPN;

public class MainForm : Form
{
    private readonly SingBoxManager _mgr = new();
    private readonly ComboBox _combo = new();
    private readonly PictureBox _power = new();
    private readonly PictureBox _gear = new();
    private readonly Label _status = new();
    private readonly NotifyIcon _tray = new();
    private readonly List<VlessProfile> _profiles = new();
    private bool _connected;

    private readonly Image _imgIdle = Img("power_idle.png");
    private readonly Image _imgActive = Img("power_active.png");

    private static string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "profiles.txt");

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
        BackColor = Color.FromArgb(10, 12, 24);
        BackgroundImage = Img("background.png");
        BackgroundImageLayout = ImageLayout.Stretch;
        try { Icon = Icon.FromHandle(((Bitmap)_imgActive).GetHicon()); } catch { }

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

        var lbl = new Label
        {
            Text = "Сервер",
            ForeColor = Color.FromArgb(205, 214, 240),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9),
            Location = new Point(30, 150),
            AutoSize = true
        };

        _combo.DropDownStyle = ComboBoxStyle.DropDownList;
        _combo.FlatStyle = FlatStyle.Flat;
        _combo.Location = new Point(30, 172);
        _combo.Width = 300;
        _combo.Font = new Font("Segoe UI", 10);
        _combo.BackColor = Color.FromArgb(28, 32, 54);
        _combo.ForeColor = Color.White;

        _status.Text = "Отключено";
        _status.ForeColor = Color.Gainsboro;
        _status.BackColor = Color.Transparent;
        _status.Font = new Font("Segoe UI", 13);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Location = new Point(30, 222);
        _status.Size = new Size(300, 30);

        _power.Image = _imgIdle;
        _power.SizeMode = PictureBoxSizeMode.Zoom;
        _power.Size = new Size(104, 104);
        _power.Location = new Point((ClientSize.Width - 104) / 2, 258);
        _power.BackColor = Color.Transparent;
        _power.Cursor = Cursors.Hand;
        _power.Click += (_, _) => Toggle();

        Controls.AddRange(new Control[] { _gear, lbl, _combo, _status, _power });

        _tray.Text = "QartzVPN";
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
        var keep = _combo.SelectedItem as string;
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
            var i = keep != null ? _combo.Items.IndexOf(keep) : -1;
            _combo.SelectedIndex = i >= 0 ? i : 0;
            if (!_connected) { _power.Enabled = true; _power.Cursor = Cursors.Hand; }
        }
        else
        {
            _status.Text = "Нет профилей — откройте настройки";
            _status.ForeColor = Color.Khaki;
            _power.Enabled = false;
            _power.Cursor = Cursors.Default;
        }
    }

    private void OpenSettings()
    {
        using (var f = new SettingsForm()) f.ShowDialog(this);
        LoadProfiles(); // профили могли измениться
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
                    MessageBox.Show("Сначала добавьте сервер в настройках.", "QartzVPN");
                    return;
                }
                SetUi(null);
                _mgr.Start(_profiles[_combo.SelectedIndex]);
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

    private void SetUi(bool? connected)
    {
        if (connected is null)
        {
            _status.Text = "Подключение…";
            _status.ForeColor = Color.Khaki;
            _power.Enabled = false;
            _power.Cursor = Cursors.Default;
            _combo.Enabled = false;
            return;
        }

        _connected = connected.Value;
        _power.Enabled = true;
        _power.Cursor = Cursors.Hand;

        if (_connected)
        {
            _power.Image = _imgActive;
            _status.Text = "Подключено";
            _status.ForeColor = Color.FromArgb(120, 230, 170);
            _combo.Enabled = false;
            _tray.Text = "QartzVPN — подключено";
        }
        else
        {
            _power.Image = _imgIdle;
            _status.Text = "Отключено";
            _status.ForeColor = Color.Gainsboro;
            _combo.Enabled = true;
            _tray.Text = "QartzVPN — отключено";
        }
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

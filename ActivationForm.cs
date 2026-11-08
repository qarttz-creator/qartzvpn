namespace QarttzVPN;

/// <summary>First-run / "change subscription" screen: paste the admin's link, pull the servers.</summary>
public class ActivationForm : Form
{
    public bool Activated;

    private readonly TextBox _input = new();
    private readonly Button _btn = new();
    private readonly Label _status = new();

    public ActivationForm()
    {
        Text = "Активация — QartzVPN";
        ClientSize = new Size(460, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(22, 24, 36);

        var title = new Label
        {
            Text = "Вставьте ссылку активации",
            ForeColor = Color.FromArgb(150, 180, 255), BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Location = new Point(20, 18), AutoSize = true,
        };
        var hint = new Label
        {
            Text = "Ссылку выдаёт администратор. После активации серверы подтянутся автоматически.",
            ForeColor = Color.Gray, BackColor = Color.Transparent,
            Location = new Point(20, 50), Size = new Size(420, 34), Font = new Font("Segoe UI", 8),
        };

        _input.Location = new Point(20, 92); _input.Width = 420; _input.Font = new Font("Segoe UI", 10);
        _input.BackColor = Color.FromArgb(28, 30, 44); _input.ForeColor = Color.White;
        _input.BorderStyle = BorderStyle.FixedSingle;
        _input.Text = Settings.Load().SubUrl;

        _btn.Text = "Активировать"; _btn.Location = new Point(20, 132); _btn.Size = new Size(150, 34);
        _btn.FlatStyle = FlatStyle.Flat; _btn.BackColor = Color.FromArgb(0, 120, 215); _btn.ForeColor = Color.White;
        _btn.Click += (_, _) => Activate();

        _status.Location = new Point(20, 180); _status.Size = new Size(420, 34);
        _status.ForeColor = Color.Gainsboro; _status.BackColor = Color.Transparent;
        _status.Font = new Font("Segoe UI", 8);

        Controls.AddRange(new Control[] { title, hint, _input, _btn, _status });
    }

    private void Activate()
    {
        var url = _input.Text.Trim();
        if (url.Length == 0) { _status.Text = "Вставьте ссылку"; return; }
        if (!(url.StartsWith("http://") || url.StartsWith("https://")))
        { _status.Text = "Ссылка должна начинаться с http:// или https://"; return; }

        _btn.Enabled = false; _status.Text = "Активация…";
        Task.Run(() =>
        {
            int n = Subscription.Refresh(url, out var err);
            BeginInvoke(() =>
            {
                if (n > 0)
                {
                    var st = Settings.Load(); st.SubUrl = url; st.Save();
                    Activated = true; Close(); return;
                }
                // Прямой доступ к /sub закрыт (заблокированная сеть) — но если есть запечённые маршруты
                // активации, всё равно активируем: подключимся на них, а своя подписка подтянется по
                // туннелю после подключения. Это и есть «подписка там, где нет прямого доступа».
                if (ActivationSeed.HasAny())
                {
                    var st = Settings.Load(); st.SubUrl = url; st.Save();
                    _status.Text = "Подписка напрямую недоступна — активируем через туннель…";
                    Activated = true; Close(); return;
                }
                _status.Text = n < 0 ? "Не удалось: " + err : "Ссылка открылась, но серверов нет";
                _btn.Enabled = true;
            });
        });
    }
}

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
        BackColor = Color.FromArgb(241, 241, 237);

        var title = new Label
        {
            Text = "Вставьте ссылку активации",
            ForeColor = Color.FromArgb(30, 110, 114), BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Location = new Point(20, 18), AutoSize = true,
        };
        var hint = new Label
        {
            Text = "Ссылку выдаёт администратор. После активации серверы подтянутся автоматически.",
            ForeColor = Color.FromArgb(109, 112, 118), BackColor = Color.Transparent,
            Location = new Point(20, 50), Size = new Size(420, 34), Font = new Font("Segoe UI", 8),
        };

        _input.Location = new Point(20, 92); _input.Width = 420; _input.Font = new Font("Segoe UI", 10);
        _input.BackColor = Color.FromArgb(255, 255, 255); _input.ForeColor = Color.FromArgb(24, 25, 27);
        _input.BorderStyle = BorderStyle.FixedSingle;
        _input.Text = Settings.Load().SubUrl;

        _btn.Text = "Активировать"; _btn.Location = new Point(20, 132); _btn.Size = new Size(150, 34);
        _btn.FlatStyle = FlatStyle.Flat; _btn.BackColor = Color.FromArgb(30, 110, 114); _btn.ForeColor = Color.White;
        _btn.Click += (_, _) => Activate();

        _status.Location = new Point(20, 180); _status.Size = new Size(420, 34);
        _status.ForeColor = Color.FromArgb(109, 112, 118); _status.BackColor = Color.Transparent;
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
                if (n < 0) { _status.Text = "Не удалось: " + err; _btn.Enabled = true; return; }
                if (n == 0) { _status.Text = "Ссылка открылась, но серверов нет"; _btn.Enabled = true; return; }
                var st = Settings.Load(); st.SubUrl = url; st.Save();
                Activated = true;
                Close();
            });
        });
    }
}

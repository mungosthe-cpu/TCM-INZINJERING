using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace TcmInzenjering.Setup;

internal sealed class InstallerForm : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progress;
    private readonly Button _closeButton;
    private readonly PictureBox _logoBox;

    public InstallerForm()
    {
        Text = "TCM-INŽINJERING — Instalacija";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 480);
        BackColor = Color.FromArgb(12, 28, 56);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        _logoBox = new PictureBox
        {
            Dock = DockStyle.Top,
            Height = 340,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(12, 28, 56)
        };

        var logo = LoadLogo();
        if (logo is not null)
        {
            _logoBox.Image = logo;
            try
            {
                Icon = Icon.FromHandle(((Bitmap)logo).GetHicon());
            }
            catch
            {
                // Icon nije kritičan.
            }
        }

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 12, 20, 12),
            BackColor = Color.FromArgb(8, 20, 40)
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Text = "Priprema instalacije…",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 22,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        _closeButton = new Button
        {
            Text = "Zatvori",
            Dock = DockStyle.Right,
            Width = 110,
            Height = 32,
            Enabled = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 140, 200),
            ForeColor = Color.White
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.Click += (_, _) => Close();

        var buttonRow = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40
        };
        buttonRow.Controls.Add(_closeButton);

        footer.Controls.Add(buttonRow);
        footer.Controls.Add(_progress);
        footer.Controls.Add(_statusLabel);

        Controls.Add(footer);
        Controls.Add(_logoBox);
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text));
            return;
        }

        _statusLabel.Text = text;
        Application.DoEvents();
    }

    public void SetProgress(int percent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(percent));
            return;
        }

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Value = Math.Clamp(percent, 0, 100);
        Application.DoEvents();
    }

    public void Complete(bool success, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Complete(success, message));
            return;
        }

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = success ? 100 : _progress.Value;
        _statusLabel.Text = message;
        _statusLabel.ForeColor = success ? Color.FromArgb(140, 230, 180) : Color.FromArgb(255, 160, 140);
        _closeButton.Enabled = true;
        AcceptButton = _closeButton;
    }

    private static Image? LoadLogo()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "TCM Logo.png"),
            Path.Combine(baseDir, "payload", "TCM Logo.png"),
            Path.Combine(baseDir, "Icons", "TCM Logo.png"),
            Path.Combine(baseDir, "payload", "TcmInzenjering.bundle", "Contents", "net8", "Icons", "TCM Logo.png")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                // Ne zaključavaj fajl — učitaj u memoriju.
                using var fs = File.OpenRead(path);
                return Image.FromStream(fs);
            }
            catch
            {
                // Probaj sledeći.
            }
        }

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("TCM Logo.png", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    return Image.FromStream(stream);
                }
            }
        }
        catch
        {
            // Bez logoa — tamna pozadina (boja forme), ne crni flash.
        }

        return null;
    }
}

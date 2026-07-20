using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace TcmInzenjering.Setup;

internal sealed class InstallerForm : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progress;
    private readonly Button _closeButton;
    private readonly Panel _overlay;

    public InstallerForm()
    {
        Text = "TCM-ROADS — Instalacija";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 480);
        // Isti ton kao donji deo brand slike — vidljiv samo ako logo nije ucitan.
        BackColor = Color.FromArgb(8, 28, 72);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        DoubleBuffered = true;

        var logo = LoadLogo();
        if (logo is not null)
        {
            // BackgroundImage preko CELOG klijentskog prostora (uklj. ispod status overlay-a).
            BackgroundImage = logo;
            BackgroundImageLayout = ImageLayout.Stretch;
            try
            {
                Icon = Icon.FromHandle(((Bitmap)logo).GetHicon());
            }
            catch
            {
                // Icon nije kritičan.
            }
        }

        const int overlayHeight = 118;
        _overlay = new Panel
        {
            // Ne koristi Dock.Bottom — to bi smanjilo prostor za BackgroundImage
            // i ostavilo „crnu“ traku bez slike.
            Bounds = new Rectangle(0, ClientSize.Height - overlayHeight, ClientSize.Width, overlayHeight),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Padding = new Padding(16, 10, 16, 10),
            BackColor = Color.FromArgb(180, 6, 18, 42)
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Text = "Priprema instalacije…",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            BackColor = Color.Transparent
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        _closeButton = new Button
        {
            Text = "Zatvori",
            Dock = DockStyle.Right,
            Width = 110,
            Height = 30,
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
            Height = 34,
            BackColor = Color.Transparent
        };
        buttonRow.Controls.Add(_closeButton);

        _overlay.Controls.Add(buttonRow);
        _overlay.Controls.Add(_progress);
        _overlay.Controls.Add(_statusLabel);

        Controls.Add(_overlay);
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
            // Bez logoa.
        }

        return null;
    }
}

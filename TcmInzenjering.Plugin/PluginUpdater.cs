using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
#if !BRICSCAD
using System.Windows;
#endif
using TcmInzenjering.Plugin.Update;

namespace TcmInzenjering.Plugin;

/// <summary>
/// Pokrece spoljasnji update proces (preuzimanje + progress bar + instalacija).
/// Preuzimanje ne radi u AutoCAD procesu da UI ne izgleda "zamrznut".
/// </summary>
internal static class PluginUpdater
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool TryStartUpdate(UpdateCheckResult result, out string message)
    {
        message = string.Empty;
        if (!result.CheckSucceeded || !result.UpdateAvailable)
        {
            message = "Nema dostupne nadogradnje.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.DownloadUrl) ||
            !result.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            message = "Nije pronadjen EXE installer u manifestu. Otvorite GitHub Releases rucno.";
            return false;
        }

#if !BRICSCAD
        var answer = MessageBox.Show(
            $"Dostupna je nova verzija {result.LatestVersion} (trenutna {result.CurrentVersion})." +
            Environment.NewLine + Environment.NewLine +
            (string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? string.Empty
                : "Novo u ovoj verziji:" + Environment.NewLine + result.ReleaseNotes + Environment.NewLine + Environment.NewLine) +
            "Preuzimanje ce ici u posebnom prozoru (sa progress bar-om), tako da AutoCAD nece biti blokiran." +
            Environment.NewLine + Environment.NewLine +
            "Nakon preuzimanja zatvorite AutoCAD da bi se instalacija zavrsila." +
            Environment.NewLine + Environment.NewLine +
            "Nastaviti?",
            "TCM-INZINJERING - Nadogradnja",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            message = "Nadogradnja otkazana.";
            return false;
        }
#endif

        try
        {
            var version = result.LatestVersion ?? "latest";
            var setupPath = Path.Combine(Path.GetTempPath(), $"TCM-INZINJERING-Setup-{version}.exe");
            var metaPath = Path.Combine(Path.GetTempPath(), "TcmInzenjering-update-meta.json");
            var scriptPath = Path.Combine(Path.GetTempPath(), "TcmInzenjering-update.ps1");

            var meta = new UpdateMeta
            {
                Version = version,
                CurrentVersion = result.CurrentVersion,
                DownloadUrl = result.DownloadUrl!,
                ReleaseNotes = result.ReleaseNotes ?? string.Empty,
                SetupPath = setupPath
            };
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOptions), Encoding.UTF8);
            File.WriteAllText(scriptPath, BuildUpdateScript(metaPath), Encoding.UTF8);

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(start);

            message =
                "Pokrenut je prozor preuzimanja (progress bar). AutoCAD mozete nastaviti da koristite. " +
                "Kada se zavrsi preuzimanje, zatvorite AutoCAD — instalacija ce se pokrenuti automatski.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Nije moguce pokrenuti nadogradnju: {ex.Message}";
            return false;
        }
    }

    private static string BuildUpdateScript(string metaPath)
    {
        var escapedMeta = metaPath.Replace("'", "''");
        return $$"""
Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type -AssemblyName System.Drawing | Out-Null
$ErrorActionPreference = "Stop"
$title = "TCM-INZINJERING - Nadogradnja"
$metaPath = '{{escapedMeta}}'
$nl = [Environment]::NewLine

function Show-Msg([string]$msg, [string]$icon = "Info") {
  $boxIcon = switch ($icon) {
    "Warning" { [System.Windows.Forms.MessageBoxIcon]::Warning }
    "Error"   { [System.Windows.Forms.MessageBoxIcon]::Error }
    default   { [System.Windows.Forms.MessageBoxIcon]::Information }
  }
  [System.Windows.Forms.MessageBox]::Show($msg, $title, [System.Windows.Forms.MessageBoxButtons]::OK, $boxIcon) | Out-Null
}

function Get-CadRunning {
  @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(acad|bricscad)$' })
}

function Format-Bytes([long]$bytes) {
  if ($bytes -lt 1KB) { return ("{0} B" -f $bytes) }
  if ($bytes -lt 1MB) { return ("{0:N1} KB" -f ($bytes / 1KB)) }
  return ("{0:N1} MB" -f ($bytes / 1MB))
}

if (-not (Test-Path -LiteralPath $metaPath)) {
  Show-Msg ("Nedostaje meta fajl za nadogradnju:" + $nl + $metaPath) "Error"
  exit 1
}

$meta = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$meta.Version
$current = [string]$meta.CurrentVersion
$url = [string]$meta.DownloadUrl
$notes = [string]$meta.ReleaseNotes
$setup = [string]$meta.SetupPath

# --- Progress window (logo umesto crnog ekrana) ---
$form = New-Object System.Windows.Forms.Form
$form.Text = $title
$form.Size = New-Object System.Drawing.Size(720, 420)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.TopMost = $true
$form.ShowInTaskbar = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(12, 28, 56)

$logoPaths = @(
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\TcmInzenjering.bundle\Contents\net8\Icons\TCM Logo.png"),
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\TcmInzenjering.bundle\Contents\net48\Icons\TCM Logo.png"),
  (Join-Path $PSScriptRoot "TCM Logo.png")
)
$pic = New-Object System.Windows.Forms.PictureBox
$pic.Dock = "Top"
$pic.Height = 260
$pic.SizeMode = "Zoom"
$pic.BackColor = $form.BackColor
foreach ($lp in $logoPaths) {
  if (Test-Path -LiteralPath $lp) {
    try {
      $fs = [System.IO.File]::OpenRead($lp)
      $pic.Image = [System.Drawing.Image]::FromStream($fs)
      $fs.Close()
      break
    } catch { }
  }
}

$label = New-Object System.Windows.Forms.Label
$label.Dock = "Top"
$label.Height = 36
$label.Padding = New-Object System.Windows.Forms.Padding(16, 8, 16, 0)
$label.ForeColor = [System.Drawing.Color]::White
$label.Text = "Preuzimanje TCM-INZINJERING v$version..."

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Dock = "Top"
$bar.Height = 26
$bar.Margin = New-Object System.Windows.Forms.Padding(16)
$bar.Minimum = 0
$bar.Maximum = 100
$bar.Style = "Continuous"
$bar.Value = 0

$status = New-Object System.Windows.Forms.Label
$status.Dock = "Top"
$status.Height = 28
$status.Padding = New-Object System.Windows.Forms.Padding(16, 6, 16, 0)
$status.ForeColor = [System.Drawing.Color]::FromArgb(176, 212, 232)
$status.Text = "Povezivanje..."

$form.Controls.AddRange(@($status, $bar, $label, $pic))
$form.Show()
$form.Refresh()
[System.Windows.Forms.Application]::DoEvents()

try {
  $req = [System.Net.HttpWebRequest]::Create($url)
  $req.Method = "GET"
  $req.UserAgent = "TcmInzenjering-Plugin-Updater"
  $req.AllowAutoRedirect = $true
  $req.Timeout = 900000
  $req.ReadWriteTimeout = 900000

  $resp = $req.GetResponse()
  $total = $resp.ContentLength
  $stream = $resp.GetResponseStream()
  $out = [System.IO.File]::Create($setup)
  $buffer = New-Object byte[] 81920
  $readTotal = [long]0
  $lastUi = [Environment]::TickCount

  if ($total -le 0) {
    $bar.Style = "Marquee"
    $bar.MarqueeAnimationSpeed = 30
    $status.Text = "Preuzimanje u toku..."
  }

  while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
    $out.Write($buffer, 0, $n)
    $readTotal += $n
    $now = [Environment]::TickCount
    if (($now - $lastUi) -ge 100 -or $n -lt $buffer.Length) {
      if ($total -gt 0) {
        $pct = [int][Math]::Min(100, [Math]::Floor(($readTotal * 100.0) / $total))
        $bar.Value = $pct
        $status.Text = ("{0} / {1}  ({2}%)" -f (Format-Bytes $readTotal), (Format-Bytes $total), $pct)
      } else {
        $status.Text = ("Preuzeto: {0}" -f (Format-Bytes $readTotal))
      }
      [System.Windows.Forms.Application]::DoEvents()
      $lastUi = $now
    }
  }

  $out.Flush()
  $out.Close()
  $stream.Close()
  $resp.Close()

  if ($total -gt 0) { $bar.Value = 100 }
  $status.Text = "Preuzimanje zavrseno."
  $label.Text = "Installer v$version je spreman."
  [System.Windows.Forms.Application]::DoEvents()
  Start-Sleep -Milliseconds 400
} catch {
  try { if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue } } catch { }
  $form.Close()
  Show-Msg ("Greska pri preuzimanju:" + $nl + $_.Exception.Message) "Error"
  exit 1
} finally {
  try { $form.Close() } catch { }
  try { $form.Dispose() } catch { }
}

if (-not (Test-Path -LiteralPath $setup)) {
  Show-Msg ("Nije pronadjen preuzeti installer:" + $nl + $setup) "Error"
  exit 1
}

[System.Windows.Forms.MessageBox]::Show(
  ("Installer v$version je preuzet." + $nl + $nl +
   "Sacuvajte crteze i zatvorite AutoCAD/BricsCAD, zatim kliknite OK." + $nl + $nl +
   "Instalacija ne moze da zameni DLL dok je program otvoren."),
  $title,
  [System.Windows.Forms.MessageBoxButtons]::OK,
  [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null

while ($true) {
  $running = Get-CadRunning
  if ($running.Count -eq 0) { break }
  $summary = ($running | Group-Object ProcessName | ForEach-Object { "$($_.Name).exe ($($_.Count))" }) -join ", "
  $retry = [System.Windows.Forms.MessageBox]::Show(
    ("Jos uvek je pokrenuto: $summary" + $nl + $nl + "Zatvorite CAD pa izaberite Retry."),
    $title,
    [System.Windows.Forms.MessageBoxButtons]::RetryCancel,
    [System.Windows.Forms.MessageBoxIcon]::Warning)
  if ($retry -eq [System.Windows.Forms.DialogResult]::Cancel) {
    Show-Msg "Nadogradnja otkazana." "Warning"
    exit 1
  }
  Start-Sleep -Milliseconds 800
}

Start-Sleep -Seconds 2

try {
  $p = Start-Process -FilePath $setup -ArgumentList "--silent" -PassThru -Wait
  if ($p.ExitCode -ne 0) {
    Show-Msg ("Instalacija nije uspela (exit $($p.ExitCode))." + $nl + "Pokrenite installer rucno:" + $nl + $setup) "Error"
    exit $p.ExitCode
  }

  $msg = "Uspesno instalirano: TCM-INZINJERING v$version"
  if ($current) { $msg += $nl + "Prethodna verzija: v$current" }
  if ($notes) {
    $msg += $nl + $nl + "Sta je novo:" + $nl + $notes
  }
  $msg += $nl + $nl + "Pokrenite AutoCAD/BricsCAD ponovo."
  Show-Msg $msg "Info"
} catch {
  Show-Msg ("Greska pri pokretanju instalera:" + $nl + $_.Exception.Message + $nl + $nl + $setup) "Error"
  exit 1
}

try { Remove-Item -LiteralPath $metaPath -Force -ErrorAction SilentlyContinue } catch { }
try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
""";
    }

    private sealed class UpdateMeta
    {
        public string Version { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string SetupPath { get; set; } = string.Empty;
    }
}

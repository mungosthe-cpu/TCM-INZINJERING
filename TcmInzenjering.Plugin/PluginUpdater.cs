using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TcmInzenjering.Plugin.Update;

namespace TcmInzenjering.Plugin;

/// <summary>
/// Pokrece spoljasnji update proces (preuzimanje + progress bar + instalacija).
/// Preuzimanje ne radi u AutoCAD procesu da UI ne izgleda "zamrznut".
/// Instalacija se odlaze dok korisnik ne zatvori AutoCAD/BricsCAD.
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
                "Pokrenut je prozor preuzimanja. Nastavite rad u AutoCAD-u." +
                Environment.NewLine +
                "Instalacija ce se automatski zavrsiti kada zatvorite AutoCAD (nema potrebe da ga gasite odmah).";
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
$script:CancelWait = $false

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

# --- Full-bleed logo preko CELOG prozora; status overlay lezi PREKO (ne uzima Dock prostor) ---
$form = New-Object System.Windows.Forms.Form
$form.Text = $title
$form.ClientSize = New-Object System.Drawing.Size(720, 480)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $true
$form.TopMost = $false
$form.ShowInTaskbar = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(8, 28, 72)

$logoPaths = @(
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\TcmInzenjering.bundle\Contents\net8\Icons\TCM Logo.png"),
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\TcmInzenjering.bundle\Contents\net48\Icons\TCM Logo.png"),
  (Join-Path $env:LOCALAPPDATA "Autodesk\ApplicationPlugins\TcmInzenjering.bundle\Contents\net8\Icons\TCM Logo.png"),
  (Join-Path $PSScriptRoot "TCM Logo.png")
)

foreach ($lp in $logoPaths) {
  if (Test-Path -LiteralPath $lp) {
    try {
      $fs = [System.IO.File]::OpenRead($lp)
      $form.BackgroundImage = [System.Drawing.Image]::FromStream($fs)
      $fs.Close()
      $form.BackgroundImageLayout = "Stretch"
      break
    } catch { }
  }
}

$overlayH = 120
$overlay = New-Object System.Windows.Forms.Panel
$overlay.Bounds = New-Object System.Drawing.Rectangle(0, ($form.ClientSize.Height - $overlayH), $form.ClientSize.Width, $overlayH)
$overlay.Anchor = "Left,Right,Bottom"
$overlay.Padding = New-Object System.Windows.Forms.Padding(16, 10, 16, 10)
$overlay.BackColor = [System.Drawing.Color]::FromArgb(180, 6, 18, 42)

$label = New-Object System.Windows.Forms.Label
$label.Dock = "Top"
$label.Height = 36
$label.ForeColor = [System.Drawing.Color]::White
$label.BackColor = [System.Drawing.Color]::Transparent
$label.Text = "Preuzimanje TCM-INZINJERING v$version..."

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Dock = "Top"
$bar.Height = 22
$bar.Minimum = 0
$bar.Maximum = 100
$bar.Style = "Continuous"
$bar.Value = 0

$status = New-Object System.Windows.Forms.Label
$status.Dock = "Top"
$status.Height = 28
$status.ForeColor = [System.Drawing.Color]::FromArgb(176, 212, 232)
$status.BackColor = [System.Drawing.Color]::Transparent
$status.Text = "Povezivanje..."

$cancelBtn = New-Object System.Windows.Forms.Button
$cancelBtn.Text = "Otkazi"
$cancelBtn.Width = 100
$cancelBtn.Height = 28
$cancelBtn.Dock = "Right"
$cancelBtn.FlatStyle = "Flat"
$cancelBtn.BackColor = [System.Drawing.Color]::FromArgb(0, 140, 200)
$cancelBtn.ForeColor = [System.Drawing.Color]::White
$cancelBtn.Visible = $false
$cancelBtn.Add_Click({
  $script:CancelWait = $true
  $cancelBtn.Enabled = $false
  $status.Text = "Otkazivanje..."
})

$btnRow = New-Object System.Windows.Forms.Panel
$btnRow.Dock = "Bottom"
$btnRow.Height = 32
$btnRow.BackColor = [System.Drawing.Color]::Transparent
$btnRow.Controls.Add($cancelBtn)

$overlay.Controls.Add($btnRow)
$overlay.Controls.Add($status)
$overlay.Controls.Add($bar)
$overlay.Controls.Add($label)

$form.Controls.Add($overlay)
$form.Add_FormClosing({
  param($sender, $e)
  if ($cancelBtn.Visible -and -not $script:CancelWait) {
    $script:CancelWait = $true
  }
})

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
} catch {
  try { if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue } } catch { }
  try { $form.Close() } catch { }
  Show-Msg ("Greska pri preuzimanju:" + $nl + $_.Exception.Message) "Error"
  exit 1
}

if (-not (Test-Path -LiteralPath $setup)) {
  try { $form.Close() } catch { }
  Show-Msg ("Nije pronadjen preuzeti installer:" + $nl + $setup) "Error"
  exit 1
}

# --- Cekaj zatvaranje CAD-a u pozadini; korisnik moze da nastavi rad ---
$running = Get-CadRunning
if ($running.Count -gt 0) {
  $label.Text = "Nadogradnja ceka zatvaranje AutoCAD-a"
  $status.Text = "Nastavite rad u AutoCAD-u. Instalacija ce se automatski zavrsiti kad ga zatvorite."
  $bar.Style = "Marquee"
  $bar.MarqueeAnimationSpeed = 40
  $cancelBtn.Visible = $true
  $form.WindowState = "Minimized"
  [System.Windows.Forms.Application]::DoEvents()

  Show-Msg (
    "Installer v$version je preuzet." + $nl + $nl +
    "Mozete nastaviti da radite u AutoCAD-u." + $nl + $nl +
    "Instalacija ce se automatski zavrsiti kada zatvorite AutoCAD/BricsCAD." + $nl +
    "(Nema potrebe da gasite program odmah.)" + $nl + $nl +
    "Prozor nadogradnje ostaje u taskbaru dok ceka."
  ) "Info"

  $form.WindowState = "Minimized"
  while (-not $script:CancelWait) {
    $running = Get-CadRunning
    if ($running.Count -eq 0) { break }
    $names = ($running | Group-Object ProcessName | ForEach-Object { "$($_.Name).exe" }) -join ", "
    $status.Text = "Cekam zatvaranje: $names  —  mozes nastaviti rad."
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 1500
  }

  if ($script:CancelWait) {
    try { $form.Close() } catch { }
    Show-Msg ("Nadogradnja otkazana." + $nl + $nl + "Installer ostaje ovde:" + $nl + $setup) "Warning"
    exit 1
  }
}

$form.WindowState = "Normal"
$form.Activate()
$cancelBtn.Visible = $false
$label.Text = "Instalacija u toku..."
$status.Text = "AutoCAD je zatvoren. Kopiranje fajlova..."
$bar.Style = "Marquee"
$bar.MarqueeAnimationSpeed = 30
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Seconds 2

try {
  $p = Start-Process -FilePath $setup -ArgumentList "--silent" -PassThru -Wait
  if ($p.ExitCode -ne 0) {
    try { $form.Close() } catch { }
    Show-Msg ("Instalacija nije uspela (exit $($p.ExitCode))." + $nl + "Pokrenite installer rucno:" + $nl + $setup) "Error"
    exit $p.ExitCode
  }

  try { $form.Close() } catch { }
  $msg = "Uspesno instalirano: TCM-INZINJERING v$version"
  if ($current) { $msg += $nl + "Prethodna verzija: v$current" }
  if ($notes) {
    $msg += $nl + $nl + "Sta je novo:" + $nl + $notes
  }
  $msg += $nl + $nl + "Pokrenite AutoCAD/BricsCAD ponovo da se ucita nova verzija."
  Show-Msg $msg "Info"
} catch {
  try { $form.Close() } catch { }
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

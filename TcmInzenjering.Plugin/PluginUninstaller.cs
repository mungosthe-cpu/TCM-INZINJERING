using System.Diagnostics;
using System.IO;
using System.Text;
#if !BRICSCAD
using System.Windows;
using Autodesk.Windows;
#endif

namespace TcmInzenjering.Plugin;

/// <summary>
/// Pokrece spoljasnji uninstall skript jer DLL ostaje zakljucan dok je AutoCAD otvoren.
/// </summary>
internal static class PluginUninstaller
{
    public static bool ConfirmAndStart(out string message)
    {
        message = string.Empty;
#if !BRICSCAD
        var answer = MessageBox.Show(
            "Ovo ce potpuno obrisati TCM-ROADS iz AutoCAD/BricsCAD " +
            "(bundle, registry, lokalna podesavanja)." + Environment.NewLine + Environment.NewLine +
            "Program ce zatraziti zatvaranje AutoCAD/BricsCAD-a " +
            "(imacete mogucnost da sacuvate crteze)." + Environment.NewLine +
            "Po zatvaranju, deinstalacija se nastavlja automatski." + Environment.NewLine + Environment.NewLine +
            "Nastaviti?",
            "TCM-ROADS - Deinstalacija",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            message = "Deinstalacija otkazana.";
            return false;
        }
#else
        // BricsCAD: potvrda preko komandne linije (PromptKeyword u Commands).
#endif

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "TcmInzenjering-uninstall.ps1");
            File.WriteAllText(scriptPath, BuildUninstallScript(), Encoding.UTF8);

            HiddenPowerShell.StartFile(scriptPath);

#if !BRICSCAD
            TryRemoveRibbonTab();
#endif
            message =
                "Pokrenuta je deinstalacija. AutoCAD ce biti zatrazten da se zatvori " +
                "(sacuvajte crteze ako se to trazi). Po zatvaranju, brisanje se nastavlja automatski.";
            return true;
        }
        catch (System.Exception ex)
        {
            message = $"Nije moguce pokrenuti deinstalaciju: {ex.Message}";
            return false;
        }
    }

#if !BRICSCAD
    private static void TryRemoveRibbonTab()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon is null)
            {
                return;
            }

            Ribbon.RibbonBuilder.RemoveAllTcmTabs();
        }
        catch
        {
            // Ribbon nije kritican za uninstall.
        }
    }
#endif

    private static string BuildUninstallScript()
    {
        return HiddenPowerShell.HideConsoleSnippet + """
Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type -AssemblyName System.Drawing | Out-Null
$ErrorActionPreference = "Continue"
$appName = "TcmInzenjering"
$bundleName = "TcmInzenjering.bundle"
$bricsBundleName = "TcmInzenjering.BricsCAD.bundle"
$title = "TCM-ROADS - Deinstalacija"
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

function Request-CadClose {
  $procs = Get-CadRunning
  foreach ($p in $procs) {
    try {
      if (-not $p.HasExited) {
        [void]$p.CloseMainWindow()
      }
    } catch { }
  }
  return $procs.Count
}

# --- Full-bleed logo UI (bez crnog PowerShell prozora) ---
$form = New-Object System.Windows.Forms.Form
$form.Text = $title
$form.ClientSize = New-Object System.Drawing.Size(720, 480)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $true
$form.ShowInTaskbar = $true
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(8, 28, 72)

$logoPaths = @(
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName\Contents\net8\Icons\TCM Logo.png"),
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName\Contents\net48\Icons\TCM Logo.png"),
  (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName\Contents\net8\Icons\TCM Logo.png"),
  (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName\Contents\net48\Icons\TCM Logo.png"),
  "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName\Contents\net8\Icons\TCM Logo.png",
  "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName\Contents\net48\Icons\TCM Logo.png"
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
$label.Text = "Deinstalacija TCM-ROADS..."

$status = New-Object System.Windows.Forms.Label
$status.Dock = "Top"
$status.Height = 28
$status.ForeColor = [System.Drawing.Color]::FromArgb(176, 212, 232)
$status.BackColor = [System.Drawing.Color]::Transparent
$status.Text = "Priprema..."

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Dock = "Top"
$bar.Height = 22
$bar.Style = "Marquee"
$bar.MarqueeAnimationSpeed = 35

$cancelBtn = New-Object System.Windows.Forms.Button
$cancelBtn.Text = "Otkazi"
$cancelBtn.Width = 100
$cancelBtn.Height = 28
$cancelBtn.Dock = "Right"
$cancelBtn.FlatStyle = "Flat"
$cancelBtn.BackColor = [System.Drawing.Color]::FromArgb(0, 140, 200)
$cancelBtn.ForeColor = [System.Drawing.Color]::White
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
  if (-not $script:CancelWait -and $cancelBtn.Visible) {
    $script:CancelWait = $true
  }
})

$form.Show()
$form.Activate()
$form.Refresh()
[System.Windows.Forms.Application]::DoEvents()

# 1) Zatrazi graceful close (dialog za Save u CAD-u)
$running = Get-CadRunning
if ($running.Count -gt 0) {
  $label.Text = "Zatvaranje AutoCAD / BricsCAD"
  $status.Text = "Otvara se upit za cuvanje crteza. Sacuvajte ili odbacite izmene."
  [System.Windows.Forms.Application]::DoEvents()

  Request-CadClose | Out-Null
  Start-Sleep -Milliseconds 600
  Request-CadClose | Out-Null

  $lastNudge = [Environment]::TickCount
  while (-not $script:CancelWait) {
    $running = Get-CadRunning
    if ($running.Count -eq 0) { break }

    $names = ($running | Group-Object ProcessName | ForEach-Object { "$($_.Name).exe ($($_.Count))" }) -join ", "
    $status.Text = "Cekam zatvaranje: $names — sacuvajte crteze ako se to trazi."
    [System.Windows.Forms.Application]::DoEvents()

    # Ponovi CloseMainWindow na svakih ~4s (ako je Save dialog otvoren, CAD ostaje dok korisnik ne odgovori).
    $now = [Environment]::TickCount
    if (($now - $lastNudge) -ge 4000) {
      Request-CadClose | Out-Null
      $lastNudge = $now
    }

    Start-Sleep -Milliseconds 800
  }

  if ($script:CancelWait) {
    try { $form.Close() } catch { }
    Show-Msg "Deinstalacija otkazana." "Warning"
    exit 1
  }
}

# 2) Kratka pauza da se DLL otkljuca
$cancelBtn.Visible = $false
$label.Text = "Brisanje plugina..."
$status.Text = "CAD je zatvoren. Uklanjanje fajlova..."
$bar.Style = "Marquee"
$bar.MarqueeAnimationSpeed = 30
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Seconds 2

$removed = New-Object System.Collections.Generic.List[string]
$failed = New-Object System.Collections.Generic.List[string]

$paths = @(
  (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName"),
  (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName"),
  "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName",
  (Join-Path $env:APPDATA "Bricsys\ApplicationPlugins\$bricsBundleName"),
  "C:\Program Files\Bricsys\ApplicationPlugins\$bricsBundleName",
  (Join-Path $env:APPDATA "TcmInzenjering")
)

foreach ($path in $paths) {
  if (-not (Test-Path -LiteralPath $path)) { continue }
  try {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
    $removed.Add($path)
  } catch {
    $failed.Add("$path ($($_.Exception.Message))")
  }
}

function Remove-AppRegistry([string]$rootPath) {
  if (-not (Test-Path $rootPath)) { return }
  Get-ChildItem $rootPath -ErrorAction SilentlyContinue | ForEach-Object {
    $apps = Join-Path $_.PSPath "Applications\$appName"
    if (Test-Path $apps) {
      try {
        Remove-Item -LiteralPath $apps -Recurse -Force -ErrorAction Stop
        $removed.Add($apps)
      } catch {
        $failed.Add("$apps ($($_.Exception.Message))")
      }
    }
    Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
      $nested = Join-Path $_.PSPath "Applications\$appName"
      if (Test-Path $nested) {
        try {
          Remove-Item -LiteralPath $nested -Recurse -Force -ErrorAction Stop
          $removed.Add($nested)
        } catch {
          $failed.Add("$nested ($($_.Exception.Message))")
        }
      }
    }
  }
}

Remove-AppRegistry "HKCU:\Software\Autodesk\AutoCAD"
Remove-AppRegistry "HKCU:\Software\Bricsys\BricsCAD"

$msg = "TCM-ROADS je uklonjen."
if ($removed.Count -gt 0) {
  $lines = ($removed | Select-Object -First 12 | ForEach-Object { " - $_" }) -join $nl
  $msg += $nl + $nl + "Obrisano:" + $nl + $lines
}
if ($failed.Count -gt 0) {
  $lines = ($failed | Select-Object -First 8 | ForEach-Object { " - $_" }) -join $nl
  $msg += $nl + $nl + "Nije obrisano (dozvole?):" + $nl + $lines
  $label.Text = "Deinstalacija delimicna"
  $status.Text = "Neki fajlovi nisu obrisani."
  [System.Windows.Forms.Application]::DoEvents()
  try { $form.Close() } catch { }
  Show-Msg $msg "Warning"
} else {
  $label.Text = "Deinstalacija zavrsena"
  $status.Text = "Plugin je uklonjen."
  $bar.Style = "Continuous"
  $bar.Value = 100
  [System.Windows.Forms.Application]::DoEvents()
  try { $form.Close() } catch { }
  Show-Msg $msg "Info"
}

try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
""";
    }
}

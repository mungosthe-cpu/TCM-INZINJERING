using System.Diagnostics;
using System.IO;
using System.Text;
#if NET8_0_OR_GREATER
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
#if NET8_0_OR_GREATER
        var answer = MessageBox.Show(
            "Ovo ce potpuno obrisati TCM-INZINJERING iz AutoCAD/BricsCAD " +
            "(bundle, registry, lokalna podesavanja)." + Environment.NewLine + Environment.NewLine +
            "Zatim zatvorite AutoCAD da bi se deinstalacija zavrsila." + Environment.NewLine + Environment.NewLine +
            "Nastaviti?",
            "TCM-INZINJERING - Deinstalacija",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            message = "Deinstalacija otkazana.";
            return false;
        }
#else
        // Legacy: potvrda preko komandne linije (PromptKeyword u Commands).
#endif

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "TcmInzenjering-uninstall.ps1");
            File.WriteAllText(scriptPath, BuildUninstallScript(), Encoding.UTF8);

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(start);

#if NET8_0_OR_GREATER
            TryRemoveRibbonTab();
#endif
            message =
                "Pokrenut je deinstalacioni proces. Zatvorite AutoCAD/BricsCAD da bi se obrisali fajlovi " +
                "(DLL je zakljucan dok je program otvoren).";
            return true;
        }
        catch (System.Exception ex)
        {
            message = $"Nije moguce pokrenuti deinstalaciju: {ex.Message}";
            return false;
        }
    }

#if NET8_0_OR_GREATER
    private static void TryRemoveRibbonTab()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon is null)
            {
                return;
            }

            var tab = ribbon.FindTab(Ribbon.RibbonBuilder.TabId);
            if (tab is not null)
            {
                ribbon.Tabs.Remove(tab);
            }
        }
        catch
        {
            // Ribbon nije kritican za uninstall.
        }
    }
#endif

    private static string BuildUninstallScript()
    {
        // ASCII-safe skripta (encoding issues sa PS UTF-8 special chars).
        return """
Add-Type -AssemblyName System.Windows.Forms | Out-Null
$ErrorActionPreference = "Continue"
$appName = "TcmInzenjering"
$bundleName = "TcmInzenjering.bundle"
$bricsBundleName = "TcmInzenjering.BricsCAD.bundle"
$title = "TCM-INZINJERING - Deinstalacija"
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

[System.Windows.Forms.MessageBox]::Show(
  ("Zatvorite AutoCAD i BricsCAD (sacuvajte crteze), zatim kliknite OK." + $nl + $nl +
   "Deinstalacija ne moze da obrise DLL dok je program otvoren."),
  $title,
  [System.Windows.Forms.MessageBoxButtons]::OK,
  [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null

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
    Show-Msg "Deinstalacija otkazana." "Warning"
    exit 1
  }
  Start-Sleep -Milliseconds 800
}

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
    # BricsCAD: locale subkeys
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

$msg = "TCM-INZINJERING je uklonjen."
if ($removed.Count -gt 0) {
  $lines = ($removed | Select-Object -First 12 | ForEach-Object { " - $_" }) -join $nl
  $msg += $nl + $nl + "Obrisano:" + $nl + $lines
}
if ($failed.Count -gt 0) {
  $lines = ($failed | Select-Object -First 8 | ForEach-Object { " - $_" }) -join $nl
  $msg += $nl + $nl + "Nije obrisano (dozvole?):" + $nl + $lines
  Show-Msg $msg "Warning"
} else {
  Show-Msg $msg "Info"
}

try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
""";
    }
}

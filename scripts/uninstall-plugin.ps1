# Standalone uninstall TCM-INZINJERING (pokrece ga i ribbon dugme TCMUNINSTALL).
# Pokretanje: powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-plugin.ps1

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Windows.Forms | Out-Null

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

$confirm = [System.Windows.Forms.MessageBox]::Show(
  ("Ovo ce potpuno obrisati TCM-INZINJERING (bundle, registry, podesavanja)." + $nl + $nl + "Nastaviti?"),
  $title,
  [System.Windows.Forms.MessageBoxButtons]::YesNo,
  [System.Windows.Forms.MessageBoxIcon]::Warning,
  [System.Windows.Forms.MessageBoxResult]::No)
if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) {
  Show-Msg "Deinstalacija otkazana." "Warning"
  exit 1
}

[System.Windows.Forms.MessageBox]::Show(
  ("Zatvorite AutoCAD i BricsCAD (sacuvajte crteze), zatim kliknite OK."),
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

param(
    [string]$BundleSource = (Join-Path $PSScriptRoot "..\TcmInzenjering.bundle"),
    [string]$BricsBundleSource = (Join-Path $PSScriptRoot "..\TcmInzenjering.BricsCAD.bundle"),
    [string]$DllPath = "",
    # Bez dijaloga (CI / tiha instalacija). Ako je AutoCAD otvoren - prekida se.
    [switch]$Quiet
)

$ErrorActionPreference = "Continue"

# Sakrij crni PowerShell host (kao Inno/Chrome updater — samo UI dijalog).
function Hide-HostConsole {
  try {
    if (-not ("Native.Win32Console" -as [type])) {
      Add-Type -Namespace Native -Name Win32Console -MemberDefinition @"
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern System.IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
"@
    }
    $hwnd = [Native.Win32Console]::GetConsoleWindow()
    if ($hwnd -ne [System.IntPtr]::Zero) {
      [void][Native.Win32Console]::ShowWindow($hwnd, 0)
    }
  } catch { }
}
if (-not $Quiet) {
  Hide-HostConsole
}

$bundleName = "TcmInzenjering.bundle"
$bricsBundleName = "TcmInzenjering.BricsCAD.bundle"
$appName = "TcmInzenjering"
$description = "TCM-INZINJERING"
$uiTitle = "TCM-INZINJERING - Azuriranje plugina"
$nl = [Environment]::NewLine

$autocadSeries = @(
    @{ Series = "R23.1"; Modern = $false; Year = 2020 },
    @{ Series = "R24.0"; Modern = $false; Year = 2021 },
    @{ Series = "R24.1"; Modern = $false; Year = 2022 },
    @{ Series = "R24.2"; Modern = $false; Year = 2023 },
    @{ Series = "R24.3"; Modern = $false; Year = 2024 },
    @{ Series = "R25.0"; Modern = $true; Year = 2025 },
    @{ Series = "R25.1"; Modern = $true; Year = 2026 }
)

function Show-UiMessage {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet("Info", "Warning", "Error")]
        [string]$Icon = "Info",
        [ValidateSet("OK", "RetryCancel", "OKCancel")]
        [string]$Buttons = "OK"
    )

    if ($Quiet) {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue | Out-Null
        return [System.Windows.Forms.DialogResult]::OK
    }

    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue | Out-Null

    $boxIcon = switch ($Icon) {
        "Warning" { [System.Windows.Forms.MessageBoxIcon]::Warning }
        "Error"   { [System.Windows.Forms.MessageBoxIcon]::Error }
        default   { [System.Windows.Forms.MessageBoxIcon]::Information }
    }

    $boxButtons = switch ($Buttons) {
        "RetryCancel" { [System.Windows.Forms.MessageBoxButtons]::RetryCancel }
        "OKCancel"    { [System.Windows.Forms.MessageBoxButtons]::OKCancel }
        default       { [System.Windows.Forms.MessageBoxButtons]::OK }
    }

    return [System.Windows.Forms.MessageBox]::Show(
        $Message,
        $uiTitle,
        $boxButtons,
        $boxIcon)
}

function Get-CadProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match '^(acad|bricscad)$' }
}

function Get-CadProcessSummary {
    $procs = @(Get-CadProcesses)
    if ($procs.Count -eq 0) {
        return $null
    }

    $names = $procs |
        Group-Object ProcessName |
        ForEach-Object { "$($_.Name).exe ($($_.Count))" }
    return ($names -join ", ")
}

function Wait-ForCadClosed {
    $summary = Get-CadProcessSummary
    if (-not $summary) {
        return $true
    }

    if ($Quiet) {
        Write-Host "GRESKA: AutoCAD/BricsCAD je otvoren ($summary). Zatvorite ga pa ponovo pokrenite instalaciju." -ForegroundColor Red
        return $false
    }

    Hide-HostConsole
    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    Add-Type -AssemblyName System.Drawing | Out-Null

    $script:WaitCancelled = $false
    $form = New-Object System.Windows.Forms.Form
    $form.Text = $uiTitle
    $form.ClientSize = New-Object System.Drawing.Size(520, 180)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $true
    $form.TopMost = $true
    $form.ShowInTaskbar = $true
    $form.BackColor = [System.Drawing.Color]::FromArgb(8, 28, 72)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Dock = "Top"
    $lbl.Height = 70
    $lbl.Padding = New-Object System.Windows.Forms.Padding(14, 12, 14, 4)
    $lbl.ForeColor = [System.Drawing.Color]::White
    $lbl.BackColor = [System.Drawing.Color]::Transparent
    $lbl.Text = "AutoCAD/BricsCAD je otvoren. Sacuvajte crteze i zatvorite program." + $nl +
                "Instalacija ce se nastaviti automatski (nema crnog PowerShell prozora)."

    $status = New-Object System.Windows.Forms.Label
    $status.Dock = "Top"
    $status.Height = 36
    $status.Padding = New-Object System.Windows.Forms.Padding(14, 0, 14, 0)
    $status.ForeColor = [System.Drawing.Color]::FromArgb(176, 212, 232)
    $status.BackColor = [System.Drawing.Color]::Transparent
    $status.Text = "Cekam: $summary"

    $bar = New-Object System.Windows.Forms.ProgressBar
    $bar.Dock = "Top"
    $bar.Height = 18
    $bar.Style = "Marquee"
    $bar.MarqueeAnimationSpeed = 35

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = "Otkazi"
    $cancel.Width = 100
    $cancel.Height = 28
    $cancel.Dock = "Right"
    $cancel.FlatStyle = "Flat"
    $cancel.BackColor = [System.Drawing.Color]::FromArgb(0, 140, 200)
    $cancel.ForeColor = [System.Drawing.Color]::White
    $cancel.Add_Click({
        $script:WaitCancelled = $true
        $cancel.Enabled = $false
        $status.Text = "Otkazivanje..."
    })

    $row = New-Object System.Windows.Forms.Panel
    $row.Dock = "Bottom"
    $row.Height = 36
    $row.Padding = New-Object System.Windows.Forms.Padding(14, 4, 14, 8)
    $row.BackColor = [System.Drawing.Color]::Transparent
    $row.Controls.Add($cancel)

    $form.Controls.Add($row)
    $form.Controls.Add($bar)
    $form.Controls.Add($status)
    $form.Controls.Add($lbl)
    $form.Add_FormClosing({
        param($s, $e)
        if (-not $script:WaitCancelled) { $script:WaitCancelled = $true }
    })

    $form.Show()
    $form.Activate()
    [System.Windows.Forms.Application]::DoEvents()

    while (-not $script:WaitCancelled) {
        Hide-HostConsole
        $summary = Get-CadProcessSummary
        if (-not $summary) {
            Start-Sleep -Milliseconds 800
            try { $form.Close() } catch { }
            return $true
        }

        $status.Text = "Cekam zatvaranje: $summary"
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 800
    }

    try { $form.Close() } catch { }
    return $false
}

function Deploy-Bundle {
    param([string]$Source, [string]$TargetRoot)

    try {
        $parent = Split-Path $TargetRoot -Parent
        if (-not (Test-Path $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        if (Test-Path $TargetRoot) {
            Remove-Item -LiteralPath $TargetRoot -Recurse -Force
        }

        Copy-Item -LiteralPath $Source -Destination $parent -Recurse -Force
        Write-Host "  Kopirano u: $TargetRoot" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  Preskoceno: $TargetRoot ($($_.Exception.Message))" -ForegroundColor Yellow
        return $false
    }
}

function Remove-LegacyBundleFiles {
    param([string]$BundleRoot)

    $legacyDll = Join-Path $BundleRoot "Contents\TcmInzenjering.Plugin.dll"
    if (Test-Path $legacyDll) {
        Remove-Item $legacyDll -Force
        Write-Host "  Uklonjen stari DLL: Contents\TcmInzenjering.Plugin.dll" -ForegroundColor DarkGray
    }

    $legacyIcons = Join-Path $BundleRoot "Contents\Icons"
    if (Test-Path $legacyIcons) {
        Remove-Item $legacyIcons -Recurse -Force
        Write-Host "  Uklonjen stari folder: Contents\Icons" -ForegroundColor DarkGray
    }
}

function Register-Plugin {
    param([string]$ApplicationsPath, [string]$ProductCode, [string]$Series, [bool]$Modern)

    try {
        if (-not (Test-Path $ApplicationsPath)) {
            New-Item -Path $ApplicationsPath -Force | Out-Null
        }

        $relativeDll = if ($Modern) {
            "Contents\net8\TcmInzenjering.Plugin.dll"
        } else {
            "Contents\net48\TcmInzenjering.Plugin.Legacy.dll"
        }

        $loader = Join-Path (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName") $relativeDll
        $appKey = Join-Path $ApplicationsPath $appName
        New-Item -Path $appKey -Force | Out-Null
        Set-ItemProperty -LiteralPath $appKey -Name "DESCRIPTION" -Value $description
        Set-ItemProperty -LiteralPath $appKey -Name "LOADCTRLS" -Value 2 -Type DWord
        Set-ItemProperty -LiteralPath $appKey -Name "LOADER" -Value $loader
        Set-ItemProperty -LiteralPath $appKey -Name "MANAGED" -Value 1 -Type DWord

        Write-Host "  Registry: $Series\$ProductCode\Applications\$appName" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  Registry preskocen: $ProductCode - $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

Write-Host "TCM-INZINJERING: instalacija / azuriranje plugina..." -ForegroundColor Cyan

if (-not (Wait-ForCadClosed)) {
    exit 1
}

Write-Host "AutoCAD/BricsCAD nije pokrenut - nastavljam instalaciju." -ForegroundColor Green

Deploy-Bundle $BundleSource (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName") | Out-Null
Remove-LegacyBundleFiles (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName")

Deploy-Bundle $BundleSource "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName" | Out-Null
if (Test-Path "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName") {
    Remove-LegacyBundleFiles "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName"
}

Deploy-Bundle $BundleSource (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName") | Out-Null
if (Test-Path (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName")) {
    Remove-LegacyBundleFiles (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName")
}

if (Test-Path $BricsBundleSource) {
    Deploy-Bundle $BricsBundleSource (Join-Path $env:APPDATA "Bricsys\ApplicationPlugins\$bricsBundleName") | Out-Null
    Deploy-Bundle $BricsBundleSource "C:\Program Files\Bricsys\ApplicationPlugins\$bricsBundleName" | Out-Null
}

$appDataNet8 = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName\Contents\net8\TcmInzenjering.Plugin.dll"
$appDataNet48 = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName\Contents\net48\TcmInzenjering.Plugin.Legacy.dll"

if (Test-Path -LiteralPath $appDataNet8) {
    $DllPath = $appDataNet8
}
elseif (Test-Path -LiteralPath $appDataNet48) {
    $DllPath = $appDataNet48
}
elseif ([string]::IsNullOrWhiteSpace($DllPath) -or -not (Test-Path -LiteralPath $DllPath)) {
    $err = "DLL nije pronadjen (net8 ni net48):$nl  $appDataNet8$nl  $appDataNet48"
    Write-Host $err -ForegroundColor Red
    Show-UiMessage -Icon Error -Message ("Instalacija nije uspela." + $nl + $nl + $err)
    throw $err
}

if (Test-Path -LiteralPath $appDataNet48) {
    Write-Host "Legacy (AutoCAD 2020-2024): $appDataNet48" -ForegroundColor Cyan
}
else {
    Write-Host "Upozorenje: nema net48 Legacy DLL — AutoCAD 2024 nece ucitati plugin." -ForegroundColor Yellow
}

if (Test-Path -LiteralPath $appDataNet8) {
    Write-Host "Modern (AutoCAD 2025-2026): $appDataNet8" -ForegroundColor Cyan
}
else {
    Write-Host "Upozorenje: nema net8 DLL — AutoCAD 2026 nece ucitati plugin." -ForegroundColor Yellow
}

$registered = 0
foreach ($seriesInfo in $autocadSeries) {
    $hklmRoot = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($seriesInfo.Series)"
    if (-not (Test-Path $hklmRoot)) {
        continue
    }

    $productCodes = Get-ChildItem $hklmRoot |
        Where-Object { $_.PSChildName -like "ACAD-*" } |
        Select-Object -ExpandProperty PSChildName
    foreach ($code in $productCodes) {
        $hkcuPath = "HKCU:\Software\Autodesk\AutoCAD\$($seriesInfo.Series)\$code\Applications"
        if (Register-Plugin $hkcuPath $code $seriesInfo.Series $seriesInfo.Modern) {
            $registered++
        }
    }
}

if ($registered -eq 0) {
    Write-Host "Upozorenje: nije registrovan nijedan AutoCAD profil (bundle i dalje moze raditi preko ApplicationPlugins)." -ForegroundColor Yellow
}
else {
    Write-Host "TCM-INZINJERING: registrovano u $registered AutoCAD profila." -ForegroundColor Cyan
}

Write-Host "LOADER = $DllPath" -ForegroundColor DarkGray

$doneMessage = "Nova verzija TCM-INZINJERING plugina je uspesno instalirana." + $nl + $nl + "Mozete ponovo pokrenuti AutoCAD."
Write-Host "Nova verzija TCM-INZINJERING plugina je uspesno instalirana. Mozete ponovo pokrenuti AutoCAD." -ForegroundColor Cyan
Show-UiMessage -Icon Info -Message $doneMessage

param(
    [string]$BundleSource = (Join-Path $PSScriptRoot "..\TcmInzenjering.bundle"),
    [string]$BricsBundleSource = (Join-Path $PSScriptRoot "..\TcmInzenjering.BricsCAD.bundle"),
    [string]$DllPath = "",
    # Bez dijaloga (CI / tiha instalacija). Ako je AutoCAD otvoren - prekida se.
    [switch]$Quiet
)

$ErrorActionPreference = "Continue"

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
    while ($true) {
        $summary = Get-CadProcessSummary
        if (-not $summary) {
            return $true
        }

        Write-Host "Pokrenuto: $summary - sacuvaj i zatvori pre instalacije." -ForegroundColor Yellow

        if ($Quiet) {
            Write-Host "GRESKA: AutoCAD/BricsCAD je otvoren. Zatvorite ga pa ponovo pokrenite instalaciju (ili bez -Quiet)." -ForegroundColor Red
            return $false
        }

        $msgClose = "Radi instalacije nove verzije plugina potrebno je zatvoriti AutoCAD (i BricsCAD ako je otvoren)." +
            $nl + $nl +
            "Trenutno pokrenuto: $summary" +
            $nl + $nl +
            "Sacuvajte crteze, zatvorite program, zatim kliknite OK."

        $answer = Show-UiMessage -Icon Warning -Buttons OKCancel -Message $msgClose
        if ($answer -eq [System.Windows.Forms.DialogResult]::Cancel) {
            Write-Host "Instalacija otkazana (AutoCAD nije zatvoren)." -ForegroundColor Yellow
            return $false
        }

        Start-Sleep -Milliseconds 800

        $summary = Get-CadProcessSummary
        if (-not $summary) {
            return $true
        }

        $msgRetry = "AutoCAD/BricsCAD je i dalje pokrenut ($summary)." +
            $nl + $nl +
            "DLL je zakljucan dok je program otvoren." +
            $nl + $nl +
            "Zatvorite ga pa izaberite Pokusaj ponovo."

        $retry = Show-UiMessage -Icon Warning -Buttons RetryCancel -Message $msgRetry
        if ($retry -eq [System.Windows.Forms.DialogResult]::Cancel) {
            Write-Host "Instalacija otkazana." -ForegroundColor Yellow
            return $false
        }
    }
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

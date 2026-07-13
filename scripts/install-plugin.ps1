param(
    [string]$BundleSource = (Join-Path $PSScriptRoot "..\TcmInzenjering.bundle"),
    [string]$BricsBundleSource = (Join-Path $PSScriptRoot "..\TcmInzenjering.BricsCAD.bundle"),
    [string]$DllPath = ""
)

$ErrorActionPreference = "Continue"

$bundleName = "TcmInzenjering.bundle"
$bricsBundleName = "TcmInzenjering.BricsCAD.bundle"
$appName = "TcmInzenjering"
$description = "TCM-INZINJERING"

$autocadSeries = @(
    @{ Series = "R23.1"; Modern = $false; Year = 2020 },
    @{ Series = "R24.0"; Modern = $false; Year = 2021 },
    @{ Series = "R24.1"; Modern = $false; Year = 2022 },
    @{ Series = "R24.2"; Modern = $false; Year = 2023 },
    @{ Series = "R24.3"; Modern = $false; Year = 2024 },
    @{ Series = "R25.0"; Modern = $true; Year = 2025 },
    @{ Series = "R25.1"; Modern = $true; Year = 2026 }
)

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

Write-Host "TCM-INZINJERING: instalacija plugina..." -ForegroundColor Cyan

$deployTargets = @(
    (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName"),
    "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName",
    (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName"),
    (Join-Path $env:APPDATA "Bricsys\ApplicationPlugins\$bricsBundleName"),
    "C:\Program Files\Bricsys\ApplicationPlugins\$bricsBundleName"
)

Deploy-Bundle $BundleSource (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName") | Out-Null
Deploy-Bundle $BundleSource "C:\Program Files\Autodesk\ApplicationPlugins\$bundleName" | Out-Null
Deploy-Bundle $BundleSource (Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName") | Out-Null
if (Test-Path $BricsBundleSource) {
    Deploy-Bundle $BricsBundleSource (Join-Path $env:APPDATA "Bricsys\ApplicationPlugins\$bricsBundleName") | Out-Null
    Deploy-Bundle $BricsBundleSource "C:\Program Files\Bricsys\ApplicationPlugins\$bricsBundleName" | Out-Null
}

$appDataDll = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName\Contents\net8\TcmInzenjering.Plugin.dll"
if (Test-Path -LiteralPath $appDataDll) {
    $DllPath = $appDataDll
}
elseif ([string]::IsNullOrWhiteSpace($DllPath) -or -not (Test-Path -LiteralPath $DllPath)) {
    throw "DLL nije pronadjen: $appDataDll"
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

$registered = 0
foreach ($seriesInfo in $autocadSeries) {
    $hklmRoot = "HKLM:\SOFTWARE\Autodesk\AutoCAD\$($seriesInfo.Series)"
    if (-not (Test-Path $hklmRoot)) {
        continue
    }

    $productCodes = Get-ChildItem $hklmRoot | Where-Object { $_.PSChildName -like "ACAD-*" } | Select-Object -ExpandProperty PSChildName
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
Write-Host "Restartuj AutoCAD/BricsCAD. Plugin ce se ucitati automatski." -ForegroundColor Cyan

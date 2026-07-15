param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($Version)) {
    $props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
    $Version = $props.Project.PropertyGroup.Version
}

Write-Host "TCM-INZINJERING: release build v$Version" -ForegroundColor Cyan

$pluginProject = Join-Path $root "TcmInzenjering.Plugin\TcmInzenjering.Plugin.csproj"
$setupProject = Join-Path $root "TcmInzenjering.Setup\TcmInzenjering.Setup.csproj"
$distDir = Join-Path $root "dist"
$payloadDir = Join-Path $distDir "payload"
$setupName = "TCM-INZINJERING-Setup-$Version.exe"

if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}

New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

Write-Host "1/4 Build plugina (net8)..." -ForegroundColor Yellow
dotnet build $pluginProject -c $Configuration /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Build plugina nije uspeo." }

function Invoke-LegacyBuild {
    param(
        [string]$PluginProject,
        [string]$Configuration,
        [string]$Version,
        [switch]$UseBricsCAD,
        [string]$BricsCADPath = "",
        [string]$AutoCADPath = ""
    )

    $args = @(
        "build", $PluginProject,
        "-c", $Configuration,
        "-f", "net48",
        "/p:TargetFrameworks=net48",
        "/p:BuildLegacy=true",
        "/p:Version=$Version"
    )

    if ($UseBricsCAD) {
        $args += "/p:UseBricsCAD=true", "/p:BricsCADPath=$BricsCADPath"
    }
    elseif ($AutoCADPath) {
        $args += "/p:UseAutoCadNuget=false", "/p:AutoCADPath=$AutoCADPath"
    }

    & dotnet @args
}

$legacyAutoCad = @("2024", "2023", "2022", "2021", "2020") |
    ForEach-Object { "C:\Program Files\Autodesk\AutoCAD $_" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

$bricsInstall = Get-ChildItem "C:\Program Files\Bricsys" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "BricsCAD V*" } |
    Sort-Object Name -Descending |
    Select-Object -First 1

$legacyBuilt = $false

# Prefer AutoCAD.NET NuGet (24.3 = AutoCAD 2024) so CI / machines without AutoCAD 2020-2024 still ship net48.
Write-Host "1b/4 Build legacy plugina (net48) za AutoCAD 2020-2024 (AutoCAD.NET 24.3 NuGet)..." -ForegroundColor Yellow
Invoke-LegacyBuild -PluginProject $pluginProject -Configuration $Configuration -Version $Version
if ($LASTEXITCODE -eq 0) {
    $legacyBuilt = $true
}
elseif ($legacyAutoCad) {
    Write-Host "Upozorenje: NuGet legacy build nije uspeo - pokusavam lokalni AutoCAD: $legacyAutoCad" -ForegroundColor Yellow
    Invoke-LegacyBuild -PluginProject $pluginProject -Configuration $Configuration -Version $Version -AutoCADPath $legacyAutoCad
    if ($LASTEXITCODE -eq 0) { $legacyBuilt = $true }
}
elseif ($bricsInstall) {
    Write-Host "1b/4 Fallback: legacy (net48) za BricsCAD sa: $($bricsInstall.FullName)" -ForegroundColor Yellow
    & (Join-Path $root "scripts\copy-bricscad-libs.ps1") -SourcePath $bricsInstall.FullName
    Invoke-LegacyBuild -PluginProject $pluginProject -Configuration $Configuration -Version $Version -UseBricsCAD -BricsCADPath (Join-Path $root "lib\BricsCAD")
    if ($LASTEXITCODE -eq 0) { $legacyBuilt = $true }
    else { Write-Host "Upozorenje: legacy BricsCAD build nije uspeo." -ForegroundColor Yellow }
}
elseif (Test-Path (Join-Path $root "lib\BricsCAD\BrxMgd.dll")) {
    Write-Host "1b/4 Fallback: legacy (net48) za BricsCAD (lib/BricsCAD)..." -ForegroundColor Yellow
    Invoke-LegacyBuild -PluginProject $pluginProject -Configuration $Configuration -Version $Version -UseBricsCAD -BricsCADPath (Join-Path $root "lib\BricsCAD")
    if ($LASTEXITCODE -eq 0) { $legacyBuilt = $true }
    else { Write-Host "Upozorenje: legacy BricsCAD build nije uspeo." -ForegroundColor Yellow }
}
else {
    Write-Host "Legacy build nije uspeo (NuGet AutoCAD.NET / lokalni AutoCAD / BricsCAD)." -ForegroundColor Yellow
    Write-Host "  Za BricsCAD: .\scripts\copy-bricscad-libs.ps1 -SourcePath 'C:\Program Files\Bricsys\BricsCAD V25 en_US'" -ForegroundColor Yellow
}

if (-not $legacyBuilt) {
    Write-Host "Upozorenje: net48 DLL nije izgradjen. BricsCAD i AutoCAD 2020-2024 nece raditi." -ForegroundColor Yellow
}

Write-Host "2/4 Priprema payload bundle paketa..." -ForegroundColor Yellow
$bundleSource = Join-Path $root "TcmInzenjering.bundle"
$bricsSource = Join-Path $root "TcmInzenjering.BricsCAD.bundle"
Copy-Item $bundleSource (Join-Path $payloadDir "TcmInzenjering.bundle") -Recurse -Force
Copy-Item $bricsSource (Join-Path $payloadDir "TcmInzenjering.BricsCAD.bundle") -Recurse -Force

$logoSrc = Join-Path $root "ICONS\TCM Logo.png"
if (Test-Path $logoSrc) {
    foreach ($dir in @(
        (Join-Path $payloadDir "TcmInzenjering.bundle\Contents\net8\Icons"),
        (Join-Path $payloadDir "TcmInzenjering.bundle\Contents\net48\Icons"),
        (Join-Path $payloadDir "TcmInzenjering.BricsCAD.bundle\Contents\net48\Icons")
    )) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item $logoSrc (Join-Path $dir "TCM Logo.png") -Force
    }
}

$acadLegacyDll = Join-Path $payloadDir "TcmInzenjering.bundle\Contents\net48\TcmInzenjering.Plugin.Legacy.dll"
if (-not (Test-Path $acadLegacyDll)) {
    throw "AutoCAD Legacy DLL nije u payload-u: $acadLegacyDll"
}
Write-Host "Legacy DLL OK: $acadLegacyDll" -ForegroundColor Green

$bricsLegacyDll = Join-Path $payloadDir "TcmInzenjering.BricsCAD.bundle\Contents\net48\TcmInzenjering.Plugin.Legacy.dll"
if (-not (Test-Path $bricsLegacyDll)) {
    Write-Host "Upozorenje: BricsCAD bundle nema net48 DLL ($bricsLegacyDll)." -ForegroundColor Yellow
    Write-Host "  AutoCAD 2020-2024 ce raditi. Za BricsCAD pokrenite build sa lib\BricsCAD referencama." -ForegroundColor Yellow
}

$manifestPath = Join-Path $root "release\update-manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.Version = $Version
$manifest.DownloadUrl = "https://github.com/mungosthe-cpu/TCM-INZINJERING/releases/latest/download/$setupName"
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding UTF8
Copy-Item $manifestPath (Join-Path $distDir "update-manifest.json") -Force

Write-Host "3/4 Publish instalera (payload ugradjen u EXE)..." -ForegroundColor Yellow
$setupPayloadDir = Join-Path $root "TcmInzenjering.Setup\payload"
if (Test-Path $setupPayloadDir) {
    Remove-Item $setupPayloadDir -Recurse -Force
}
Copy-Item $payloadDir $setupPayloadDir -Recurse -Force

dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true /p:Version=$Version /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "Publish instalera nije uspeo." }

$publishedSetup = Join-Path $root "TcmInzenjering.Setup\bin\$Configuration\net8.0-windows\win-x64\publish\TCM-INZINJERING-Setup.exe"
if (-not (Test-Path $publishedSetup)) {
    # Starija putanja (pre WinForms / net8.0-windows).
    $legacyPublish = Join-Path $root "TcmInzenjering.Setup\bin\$Configuration\net8.0\win-x64\publish\TCM-INZINJERING-Setup.exe"
    if (Test-Path $legacyPublish) { $publishedSetup = $legacyPublish }
}
if (-not (Test-Path $publishedSetup)) {
    throw "Installer EXE nije pronadjen: $publishedSetup"
}

Write-Host "4/4 Pakovanje distribucije..." -ForegroundColor Yellow
Copy-Item $publishedSetup (Join-Path $distDir $setupName) -Force

$zipPath = Join-Path $distDir "TCM-INZINJERING-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir $setupName), (Join-Path $distDir "update-manifest.json") -DestinationPath $zipPath

Write-Host ""
Write-Host "Release spreman:" -ForegroundColor Green
Write-Host "  Installer: $(Join-Path $distDir $setupName)"
Write-Host "  ZIP:       $zipPath"
Write-Host "  Manifest:  $(Join-Path $distDir 'update-manifest.json')"
Write-Host ""
Write-Host "GitHub koraci:" -ForegroundColor Cyan
Write-Host "  1. git tag v$Version"
Write-Host "  2. git push origin v$Version"
Write-Host "  3. GitHub Release -> upload $setupName i update-manifest.json"

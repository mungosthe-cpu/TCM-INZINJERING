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

$legacyAutoCad = @("2024", "2023", "2022", "2021", "2020") |
    ForEach-Object { "C:\Program Files\Autodesk\AutoCAD $_" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if ($legacyAutoCad) {
    Write-Host "1b/4 Build legacy plugina (net48) sa: $legacyAutoCad" -ForegroundColor Yellow
    dotnet build $pluginProject -c $Configuration /p:Version=$Version /p:BuildLegacy=true /p:AutoCADPath="$legacyAutoCad"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Upozorenje: legacy build nije uspeo. AutoCAD 2020-2024 DLL nece biti u paketu." -ForegroundColor Yellow
    }
}
else {
    Write-Host "Legacy build preskocen (nema AutoCAD 2020-2024 na build masini)." -ForegroundColor Yellow
}

Write-Host "2/4 Priprema payload bundle paketa..." -ForegroundColor Yellow
$bundleSource = Join-Path $root "TcmInzenjering.bundle"
$bricsSource = Join-Path $root "TcmInzenjering.BricsCAD.bundle"
Copy-Item $bundleSource (Join-Path $payloadDir "TcmInzenjering.bundle") -Recurse -Force
Copy-Item $bricsSource (Join-Path $payloadDir "TcmInzenjering.BricsCAD.bundle") -Recurse -Force

$manifestPath = Join-Path $root "release\update-manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $Version
$manifest.downloadUrl = "https://github.com/mungosthe-cpu/TCM-INZINJERING/releases/latest/download/$setupName"
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $distDir "update-manifest.json") -Encoding UTF8
Copy-Item $manifestPath (Join-Path $distDir "update-manifest.json") -Force

Write-Host "3/4 Publish instalera..." -ForegroundColor Yellow
dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true /p:Version=$Version /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "Publish instalera nije uspeo." }

$publishedSetup = Join-Path $root "TcmInzenjering.Setup\bin\$Configuration\net8.0\win-x64\publish\TCM-INZINJERING-Setup.exe"
if (-not (Test-Path $publishedSetup)) {
    throw "Installer EXE nije pronadjen: $publishedSetup"
}

$publishDir = Split-Path $publishedSetup -Parent
Copy-Item $payloadDir (Join-Path $publishDir "payload") -Recurse -Force

Write-Host "4/4 Pakovanje distribucije..." -ForegroundColor Yellow
Copy-Item $publishedSetup (Join-Path $distDir $setupName) -Force
Copy-Item (Join-Path $publishDir "payload") (Join-Path $distDir "payload") -Recurse -Force

$zipPath = Join-Path $distDir "TCM-INZINJERING-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir $setupName), (Join-Path $distDir "payload"), (Join-Path $distDir "update-manifest.json") -DestinationPath $zipPath

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

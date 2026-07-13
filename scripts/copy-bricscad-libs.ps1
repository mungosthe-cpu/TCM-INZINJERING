param(
    [string]$SourcePath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "lib\BricsCAD"

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Get-ChildItem "C:\Program Files\Bricsys" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "BricsCAD V*" } |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($SourcePath) -or -not (Test-Path $SourcePath)) {
    throw "BricsCAD instalacija nije pronadjena. Prosledite -SourcePath 'C:\Program Files\Bricsys\BricsCAD V25 en_US'."
}

New-Item -ItemType Directory -Path $dest -Force | Out-Null

$files = @("BrxMgd.dll", "TD_Mgd.dll")
foreach ($file in $files) {
    $src = Join-Path $SourcePath $file
    if (-not (Test-Path $src)) {
        throw "Nedostaje $file u $SourcePath"
    }

    Copy-Item $src (Join-Path $dest $file) -Force
    Write-Host "Kopirano: $file" -ForegroundColor Green
}

Write-Host "BricsCAD reference DLL spremne u: $dest" -ForegroundColor Cyan

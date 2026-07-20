$roots = @(
    "C:\Users\User\Desktop\AUTOCAD PROGRAMS\TcmInzenjering.bundle",
    "$env:APPDATA\Autodesk\ApplicationPlugins\TcmInzenjering.bundle",
    "C:\Program Files\Autodesk\ApplicationPlugins\TcmInzenjering.bundle",
    "C:\ProgramData\Autodesk\ApplicationPlugins\TcmInzenjering.bundle"
)

foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Filter "TCM PROJEKAT.png" -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item $_.FullName -Force
            Write-Host "Obrisano: $($_.FullName)"
        }
}
Write-Host "Gotovo."

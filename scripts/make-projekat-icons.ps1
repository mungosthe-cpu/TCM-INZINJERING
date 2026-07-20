Add-Type -AssemblyName System.Drawing

$sourcePath = "C:\Users\User\Desktop\AUTOCAD PROGRAMS\ICONS\TCM PROJEKAT.png"
$outDir = "C:\Users\User\Desktop\AUTOCAD PROGRAMS\TcmInzenjering.Plugin\Icons"

$src = [System.Drawing.Image]::FromFile($sourcePath)
try {
    foreach ($size in 16, 32, 48, 64) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'HighQuality'
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode = 'HighQuality'

        $scale = [Math]::Min($size / $src.Width, $size / $src.Height)
        $w = [Math]::Max(1, [int]($src.Width * $scale))
        $h = [Math]::Max(1, [int]($src.Height * $scale))
        $x = [int](($size - $w) / 2)
        $y = [int](($size - $h) / 2)
        $g.DrawImage($src, $x, $y, $w, $h)
        $g.Dispose()

        $outPath = Join-Path $outDir "tcm_projekat_$size.png"
        $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host "OK: $outPath"
    }
}
finally {
    $src.Dispose()
}

# Builds a multi-size app.ico (16..256, PNG-compressed entries) from Assets/logo.png.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = "src\AIClient.App\Assets"
$src = New-Object System.Drawing.Bitmap("$root\logo.png")
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$blobs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += , @{ Size = $s; Data = $ms.ToArray() }
    $bmp.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)

# ICONDIR: reserved, type=1 (icon), count.
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$blobs.Count)

$offset = 6 + 16 * $blobs.Count
foreach ($b in $blobs) {
    $dim = if ($b.Size -ge 256) { 0 } else { $b.Size }
    $bw.Write([byte]$dim)          # width (0 means 256)
    $bw.Write([byte]$dim)          # height
    $bw.Write([byte]0)             # palette colours
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bits per pixel
    $bw.Write([uint32]$b.Data.Length)
    $bw.Write([uint32]$offset)
    $offset += $b.Data.Length
}

foreach ($b in $blobs) { $bw.Write($b.Data) }
$bw.Flush()

[System.IO.File]::WriteAllBytes("$root\app.ico", $out.ToArray())
Write-Host "app.ico written: $((Get-Item "$root\app.ico").Length) bytes, $($blobs.Count) sizes"

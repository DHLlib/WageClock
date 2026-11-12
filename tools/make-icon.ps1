$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int]($size * 0.02)
    $r = [int]($size * 0.22)
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 91, 231, 169)
    $c2 = [System.Drawing.Color]::FromArgb(255, 11, 143, 98)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 148.0)
    $g.FillPath($brush, $path)
    $brush.Dispose()

    # top highlight
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillPath($hl, $path)
    $hl.Dispose()

    $fontSize = [float]($size * 0.60)
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(45, 0, 40, 25))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $dy = [float]($size * 0.03)
    $layout = New-Object System.Drawing.RectangleF([float]$rect.X, [float]($rect.Y + $dy), [float]$rect.Width, [float]$rect.Height)
    $g.DrawString([string][char]0x00A5, $font, $shadow, $layout, $fmt)
    $layout2 = New-Object System.Drawing.RectangleF([float]$rect.X, [float]$rect.Y, [float]$rect.Width, [float]$rect.Height)
    $g.DrawString([string][char]0x00A5, $font, $white, $layout2, $fmt)

    $shadow.Dispose(); $white.Dispose(); $font.Dispose(); $fmt.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$master = New-IconBitmap 256
$master.Save((Join-Path $assets 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16, 24, 32, 48, 64, 256)
$pngs = @{}
foreach ($s in $sizes) {
    $scaled = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($scaled)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $scaled.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $ms.Dispose(); $scaled.Dispose()
}
$master.Dispose()

# assemble .ico with PNG-compressed entries
$count = $sizes.Count
$header = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($header)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$count)
$offset = 6 + 16 * $count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $header.ToArray())
$bw.Dispose(); $header.Dispose()

Write-Output ("icon.ico written: " + (Get-Item (Join-Path $assets 'icon.ico')).Length + " bytes")

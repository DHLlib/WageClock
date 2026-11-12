$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Add-Type -AssemblyName System.Drawing

function Draw-Rounded($g, $img, [int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $path.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $path.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    $old = $g.Clip
    $g.SetClip($path)
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle($x, $y, $w, $h)))
    $g.Clip = $old
    $path.Dispose()
}
$shots = Join-Path $root 'shots'
$out = Join-Path $root 'shots\sheet.png'

# 各状态截图不入库：缺了就用宿主自带的截图模式重新生成（要求没有正在运行的实例）
$needed = @(
    '01-working-dark.png', '02-lunch-dark.png', '03-before-dark.png', '04-done-dark.png', '05-off-dark.png',
    '06-working-light.png', '07-lunch-light.png', '08-off-light.png',
    '09-settings-dark.png', '10-settings-light.png', '11-big-salary.png'
)
$missing = @($needed | Where-Object { -not (Test-Path (Join-Path $shots $_)) })
if ($missing.Count -gt 0) {
    $exe = Join-Path $root 'build\WageClock.exe'
    if (-not (Test-Path $exe)) { throw "缺少状态截图，且没有 build\WageClock.exe；先运行 tools\build.ps1" }
    if (Get-Process WageClock -ErrorAction SilentlyContinue) {
        throw "缺少状态截图，但 WageClock 正在运行；先退出托盘实例再重跑本脚本"
    }
    Write-Output ("生成缺失的状态截图（" + $missing.Count + " 张）…")
    & $exe --shot $shots | Out-Null
    $still = @($needed | Where-Object { -not (Test-Path (Join-Path $shots $_)) })
    if ($still.Count -gt 0) { throw ("截图失败，仍缺：" + ($still -join ', ')) }
}

$W = 1432
$H = 1176

function Brush([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B))
}
function Font([string]$name, [float]$size, $style) {
    return New-Object System.Drawing.Font($name, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

# background
$bgRect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect,
    [System.Drawing.ColorTranslator]::FromHtml('#0C0E12'),
    [System.Drawing.ColorTranslator]::FromHtml('#14171C'), 90.0)
$g.FillRectangle($bgBrush, $bgRect)
$bgBrush.Dispose()

# soft accent glow, top-left
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(-260, -420, 900, 760)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
$glow.CenterColor = [System.Drawing.Color]::FromArgb(46, 85, 227, 166)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 85, 227, 166))
$g.FillPath($glow, $glowPath)
$glow.Dispose(); $glowPath.Dispose()

# header
$fTitle = Font 'Microsoft YaHei UI' 27 ([System.Drawing.FontStyle]::Bold)
$fSub = Font 'Microsoft YaHei UI' 13.5 ([System.Drawing.FontStyle]::Regular)
$fTag = Font 'Microsoft YaHei UI' 11 ([System.Drawing.FontStyle]::Regular)
$fCap = Font 'Microsoft YaHei UI' 11 ([System.Drawing.FontStyle]::Regular)
$fNote = Font 'Microsoft YaHei UI' 12 ([System.Drawing.FontStyle]::Regular)

$white = Brush '#F2F4F8'
$gray = Brush '#8A929C'
$dim = Brush '#5C636D'
$accent = Brush '#55E3A6'

$g.DrawString('Wage Clock · 工资时钟', $fTitle, $white, 56, 44)
$g.DrawString('常驻桌面的「今天已经赚了多少」——时间驱动，每秒重算，午休与休息日自动冻结。', $fSub, $gray, 58, 88)

$tagText = 'v0.1 · WinForms + WebView2 · 实机截图'
$tagSize = $g.MeasureString($tagText, $fTag)
$tagRect = New-Object System.Drawing.RectangleF(($W - 56 - $tagSize.Width - 24), 52, ($tagSize.Width + 24), 26)
$tagPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 85, 227, 166), 1)
$g.DrawRectangle($tagPen, $tagRect.X, $tagRect.Y, $tagRect.Width, $tagRect.Height)
$g.DrawString($tagText, $fTag, $accent, ($tagRect.X + 12), ($tagRect.Y + 5))
$tagPen.Dispose()

# widget grid: 2 columns x 3 rows
$states = @(
    @{ File = '01-working-dark.png'; Caption = '工作中 · 秒级滚动' },
    @{ File = '02-lunch-dark.png';   Caption = '午休 · 金额冻结，倒计时切到下午' },
    @{ File = '03-before-dark.png';  Caption = '待开工 · 只倒计时，不计薪' },
    @{ File = '04-done-dark.png';    Caption = '已下班 · 满额，自动指向下一个工作日' },
    @{ File = '05-off-dark.png';     Caption = '休息日 · 灰显，只显示「已到周末」' }
)

$x0 = 56
$y0 = 150
$cw = 400
$ch = 272
$gap = 20

for ($i = 0; $i -lt $states.Count; $i++) {
    $col = $i % 2
    $row = [math]::Floor($i / 2)
    $x = $x0 + $col * ($cw + $gap)
    $y = $y0 + $row * ($ch + $gap + 26)
    $img = [System.Drawing.Image]::FromFile((Join-Path $shots $states[$i].File))
    Draw-Rounded $g $img $x $y $cw $ch 8
    $img.Dispose()
    $g.DrawString($states[$i].Caption, $fCap, $dim, ($x + 2), ($y + $ch + 7))
}

# settings panel on the right
$sx = 936
$sy = 150
$sImg = [System.Drawing.Image]::FromFile((Join-Path $shots '09-settings-dark.png'))
Draw-Rounded $g $sImg $sx $sy 440 760 8
$sImg.Dispose()
$g.DrawString('设置 · 月薪 / 两段班次 / 工作日 / 节假日 / 外观', $fCap, $dim, ($sx + 2), ($sy + 760 + 7))

# bottom notes
$ny = $y0 + 3 * ($ch + $gap + 26) + 4
$g.DrawString('口径：日薪 = 月薪 ÷ 当月应出勤工作日（含 2026 法定节假日与调休补班），今日已赚按时段线性累计。', $fNote, $gray, 58, $ny)
$g.DrawString('金额按「已工作秒数 / 应工作秒数」线性累计，向下取整到分；午休、休息日、下班后不再增长。', $fNote, $gray, 58, ($ny + 24))

$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("sheet written: " + $out + " " + (Get-Item $out).Length + " bytes")

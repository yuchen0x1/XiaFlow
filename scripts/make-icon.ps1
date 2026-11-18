# Generates the XiaFlow brand icon (multi-size .ico with PNG entries) + PNG preview.
param([string]$OutPath = "src/XiaFlow.App/Assets/XiaFlow.ico")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-BrandImage([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2, [int]($size * 0.22))
    $m = [Math]::Max(1, [int]($size * 0.03))
    $w = $size - 2 * $m
    $rect = New-Object System.Drawing.Rectangle($m, $m, $w, $w)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 16, 20, 38),
        [System.Drawing.Color]::FromArgb(255, 34, 28, 74),
        55.0)
    $g.FillRectangle($bg, $rect)

    $penW = [Math]::Max(1.5, $size * 0.02)
    $borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 91, 140, 255)), $penW
    $g.DrawPath($borderPen, $path)

    # Archive box
    $cx = $size / 2.0
    $bw = $size * 0.44
    $bh = $size * 0.30
    $bx = $cx - $bw / 2
    $by = $size * 0.38
    $stroke = [Math]::Max(2, $size * 0.05)
    $white = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 238, 242, 255)), $stroke
    $white.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # Lid line
    $g.DrawLine($white, $bx, $by + $bh * 0.30, $bx + $bw, $by + $bh * 0.30)
    # Body outline
    $g.DrawRectangle($white, [float]($bx), [float]($by + $bh * 0.30), [float]($bw), [float]($bh * 0.70))

    # Flow wave across the body
    $flowRect = New-Object System.Drawing.RectangleF([float]$bx, [float]($by - $size * 0.05), [float]$bw, [float]($size * 0.3))
    $flow = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $flowRect,
        [System.Drawing.Color]::FromArgb(255, 91, 140, 255),
        [System.Drawing.Color]::FromArgb(255, 124, 108, 255),
        0.0)
    $flowPen = New-Object System.Drawing.Pen ($flow, [Math]::Max(2, $size * 0.042))
    $flowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $flowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $y1 = $by + $bh * 0.62
    $amp = $bh * 0.12
    $x1 = $bx + $bw * 0.12; $x2 = $bx + $bw * 0.50; $x3 = $bx + $bw * 0.88
    $g.DrawBezier($flowPen,
        (New-Object System.Drawing.PointF([float]$x1, [float]$y1)),
        (New-Object System.Drawing.PointF([float]$x2, [float]($y1 - $amp))),
        (New-Object System.Drawing.PointF([float]$x2, [float]($y1 - $amp))),
        (New-Object System.Drawing.PointF([float]$x3, [float]$y1)))

    # Arrow down into the box
    $ay = $by - $size * 0.095
    $arrowTop = $by - $size * 0.16
    $g.DrawLine($white, [float]$cx, [float]$arrowTop, [float]$cx, [float]$ay)
    $aw = $size * 0.05
    $ah = $size * 0.05
    $pts = @(
        (New-Object System.Drawing.PointF([float]$cx, [float]($ay + $ah * 0.8))),
        (New-Object System.Drawing.PointF([float]($cx - $aw), [float]($ay - $ah * 0.35))),
        (New-Object System.Drawing.PointF([float]($cx + $aw), [float]($ay - $ah * 0.35)))
    )
    $g.FillPolygon($white.Brush, $pts)

    $g.Dispose()
    return $bmp
}

function Get-BmpIconEntry([System.Drawing.Bitmap]$resized) {
    $s = $resized.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $resized.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $xor = New-Object byte[] ($s * $s * 4)
    $rowBuf = New-Object byte[] $stride
    for ($y = 0; $y -lt $s; $y++) {
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $rowBuf, 0, $stride)
        $srcRow = [IntPtr]::Add($data.Scan0, $y * $stride)
        $dstOff = ($s - 1 - $y) * $s * 4
        for ($x = 0; $x -lt $s; $x++) {
            $b = $rowBuf[$x * 4]; $gg = $rowBuf[$x * 4 + 1]; $r = $rowBuf[$x * 4 + 2]; $a = $rowBuf[$x * 4 + 3]
            $xor[$dstOff + $x * 4]     = $b
            $xor[$dstOff + $x * 4 + 1] = $gg
            $xor[$dstOff + $x * 4 + 2] = $r
            $xor[$dstOff + $x * 4 + 3] = $a
        }
    }
    $resized.UnlockBits($data)
    # AND mask: rows padded to 4 bytes, all opaque
    $maskRow = [Math]::Ceiling($s / 8.0)
    $maskStride = [Math]::Ceiling($maskRow / 4.0) * 4
    $mask = New-Object byte[] ($maskStride * $s)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]0); $bw.Write([UInt32](($s * $s * 4) + $mask.Length))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    $bw.Write($xor); $bw.Write($mask)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

function Save-Ico([System.Drawing.Bitmap]$src, [string]$path, [int[]]$sizes) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0)  # reserved
    $bw.Write([UInt16]1)  # type: icon
    $bw.Write([UInt16]$sizes.Count)
    $entries = @()
    foreach ($s in $sizes) {
        $img = New-Object System.Drawing.Bitmap($src, $s, $s)
        if ($s -le 64) {
            $entries += ,@($s, (Get-BmpIconEntry $img))
        } else {
            $pngMs = New-Object System.IO.MemoryStream
            $img.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
            $entries += ,@($s, $pngMs.ToArray())
            $pngMs.Dispose()
        }
        $img.Dispose()
    }
    $offset = 6 + 16 * $sizes.Count
    foreach ($e in $entries) {
        $s = $e[0]; $data = [byte[]]$e[1]
        $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write([Byte]0)
        $bw.Write([Byte]0)
        $bw.Write([UInt16]1)
        $bw.Write([UInt16]32)
        $bw.Write([UInt32]$data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($e in $entries) { $bw.Write([byte[]]$e[1]) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

$src = New-BrandImage 256
$full = Join-Path (Get-Location) $OutPath
$dir = Split-Path $full -Parent
if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$preview = Join-Path $dir 'XiaFlow-preview.png'
$src.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)

Save-Ico $src $full @(256, 128, 64, 48, 32, 16)
$src.Dispose()

# Validate ICO loads
$check = [System.Drawing.Image]::FromFile($full)
Write-Host "ICO_VALIDATE size=$($check.Width)x$($check.Height)"
$check.Dispose()
Write-Host "ICON_OK $full"

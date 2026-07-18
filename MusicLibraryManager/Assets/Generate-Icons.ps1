param(
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-BrandBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $Size / 256.0

    $shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(168, 13, 79, 89))
    $sky = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 55, 157, 218))
    $ocean = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 8, 127, 140))
    $paper = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(250, 247, 251, 252))
    $groove = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 155, 220, 223), [Math]::Max(1, 7 * $scale))
    $detail = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 247, 251, 252), [Math]::Max(1, 10 * $scale))
    $detail.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $detail.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $paths = @(
        (New-RoundedRectanglePath (32 * $scale) (50 * $scale) (142 * $scale) (154 * $scale) (28 * $scale)),
        (New-RoundedRectanglePath (55 * $scale) (32 * $scale) (150 * $scale) (164 * $scale) (30 * $scale)),
        (New-RoundedRectanglePath (78 * $scale) (54 * $scale) (150 * $scale) (164 * $scale) (30 * $scale))
    )

    try {
        $graphics.FillPath($shadow, $paths[0])
        $graphics.FillPath($sky, $paths[1])
        $graphics.FillPath($ocean, $paths[2])
        $graphics.FillEllipse($paper, 101 * $scale, 84 * $scale, 104 * $scale, 104 * $scale)
        $graphics.DrawEllipse($groove, 118 * $scale, 101 * $scale, 70 * $scale, 70 * $scale)
        $graphics.FillEllipse($ocean, 142 * $scale, 125 * $scale, 22 * $scale, 22 * $scale)
        $graphics.DrawLine($detail, 94 * $scale, 82 * $scale, 148 * $scale, 82 * $scale)
    }
    finally {
        $paths | ForEach-Object { $_.Dispose() }
        $shadow.Dispose()
        $sky.Dispose()
        $ocean.Dispose()
        $paper.Dispose()
        $groove.Dispose()
        $detail.Dispose()
        $graphics.Dispose()
    }

    return $bitmap
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngPaths = @()

foreach ($size in $sizes) {
    $path = Join-Path $OutputDirectory "AppIcon-$size.png"
    $bitmap = New-BrandBitmap $size
    try {
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
    $pngPaths += $path
}

$iconPath = Join-Path $OutputDirectory 'AppIcon.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $images = @()
    foreach ($pngPath in $pngPaths) {
        $images += ,([byte[]][System.IO.File]::ReadAllBytes($pngPath))
    }
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + (16 * $images.Count)

    for ($index = 0; $index -lt $images.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated $($sizes.Count) PNG assets and $iconPath"

[CmdletBinding()]
param(
    [string]$OriginalDirectory,
    [string]$AvaloniaDirectory,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OriginalDirectory)) {
    $OriginalDirectory = Join-Path $PSScriptRoot '..\artifacts\studio-optical\original'
}
if ([string]::IsNullOrWhiteSpace($AvaloniaDirectory)) {
    $AvaloniaDirectory = Join-Path $PSScriptRoot '..\artifacts\studio-optical\avalonia'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\studio-optical\comparisons'
}

$pages = 'Home', 'Library', 'Health', 'Ingest', 'Organize', 'Operations', 'Settings'
$logicalWidth = 1440
$logicalHeight = 900
$labelHeight = 40

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($page in $pages) {
    $originalPath = Join-Path $OriginalDirectory "$page.png"
    $avaloniaPath = Join-Path $AvaloniaDirectory "$page.png"
    if (-not (Test-Path -LiteralPath $originalPath) -or -not (Test-Path -LiteralPath $avaloniaPath)) {
        throw "Missing an optical reference for $page."
    }

    $original = [Drawing.Image]::FromFile($originalPath)
    $avalonia = [Drawing.Image]::FromFile($avaloniaPath)
    $comparison = [Drawing.Bitmap]::new($logicalWidth * 2, $logicalHeight + $labelHeight)
    $graphics = [Drawing.Graphics]::FromImage($comparison)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(13, 20, 23))
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($original, [Drawing.Rectangle]::new(0, $labelHeight, $logicalWidth, $logicalHeight))
        $graphics.DrawImage($avalonia, [Drawing.Rectangle]::new($logicalWidth, $labelHeight, $logicalWidth, $logicalHeight))

        $font = [Drawing.Font]::new('Segoe UI', 13, [Drawing.FontStyle]::Bold)
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(234, 243, 242))
        $divider = [Drawing.Pen]::new([Drawing.Color]::FromArgb(61, 85, 91), 2)
        try {
            $graphics.DrawString("Existing Studio - $page", $font, $brush, 14, 10)
            $graphics.DrawString("Avalonia - $page", $font, $brush, $logicalWidth + 14, 10)
            $graphics.DrawLine($divider, $logicalWidth, 0, $logicalWidth, $logicalHeight + $labelHeight)
        }
        finally {
            $divider.Dispose()
            $brush.Dispose()
            $font.Dispose()
        }

        $comparison.Save((Join-Path $OutputDirectory "$page.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $comparison.Dispose()
        $avalonia.Dispose()
        $original.Dispose()
    }
}

Write-Host "Created $($pages.Count) comparisons in $OutputDirectory"

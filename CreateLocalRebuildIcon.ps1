param(
    [string]$SourceIcon = (Join-Path $PSScriptRoot 'OpenTween\Resources\4b.ico'),
    [string]$OutputIcon = (Join-Path $PSScriptRoot 'OpenTween\Resources\LocalRebuild.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$baseIcon = [System.Drawing.Icon]::new($SourceIcon, 256, 256)
$baseBitmap = $baseIcon.ToBitmap()
$frames = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $bitmap = [System.Drawing.Bitmap]::new(
            $size,
            $size,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
        )
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $badgeBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 238, 112, 37)
        )
        $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $whitePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.5, $size * 0.045))

        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($baseBitmap, 0, 0, $size, $size)

            $badgeSize = $size * 0.47
            $inset = $size * 0.015
            $badgeRect = [System.Drawing.RectangleF]::new(
                $size - $badgeSize - $inset,
                $size - $badgeSize - $inset,
                $badgeSize,
                $badgeSize
            )
            $graphics.FillEllipse($badgeBrush, $badgeRect)

            $centerX = $badgeRect.X + $badgeRect.Width * 0.5
            $lineTop = $badgeRect.Y + $badgeRect.Height * 0.24
            $lineBottom = $badgeRect.Y + $badgeRect.Height * 0.65
            $graphics.DrawLine($whitePen, $centerX, $lineTop, $centerX, $lineBottom)

            $dotDiameter = [Math]::Max(1.5, $size * 0.075)
            $graphics.FillEllipse(
                $whiteBrush,
                $centerX - $dotDiameter * 0.5,
                $badgeRect.Y + $badgeRect.Height * 0.79,
                $dotDiameter,
                $dotDiameter
            )

            $memory = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add([pscustomobject]@{
                    Size = $size
                    Data = $memory.ToArray()
                })
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $whitePen.Dispose()
            $whiteBrush.Dispose()
            $badgeBrush.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $directory = [System.IO.Path]::GetDirectoryName($OutputIcon)
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $stream = [System.IO.File]::Create($OutputIcon)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)

        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Data.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Data.Length
        }

        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame.Data)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}
finally {
    $baseBitmap.Dispose()
    $baseIcon.Dispose()
}

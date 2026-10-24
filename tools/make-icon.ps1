# Regenerates src/PinPoint.App/Assets/PinPoint.ico (+ PNG tile) from the Segoe Fluent "pinned" glyph.
# Run with Windows PowerShell 5.1:  powershell -File tools\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\PinPoint.App\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile with a Windows-blue gradient.
    $r = [Math]::Max(2, [int]($size * 0.22))
    $rect = New-Object System.Drawing.Rectangle 0, 0, ($size - 1), ($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 76, 194, 255)), ([System.Drawing.Color]::FromArgb(255, 0, 95, 184)), 45
    $g.FillPath($brush, $path)

    # Pin glyph: Segoe Fluent "Pin" outline (U+E718, has the needle) + "PinnedFill" body (U+E841).
    $font = New-Object System.Drawing.Font 'Segoe Fluent Icons', ($size * 0.58), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $glyphRect = New-Object System.Drawing.RectangleF 0, ($size * 0.03), $size, $size
    $g.DrawString([string][char]0xE718, $font, [System.Drawing.Brushes]::White, $glyphRect, $fmt)
    $g.DrawString([string][char]0xE841, $font, [System.Drawing.Brushes]::White, $glyphRect, $fmt)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'PinPoint.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'PinPoint.ico'), $out.ToArray())
Write-Output "Wrote $(Join-Path $OutDir 'PinPoint.ico')"

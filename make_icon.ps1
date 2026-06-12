Add-Type -AssemblyName System.Drawing
$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = New-Object System.Collections.ArrayList
foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 30, 30))
    $g.FillRectangle($bg, 0, 0, $sz, $sz)

    $accentColor = [System.Drawing.Color]::FromArgb(78, 201, 176)
    $borderPen = New-Object System.Drawing.Pen ($accentColor), ([float]([Math]::Max(1, $sz / 32)))
    $inset = [Math]::Max(1, $sz / 16)
    $g.DrawRectangle($borderPen, $inset, $inset, $sz - $inset*2 - 1, $sz - $inset*2 - 1)

    $accent = New-Object System.Drawing.SolidBrush $accentColor
    $fontSize = [float]($sz * 0.44)
    $font = New-Object System.Drawing.Font "Consolas", $fontSize, ([System.Drawing.FontStyle]::Bold)
    $text = ">_"
    $ts = $g.MeasureString($text, $font)
    $g.DrawString($text, $font, $accent, ($sz - $ts.Width) / 2, ($sz - $ts.Height) / 2 - ($sz * 0.03))

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $null = $pngs.Add($ms.ToArray())

    $g.Dispose(); $bmp.Dispose(); $font.Dispose(); $borderPen.Dispose()
    $bg.Dispose(); $accent.Dispose()
}

$out = [System.IO.File]::Open("E:\cmd\app.ico", [System.IO.FileMode]::Create)
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$sizes.Count)
$offset = 6 + $sizes.Count * 16
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = if ($sizes[$i] -eq 256) { 0 } else { [byte]$sizes[$i] }
    $w.Write([byte]$s)
    $w.Write([byte]$s)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p, 0, $p.Length) }
$w.Close()
$out.Close()
Write-Host "icon written: E:\cmd\app.ico"

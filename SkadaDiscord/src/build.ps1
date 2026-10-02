# Сборка SkadaDiscord.exe встроенным в Windows компилятором C# (.NET Framework 4).
# Запуск: powershell -ExecutionPolicy Bypass -File src\build.ps1   (необязательно: -OutDir <папка>)
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$out = if ($OutDir) { $OutDir } else { Split-Path $src -Parent }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

# --- значок: полоски как в Skada на фоне цвета Discord (16/32/48 BMP + 256 PNG) ---
Add-Type -AssemblyName System.Drawing
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 64.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 14 * $s; $w = 64 * $s - 1
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 88, 101, 242))), $path)
    $colors = @(@(255, 196, 31, 59), @(255, 105, 204, 240), @(255, 255, 125, 10), @(255, 255, 255, 255))
    $lens = @(44, 36, 28, 18)
    for ($i = 0; $i -lt 4; $i++) {
        $c = $colors[$i]
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($c[0], $c[1], $c[2], $c[3]))
        $g.FillRectangle($brush, [float](10 * $s), [float]((11 + $i * 11) * $s), [float]($lens[$i] * $s), [float](8 * $s))
    }
    $g.Dispose()
    return $bmp
}
$icoPath = Join-Path $src 'app.ico'
$entries = @()
foreach ($size in 16, 32, 48, 256) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    if ($size -eq 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $size; $x++) { $p = $bmp.GetPixel($x, $y); $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A) } }
        $maskRow = [int]([math]::Ceiling($size / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $entries += , @($size, $ms.ToArray())
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $sz = if ($e[0] -ge 256) { 0 } else { $e[0] }
    $w.Write([byte]$sz); $w.Write([byte]$sz); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e[1].Length); $w.Write([int]$offset)
    $offset += $e[1].Length
}
foreach ($e in $entries) { $w.Write($e[1]) }
$w.Close()

# --- компиляция ---
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $out 'SkadaDiscord.exe'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$icoPath" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll /r:System.Net.Http.dll $files
if ($LASTEXITCODE -ne 0) { throw "ошибка компиляции" }
Write-Host "Готово: $exe"

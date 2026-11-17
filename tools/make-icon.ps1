# Gera src/Claudin/Assets/Claudin.ico (16 a 256 px, entradas PNG).
# Desenho: quadrado arredondado escuro com dois círculos sobrepostos (dois perfis).
# Uso: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Claudin\Assets\Claudin.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); $p
}

function New-IconPng([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
  $pad = [single]([Math]::Max(0.5, $s * 0.04))
  $bg = New-RoundedPath $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ([single]($s * 0.22))
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x2B, 0x2B, 0x2B))), $bg)
  if ($s -ge 32) { $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x55, 0x55, 0x55)), ([single]($s / 64))), $bg) }
  $d = [single]($s * 0.46); $cy = [single](($s - $d) / 2)
  $left = [single]($s * 0.17); $right = [single]($s - $s * 0.17 - $d)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57))), $left, $cy, $d, $d)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 0x4A, 0x8F, 0xE7))), $right, $cy, $d, $d)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  if ($s -ge 256) {
    # 256 px: PNG (padrão do Windows Vista+)
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  } else {
    # Demais tamanhos: DIB 32 bpp (BITMAPINFOHEADER + BGRA de baixo para cima + máscara AND), compatível com tudo
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
  }
  $bmp.Dispose()
  , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $images[$i].Length
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose()
"Gerado: $((Resolve-Path $out).Path) ($((Get-Item $out).Length) bytes)"

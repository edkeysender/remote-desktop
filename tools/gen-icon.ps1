# Generates a multi-resolution Remotler .ico from the brand mark (gradient rounded
# square + the "remotler" roof glyph). Emits classic 32bpp BMP/DIB frames for maximum
# shell + toolchain compatibility. Run: powershell -File tools/gen-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'assets\remotler.ico'
$sizes = 16,24,32,48,64,128,256

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)

  $rad = [Math]::Max(2, [int]($s * 0.22)); $d = $rad*2
  $rect = New-Object System.Drawing.Rectangle(0,0,$s,$s)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0,0,$d,$d,180,90); $path.AddArc($s-$d,0,$d,$d,270,90)
  $path.AddArc($s-$d,$s-$d,$d,$d,0,90); $path.AddArc(0,$s-$d,$d,$d,90,90); $path.CloseFigure()
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::White,[System.Drawing.Color]::White,135.0)
  $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
  $blend.Colors = @(
    [System.Drawing.Color]::FromArgb(0x5B,0x5B,0xF5),
    [System.Drawing.Color]::FromArgb(0x9D,0x5C,0xFF),
    [System.Drawing.Color]::FromArgb(0x3E,0xC8,0xFF))
  $blend.Positions = @(0.0,0.55,1.0); $br.InterpolationColors = $blend
  $g.FillPath($br,$path)

  $scale = $s/24.0
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White,[float]([Math]::Max(1.2,2.2*$scale)))
  $pen.StartCap='Round';$pen.EndCap='Round';$pen.LineJoin='Round'
  function P([double]$x,[double]$y){New-Object System.Drawing.PointF([float]($x*$scale),[float]($y*$scale))}
  $g.DrawLines($pen,@((P 3 18),(P 12 4),(P 21 18)))
  $g.DrawLines($pen,@((P 7.5 18),(P 12 11),(P 16.5 18)))
  $g.Dispose()

  # extract BGRA top-down, then build a bottom-up DIB (color + empty AND mask)
  $rectL = New-Object System.Drawing.Rectangle(0,0,$s,$s)
  $bd = $bmp.LockBits($rectL,'ReadOnly',[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $bd.Stride; $buf = New-Object byte[] ($stride*$s)
  [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0,$buf,0,$buf.Length)
  $bmp.UnlockBits($bd); $bmp.Dispose()

  $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter($ms)
  # BITMAPINFOHEADER (height doubled for color+mask)
  $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s*2))
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
  $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
  for ($y=$s-1; $y -ge 0; $y--) { $bw.Write($buf, $y*$stride, $stride) }   # color, bottom-up
  $maskRow = [int]([Math]::Floor(($s+31)/32)*4)
  $bw.Write((New-Object byte[] ($maskRow*$s)))                            # AND mask (zeros)
  $bw.Flush(); return ,$ms.ToArray()
}

$frames = @(); foreach ($s in $sizes) { $frames += ,(New-Frame $s) }

$fs = [System.IO.File]::Open($out,'Create'); $bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i=0;$i -lt $sizes.Count;$i++) {
  $s=$sizes[$i]; $len=$frames[$i].Length
  $bw.Write([Byte]($(if($s -ge 256){0}else{$s}))); $bw.Write([Byte]($(if($s -ge 256){0}else{$s})))
  $bw.Write([Byte]0); $bw.Write([Byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush();$bw.Close();$fs.Close()
Write-Host "Wrote $out ($([Math]::Round((Get-Item $out).Length/1kb,1)) KB, $($sizes.Count) sizes)"

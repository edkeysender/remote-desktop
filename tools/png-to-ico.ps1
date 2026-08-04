# Build a multi-resolution .ico from a source PNG (center-cropped to square).
# Emits classic 32bpp BMP/DIB frames for maximum shell + toolchain compatibility.
#   powershell -File tools/png-to-ico.ps1 -In assets/remotler-icon.png -Out assets/remotler.ico
param(
  [string]$In  = 'assets/remotler-icon.png',
  [string]$Out = 'assets/remotler.ico'
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src  = [System.Drawing.Image]::FromFile((Join-Path $root $In))
$sizes = 16,24,32,48,64,128,256

# Center square crop of the source.
$side = [Math]::Min($src.Width, $src.Height)
$cx = [int](($src.Width  - $side) / 2)
$cy = [int](($src.Height - $side) / 2)

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $dst = New-Object System.Drawing.Rectangle(0,0,$s,$s)
  $g.DrawImage($src, $dst, $cx, $cy, $side, $side, [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose()

  $rectL = New-Object System.Drawing.Rectangle(0,0,$s,$s)
  $bd = $bmp.LockBits($rectL,'ReadOnly',[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $bd.Stride; $buf = New-Object byte[] ($stride*$s)
  [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0,$buf,0,$buf.Length)
  $bmp.UnlockBits($bd); $bmp.Dispose()

  $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s*2))
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
  $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
  for ($y=$s-1; $y -ge 0; $y--) { $bw.Write($buf, $y*$stride, $stride) }
  $maskRow = [int]([Math]::Floor(($s+31)/32)*4)
  $bw.Write((New-Object byte[] ($maskRow*$s)))
  $bw.Flush(); return ,$ms.ToArray()
}

$frames = @(); foreach ($s in $sizes) { $frames += ,(New-Frame $s) }
$src.Dispose()

$outPath = Join-Path $root $Out
$fs = [System.IO.File]::Open($outPath,'Create'); $bw = New-Object System.IO.BinaryWriter($fs)
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
Write-Host "Wrote $outPath ($([Math]::Round((Get-Item $outPath).Length/1kb,1)) KB, $($sizes.Count) sizes)"

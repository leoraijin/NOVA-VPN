param([string]$AssetDirectory=(Join-Path $PSScriptRoot '..\Assets'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
[xml]$source=Get-Content -LiteralPath (Join-Path $AssetDirectory 'nova-desktop-v3.svg') -Raw
function Brush([string]$color) {
 if(-not $color -or $color -eq 'none'){return $null}
 return [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($color))
}
function Render([int]$size) {
 $visual=[Windows.Media.DrawingVisual]::new();$drawing=$visual.RenderOpen()
 try {
  $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/128.0,$size/128.0))
  $gradient=[Windows.Media.LinearGradientBrush]::new()
  $gradient.StartPoint=[Windows.Point]::new(0,0);$gradient.EndPoint=[Windows.Point]::new(1,1)
  foreach($stop in $source.svg.defs.linearGradient.stop){$gradient.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color'),[double]$stop.offset))}
  $rect=$source.svg.rect
  $drawing.DrawRoundedRectangle($gradient,[Windows.Media.Pen]::new((Brush $rect.stroke),[double]$rect.'stroke-width'),[Windows.Rect]::new([double]$rect.x,[double]$rect.y,[double]$rect.width,[double]$rect.height),[double]$rect.rx,[double]$rect.rx)
  foreach($path in $source.svg.path) {
   $pen=$null
   if($path.stroke){$pen=[Windows.Media.Pen]::new((Brush $path.stroke),[double]$path.'stroke-width');$pen.StartLineCap=$pen.EndLineCap=[Windows.Media.PenLineCap]::Round;$pen.LineJoin=[Windows.Media.PenLineJoin]::Round}
   $drawing.DrawGeometry((Brush $path.fill),$pen,[Windows.Media.Geometry]::Parse($path.d))
  }
  $drawing.Pop()
 } finally {$drawing.Close()}
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.MemoryStream]::new()
 try {$encoder.Save($stream);return ,$stream.ToArray()}finally{$stream.Dispose()}
}
$sizes=@(16,20,24,32,40,48,64,128,256)
$frames=@();foreach($size in $sizes){$frames+=,(Render $size)}
$file=[IO.File]::Create((Join-Path $AssetDirectory 'nova-desktop-v3.ico'))
$writer=[IO.BinaryWriter]::new($file)
try {
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
 $offset=6+16*$sizes.Count
 for($i=0;$i -lt $sizes.Count;$i++) {
  $edge=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
  $writer.Write([byte]$edge);$writer.Write([byte]$edge);$writer.Write([byte]0);$writer.Write([byte]0)
  $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
  $offset+=$frames[$i].Length
 }
 foreach($frame in $frames){$writer.Write([byte[]]$frame)}
}finally{$writer.Dispose();$file.Dispose()}
[IO.File]::WriteAllBytes((Join-Path $AssetDirectory 'nova-desktop-v3.png'),(Render 512))
Write-Output 'Desktop icon rendered from editable SVG: 9 alpha ICO sizes (16..256) and 512px PNG.'

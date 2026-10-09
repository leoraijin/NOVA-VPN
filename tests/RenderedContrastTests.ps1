param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object Windows.Application
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$design=$assembly.GetType('NovaVpn.VisualDesign');$paletteType=$assembly.GetType('NovaVpn.ThemePalette')
function Linear($v){$v=$v/255.0;if($v -le .04045){return $v/12.92};return [Math]::Pow(($v+.055)/1.055,2.4)}
function Luma($r,$g,$b){return .2126*(Linear $r)+.7152*(Linear $g)+.0722*(Linear $b)}
$reports=@()
foreach($theme in $paletteType.GetField('ThemeNames').GetValue($null)) {
 foreach($controlLayer in @($false,$true)) {
  $v=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences'));$v.LoadThemeSettings($theme)
  $p=$paletteType.GetMethod('Create').Invoke($null,@($v))
  $root=New-Object Windows.Controls.Grid;$root.Width=320;$root.Height=160
  $backdrop=$design.GetMethod('Backdrop').Invoke($null,@($v));$root.Children.Add($backdrop)|Out-Null
  $card=New-Object Windows.Controls.Border;$card.Margin=[Windows.Thickness]::new(10);$card.Padding=[Windows.Thickness]::new(20)
  $text=New-Object Windows.Controls.TextBlock;$text.Text='Readable information';$text.FontSize=20;$text.FontWeight='SemiBold';$text.Foreground=[Windows.Media.SolidColorBrush]::new($p.Muted);$text.VerticalAlignment='Center'
  $card.Child=$text;$design.GetMethod('ApplyGlassSurface').Invoke($null,@($card.PSObject.BaseObject,$v,$controlLayer))|Out-Null
  $root.Children.Add($card)|Out-Null
  $window=New-Object Windows.Window;$window.Content=$root;$window.SizeToContent='WidthAndHeight';$window.WindowStyle='None';$window.Left=-32000;$window.Top=-32000;$window.ShowInTaskbar=$false
  try {
   $window.Show();$window.UpdateLayout()
   $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(320,160,96,96,[Windows.Media.PixelFormats]::Pbgra32);$image.Render($root)
   $pixels=New-Object byte[] (320*160*4);$image.CopyPixels($pixels,1280,0)
   $foreground=if($p.IsLight){1.0}else{0.0}
   for($y=65;$y -lt 95;$y++){for($x=30;$x -lt 200;$x++){$i=($y*320+$x)*4;$l=Luma $pixels[$i+2] $pixels[$i+1] $pixels[$i];$foreground=if($p.IsLight){[Math]::Min($foreground,$l)}else{[Math]::Max($foreground,$l)}}}
   $i=(80*320+270)*4;$background=Luma $pixels[$i+2] $pixels[$i+1] $pixels[$i]
   $ratio=([Math]::Max($foreground,$background)+.05)/([Math]::Min($foreground,$background)+.05)
   $reports+=[pscustomobject]@{Theme=$theme;ControlLayer=$controlLayer;RenderedContrast=[Math]::Round($ratio,2)}
   if($ratio -lt 4.5){throw "Rendered secondary-text contrast below 4.5: $theme layer=$controlLayer ratio=$ratio"}
  }finally{$window.Close()}
 }
}
$reports|Format-Table
Write-Output 'PASS: rasterized secondary text against rendered built-in card and control backgrounds meets 4.5:1; arbitrary custom wallpaper is outside this test scope.'
$app.Shutdown()

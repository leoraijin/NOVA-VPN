param([string]$AssemblyPath,[string]$InstallerPath,[string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
Add-Type -AssemblyName System.Drawing
if($null -eq [Windows.Application]::Current){$app=New-Object Windows.Application}
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$window=[Activator]::CreateInstance($assembly.GetType('NovaVpn.MainWindow'),@($true))
try {
 if($null -eq $window.Icon -or $window.Icon.PixelWidth -lt 16){throw 'Missing window icon'}
 $nativeIcon=[Drawing.Icon]::ExtractAssociatedIcon([IO.Path]::GetFullPath($AssemblyPath))
 try {
  $nativeBitmap=$nativeIcon.ToBitmap()
  try {
   $color=$nativeBitmap.GetPixel([int]($nativeBitmap.Width/2),[int]($nativeBitmap.Height/2))
   if($color.G -lt 180 -or $color.B -lt 180 -or $color.R -gt 180){throw 'Executable icon did not receive cyan mobile design'}
  }finally{$nativeBitmap.Dispose()}
 }finally{$nativeIcon.Dispose()}
 $setup=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($InstallerPath))
 $method=$setup.GetType('NovaInstaller').GetMethod('CreateShortcut',[Reflection.BindingFlags]'Static,NonPublic')
 if(-not $WorkPath){$WorkPath=[IO.Path]::GetTempPath()}
 $folder=Join-Path $WorkPath ('NovaIconTest-'+[Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path (Join-Path $folder 'Assets') | Out-Null
 $source=Join-Path $PSScriptRoot '..\Assets\nova-desktop-v3.ico'
 Copy-Item -LiteralPath $source -Destination (Join-Path $folder 'Assets/nova-desktop-v3.ico')
 $decoder=[Windows.Media.Imaging.BitmapDecoder]::Create([Uri][IO.Path]::GetFullPath($source),[Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
 $sizes=@($decoder.Frames | ForEach-Object {$_.PixelWidth} | Sort-Object)
 if(($sizes -join ',') -ne '16,20,24,32,40,48,64,128,256'){throw 'ICO sizes missing'}
 foreach($frame in $decoder.Frames) {
  $pixels=New-Object byte[] ($frame.PixelWidth*$frame.PixelHeight*4)
  $converted=[Windows.Media.Imaging.FormatConvertedBitmap]::new($frame,[Windows.Media.PixelFormats]::Bgra32,$null,0)
  $converted.CopyPixels($pixels,$frame.PixelWidth*4,0)
  if($pixels[3] -ne 0){throw 'ICO corner lost transparency'}
  $center=4*([int][Math]::Floor($frame.PixelHeight/2)*$frame.PixelWidth+[int][Math]::Floor($frame.PixelWidth/2))
  if($pixels[$center+3] -ne 255){throw 'ICO tile has transparent center'}
 }
 $link=Join-Path $folder 'NOVA VPN.lnk'
 $method.Invoke($null,[object[]]@([string]$link,[string][IO.Path]::GetFullPath($AssemblyPath),[string]$folder,[string]'')) | Out-Null
 $shell=New-Object -ComObject WScript.Shell
 $shortcut=$shell.CreateShortcut($link)
 if($shortcut.IconLocation -notlike '*nova-desktop-v3.ico*'){throw 'Shortcut lost new explicit icon'}
 $iconPath=$shortcut.IconLocation.Substring(0,$shortcut.IconLocation.LastIndexOf(','))
 if($iconPath.Contains('"') -or -not [IO.File]::Exists($iconPath)){throw 'Shell icon path contains quotes or does not exist'}
 Write-Output ('PASS: embedded WPF window icon '+$window.Icon.PixelWidth+' px; shell shortcut points to installed ICO. Test-only shortcut: '+$link)
} finally { $window.Close() }

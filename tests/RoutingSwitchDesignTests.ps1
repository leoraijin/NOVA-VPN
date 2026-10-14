param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
if($null -eq [Windows.Application]::Current){$app=New-Object Windows.Application}
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath);[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow');$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
try{
 $state=$type.GetField('state',$flags).GetValue($window)
 $entry=[Activator]::CreateInstance($assembly.GetType('NovaVpn.ApplicationEntry'));$entry.Name='Browser';$entry.ExecutablePath='D:\NOVA-VPN\work\fixture-browser.exe';$entry.Source='Fixture'
 $routing=$assembly.GetType('NovaVpn.ApplicationRoutingService')
 foreach($theme in $assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)){
  $state.Visual.LoadThemeSettings($theme);$type.GetMethod('ApplyVisualPalette',$flags).Invoke($window,@())|Out-Null
  $panel=[Windows.Controls.StackPanel]::new()
  foreach($mode in @('Auto','Proxy','Direct')){
   $routing.GetMethod('SetRoute').Invoke($null,@($state.Routing,$entry.ExecutablePath,$mode))|Out-Null
   $item=$type.GetMethod('BuildApplicationRoutingRow',$flags).Invoke($window,@($entry,[Action]{}))
   $item.Width=760;$item.Height=84;$panel.Children.Add($item)|Out-Null
   $button=$item.Content.Children[2].Children[1]
   $button.ApplyTemplate()|Out-Null
   $root=[Windows.Media.VisualTreeHelper]::GetChild($button,0)
   if($root -isnot [Windows.Controls.Border] -or $root.Background.Color.A -ne 0 -or $root.BorderThickness.Left -ne 0){throw 'Visible square native button frame remains'}
   if($button.Content.CornerRadius.TopLeft -ne 13 -or $button.Content.Child.Width -ne 19){throw 'Pill/knob dimensions changed'}
   $expected=if($mode-eq 'Proxy'){'Right'}elseif($mode-eq 'Direct'){'Left'}else{'Center'}
   if($button.Content.Child.HorizontalAlignment.ToString()-ne $expected){throw 'Route position is wrong'}
   if([Windows.Automation.AutomationProperties]::GetName($button)-notlike 'Browser*'){throw 'Accessible route label missing'}
  }
  $palette=$type.GetField('palette',$flags).GetValue($window)
  $border=[Windows.Controls.Border]::new();$border.Background=[Windows.Media.SolidColorBrush]::new($palette.Surface);$border.Child=$panel
  $border.Measure([Windows.Size]::new(760,252));$border.Arrange([Windows.Rect]::new(0,0,760,252));$border.UpdateLayout()
  $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(760,252,96,96,[Windows.Media.PixelFormats]::Pbgra32);$image.Render($border)
  $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
  $file=[IO.File]::Create((Join-Path $WorkPath (($theme-replace '[^a-zA-Z0-9]','-')+'.png')));try{$encoder.Save($file)}finally{$file.Dispose()}
 }
 'PASS: five styles x three routes; no square frame; pill/knob shape; positions; accessibility; rendered previews.'
}finally{$window.Close()}

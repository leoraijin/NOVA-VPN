param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$profile=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VpnProfile'));$profile.Name='PL Poland - Warsaw'
$profiles=[Array]::CreateInstance($profile.GetType(),1);$profiles.SetValue($profile,0)
$healthType=[Collections.Generic.Dictionary``2].MakeGenericType([string],$assembly.GetType('NovaVpn.ServerMapHealth'))
$health=[Activator]::CreateInstance($healthType)
$visual=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences'));$visual.ThemeName='Windows 11 Dark'
$palette=$assembly.GetType('NovaVpn.ThemePalette').GetMethod('Create').Invoke($null,@($visual))
$script:chosen=$false;$select=[Action[object]]{param($p)$script:chosen=$true}
$map=$assembly.GetType('NovaVpn.ServerRegionMapService').GetMethod('BuildInteractiveCanvas').Invoke($null,@($profiles,$profile,[double]820,[double]400,$palette,$health,$select,$true))
$viewport=[Activator]::CreateInstance($assembly.GetType('NovaVpn.ServerMapViewport'),@($map))
function Assert($v,$m){if(-not $v){throw $m}}
$count=$map.Children.Count
$anchor=[Windows.Point]::new(410,200)
$viewport.ZoomAt($anchor,2)
Assert ($viewport.Zoom -eq 2 -and $viewport.Offset.X -eq -410 -and $viewport.Offset.Y -eq -200) 'zoom must preserve cursor anchor'
$viewport.PanBy([Windows.Vector]::new(20,30))
Assert ($viewport.Offset.X -eq -390 -and $viewport.Offset.Y -eq -170) 'pan must translate existing map'
$viewport.PanBy([Windows.Vector]::new(99999,99999));Assert ($viewport.Offset.X -eq 0 -and $viewport.Offset.Y -eq 0) 'positive bounds'
$viewport.PanBy([Windows.Vector]::new(-99999,-99999));Assert ($viewport.Offset.X -eq -820 -and $viewport.Offset.Y -eq -400) 'negative bounds'
$viewport.ZoomAt($anchor,999);Assert ($viewport.Zoom -eq 12) 'maximum zoom'
$viewport.ZoomAt($anchor,0.0001);Assert ($viewport.Zoom -eq 1 -and $viewport.Offset.X -eq 0) 'minimum zoom'
$viewport.ZoomAt($anchor,2);$viewport.ResetView();Assert ($viewport.Zoom -eq 1 -and $viewport.Offset.Y -eq 0) 'reset'
$viewport.ZoomAt($anchor,[double]::NaN);Assert ($viewport.Zoom -eq 1) 'invalid factor'
$dot=@($map.Children|Where-Object {$_.Tag -eq 'server-node'})[0]
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$up=[Windows.Input.MouseButtonEventArgs]::new([Windows.Input.InputManager]::Current.PrimaryMouseDevice,0,[Windows.Input.MouseButton]::Left)
$up.RoutedEvent=[Windows.UIElement]::PreviewMouseLeftButtonUpEvent
$viewport.GetType().GetField('dragging',$flags).SetValue($viewport,$true)
$viewport.GetType().GetMethod('OnPreviewMouseLeftButtonUp',$flags).Invoke($viewport,@($up))|Out-Null
$up.RoutedEvent=[Windows.UIElement]::MouseLeftButtonUpEvent;$dot.RaiseEvent($up)
Assert ($up.Handled -and -not $script:chosen) 'drag release must not select VPN server'
$click=[Windows.Input.MouseButtonEventArgs]::new([Windows.Input.InputManager]::Current.PrimaryMouseDevice,0,[Windows.Input.MouseButton]::Left)
$click.RoutedEvent=[Windows.UIElement]::PreviewMouseLeftButtonUpEvent
$viewport.GetType().GetMethod('OnPreviewMouseLeftButtonUp',$flags).Invoke($viewport,@($click))|Out-Null
$click.RoutedEvent=[Windows.UIElement]::MouseLeftButtonUpEvent;$dot.RaiseEvent($click)
Assert ($script:chosen) 'ordinary click must still select server'
$map.RefreshStatus();Assert ($count -eq $map.Children.Count -and $map.RenderTransform.Matrix.M11 -eq 1) 'health refresh must preserve viewport and geometry'
$viewport.ZoomAt([Windows.Point]::new(450,150),4)
Assert ($dot.RenderTransform.Value.M11 -eq 0.25) 'server markers must retain readable screen size when zooming'
$viewport.Measure([Windows.Size]::new(820,400));$viewport.Arrange([Windows.Rect]::new(0,0,820,400));$viewport.UpdateLayout()
$viewport.Background=[Windows.Media.SolidColorBrush]::new($palette.Bg)
$image=[Windows.Media.Imaging.RenderTargetBitmap]::new(820,400,96,96,[Windows.Media.PixelFormats]::Pbgra32);$image.Render($viewport)
$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null;$file=[IO.File]::Create((Join-Path $WorkPath 'zoomed-map.png'));try{$encoder.Save($file)}finally{$file.Dispose()}
'PASS: anchored zoom; pan and bounds; 1x/12x limits; reset; drag-click separation; map health and geometry reuse; zoomed render.'

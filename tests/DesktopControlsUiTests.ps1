param([Parameter(Mandatory=$true)][string]$AssemblyPath, [Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object Windows.Application
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
$state=$type.GetField('state',$flags).GetValue($window)
$root=$type.GetField('contentHost',$flags).GetValue($window)
$window.Left=-32000; $window.Top=-32000; $window.ShowInTaskbar=$false; $window.ShowActivated=$false
$WorkPath=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkPath)
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
$checks=0
function Assert($value,$message){$script:checks++; if(-not $value){throw $message}}
function Nodes($node) {
 $node
 for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++) { Nodes ([Windows.Media.VisualTreeHelper]::GetChild($node,$i)) }
}
function Named($name){@(Nodes $root | Where-Object {$_ -is [Windows.FrameworkElement] -and $_.Name -eq $name})[0]}
function Layout {
 $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
 $window.UpdateLayout()
}
function Bounds($element,$ancestor){$element.TransformToAncestor($ancestor).TransformBounds([Windows.Rect]::new($element.RenderSize))}
function Visible($element,$ancestor,$label) {
 $b=Bounds $element $ancestor
 Assert ($b.Width -gt 0 -and $b.Height -gt 0 -and $b.Left -ge -1 -and $b.Top -ge -1 -and $b.Right -le $ancestor.ActualWidth+1 -and $b.Bottom -le $ancestor.ActualHeight+1) "$label outside viewport: $b"
}
function Save-Image($name) {
 $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap([int][Math]::Ceiling($window.ActualWidth),[int][Math]::Ceiling($window.ActualHeight),96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($window);$encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create((Join-Path $WorkPath ($name+'.png')))
 try {$encoder.Save($stream)}finally{$stream.Dispose()}
}
try {
 $window.Show()
 $cases=@(
  @{Name='light';Theme='Windows 11 Light';Width=1200;Height=760;Scale=1;Dual=$false},
  @{Name='dark';Theme='Windows 11 Dark';Width=900;Height=760;Scale=1;Dual=$false},
  @{Name='compact';Theme='Windows 11 Dark';Width=680;Height=720;Scale=1;Dual=$false},
  @{Name='small-dual';Theme='Windows 11 Dark';Width=680;Height=460;Scale=1;Dual=$true},
  @{Name='large-text';Theme='Windows 11 Light';Width=680;Height=760;Scale=1.35;Dual=$true},
  @{Name='dual';Theme='Windows 11 Light';Width=1200;Height=760;Scale=1;Dual=$true}
 )
 foreach($case in $cases){
  $state.Routing.MultiServer=$null
  if($case.Dual){
   $settings=[Activator]::CreateInstance($assembly.GetType('NovaVpn.MultiServerSettings'))
   $settings.DefaultProfileId=$state.Profiles[0].Id;$settings.SecondaryProfileId=$state.Profiles[2].Id
   $state.Routing.MultiServer=$settings
  }
  $window.Width=$case.Width;$window.Height=$case.Height
  $window.ConfigureSnapshot($case.Theme,'','', '',[double]$case.Scale,$false)
  $window.ShowPageForSnapshot('home');Layout
  $root=$type.GetField('contentHost',$flags).GetValue($window)
  $card=Named 'DualVpnCard';$toggle=Named 'DualVpnToggle';$edit=Named 'ConfigureDualVpn'
  Assert ($null -ne $card -and $null -ne $toggle -and $null -ne $edit) 'Home dual controls missing'
  $labels=@(Nodes $root | Where-Object {$_ -is [Windows.Controls.TextBlock]} | ForEach-Object Text)
  Assert ($labels -contains 'Два VPN-конфига') 'Dual VPN heading missing'
  Assert ($edit.Content -eq $(if($case.Dual){'Конфиги и маршруты'}else{'Выбрать два конфига'})) 'Wrong configuration action'
  $names=Named 'DualVpnProfiles'
  Assert (($null -ne $names) -eq $case.Dual) 'Profile names do not reflect mode'
  if($names){Assert ($names.Text.Contains($state.Profiles[0].Name) -and $names.Text.Contains($state.Profiles[2].Name)) 'Selected configs missing'}
  Visible $toggle $card 'Dual toggle';Visible $edit $card 'Dual editor button'
  # The entry is visible without scrolling at ordinary desktop sizes; minimum height remains scrollable.
  if($case.Height -ge 720 -and $case.Scale -eq 1){Visible $toggle $root 'Home toggle on first screen'}
  $card.BringIntoView();Layout
  Visible $toggle $root 'Reachable home toggle'
  $edit.BringIntoView();Layout;Visible $edit $root 'Reachable home editor button'
  Save-Image ('home-'+$case.Name)
  $navigation=$type.GetField('navItems',$flags).GetValue($window)
  Assert (-not $navigation.ContainsKey('gaming')) 'Gaming navigation is still present'
  $window.ShowPageForSnapshot('gaming');Layout
  Assert ($type.GetField('currentPage',$flags).GetValue($window) -eq 'home') 'Removed gaming route is reachable'

  $window.ShowPageForSnapshot('servers');Layout
  $actions=Named 'ServerActions';$services=Named 'ServiceAvailability'
  Assert ($actions -is [Windows.Controls.Primitives.UniformGrid]) 'Server action grid missing'
  Assert ($null -ne $services) 'Service availability action missing'
  $buttons=@($actions.Children);$first=$buttons[0]
  foreach($button in $buttons){
   Assert ([Math]::Abs($button.ActualWidth-$first.ActualWidth) -lt 1 -and [Math]::Abs($button.ActualHeight-$first.ActualHeight) -lt 1) 'Action sizes differ'
   Visible $button $root 'Server action'
   $label=$button.Content
   if($label -is [Windows.Controls.TextBlock]){Assert ($label.ActualHeight -le $button.ActualHeight - 6) 'Button label clipped'}
  }
  for($i=0;$i -lt $buttons.Count;$i++){for($j=$i+1;$j -lt $buttons.Count;$j++){
   $a=Bounds $buttons[$i] $actions;$b=Bounds $buttons[$j] $actions
   Assert (-not $a.IntersectsWith($b)) 'Server actions overlap'
  }}
  Save-Image ('servers-'+$case.Name)
 }
 $state.Routing.MultiServer=$null
 $window.ShowPageForSnapshot('routing');Layout
 $labels=@(Nodes $root | Where-Object {$_ -is [Windows.Controls.TextBlock]} | ForEach-Object Text)
 Assert (-not ($labels -contains 'ПРИЛОЖЕНИЯ')) 'Removed legacy application section remains'
 Assert (-not @($labels|Where-Object {$_ -like 'Перетащите переключатель:*'}).Count) 'Removed application instructions remain'
 $edit=Named 'EditMultiServerRouting';Assert ($null -ne $edit) 'Multi-server editor entry removed with legacy block'
 Save-Image 'routing'
 $state.Routing.MultiServer=$settings
 $window.ShowPageForSnapshot('routing');Layout
 Assert ($null -ne (Named 'EditMultiServerRouting')) 'Active multi-server routing editor missing'
 Write-Output "PASS: $checks desktop UI checks; six window/theme/mode variants. Synthetic profiles only."
} finally {$window.Close();$app.Shutdown()}

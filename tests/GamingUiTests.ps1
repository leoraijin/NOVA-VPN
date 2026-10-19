param([Parameter(Mandatory=$true)][string]$AssemblyPath, [string]$ScreenshotPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object Windows.Application
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Nodes($node) {
  $node
  for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++) { Nodes ([Windows.Media.VisualTreeHelper]::GetChild($node,$i)) }
}
$window=[Activator]::CreateInstance($type,@($true))
$window.Left=-32000; $window.Top=-32000; $window.ShowInTaskbar=$false; $window.ShowActivated=$false
try {
  $window.Show()
  $library=$type.GetField('gamingLibrary',$flags).GetValue($window)
  if($library.Count -ne 3){throw 'Snapshot game library missing'}
  foreach($width in @(760,1100,1450)) {
    $window.Width=$width; $window.ShowPageForSnapshot('gaming'); $window.UpdateLayout()
    $root=$type.GetField('contentHost',$flags).GetValue($window)
    $nodes=@(Nodes $root)
    $labels=@($nodes | Where-Object {$_ -is [Windows.Controls.TextBlock]} | ForEach-Object Text)
    if(-not ($labels -contains 'Игровой режим')) {throw 'Gaming page missing'}
    foreach($gameName in @('Rust','Fortnite','War Thunder')){if($labels -notcontains $gameName){throw 'Game card missing'}}
    $type.GetMethod('SelectGamingGame',$flags).Invoke($window,[object[]]@($library[0]))
    $window.UpdateLayout()
    $nodes=@(Nodes ($type.GetField('contentHost',$flags).GetValue($window)))
    if(@($nodes | Where-Object {$_ -is [Windows.Controls.ComboBox] -or $_ -is [Windows.Controls.CheckBox]}).Count) {throw 'Automatic gaming UI exposes route choices'}
    if(@($nodes | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Играть' -and $_.IsEnabled}).Count) {throw 'Play enabled without automatic result'}
    if(-not @($nodes | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Подобрать конфиг'}).Count) {throw 'Automatic pick action missing'}
  }
  $state=$type.GetField('state',$flags).GetValue($window)
  $core=$type.GetField('core',$flags).GetValue($window)
  $network=$assembly.GetType('NovaVpn.GameCatalog').GetMethod('NetworkKey').Invoke($null,@())
  $stampType=$assembly.GetType('NovaVpn.GamingRecommendationStamp')
  $stamp=[Activator]::CreateInstance($stampType,[object[]]@($library[0],$state.Profiles,$network,$core.ActiveProfileId,$false,$core.Engine,$core.Status,[DateTime]::UtcNow))
  $result=[Activator]::CreateInstance($assembly.GetType('NovaVpn.AutomaticGamingResult'))
  $result.ProfileId=$state.Profiles[0].Id;$result.Message='Fixture recommendation'
  $type.GetField('gamingAutomaticResult',$flags).SetValue($window,$result)
  $type.GetField('gamingRecommendation',$flags).SetValue($window,$stamp)
  $window.ShowPageForSnapshot('gaming');$window.UpdateLayout()
  $nodes=@(Nodes ($type.GetField('contentHost',$flags).GetValue($window)))
  if(@($nodes | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Играть'}).Count -ne 1){throw 'Valid automatic result did not produce one Play action'}
  $oldServer=$state.Profiles[0].Server;$state.Profiles[0].Server='changed-fixture.invalid'
  $window.ShowPageForSnapshot('gaming');$window.UpdateLayout()
  if(@(Nodes ($type.GetField('contentHost',$flags).GetValue($window)) | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Играть'}).Count){throw 'Changed server retained Play action'}
  $state.Profiles[0].Server=$oldServer
  $type.GetField('gamingAutomaticResult',$flags).SetValue($window,$null)
  $type.GetField('gamingRecommendation',$flags).SetValue($window,$null)
  $state.KillSwitch=$true
  $session=[Activator]::CreateInstance($assembly.GetType('NovaVpn.GamingRoute'))
  $session.Game='Fixture';$session.Processes=@('fixture-game.exe')
  $type.GetField('gamingSession',$flags).SetValue($window,$session)
  $blocked=$false
  try {$type.GetMethod('EffectiveRouting',$flags).Invoke($window,@())} catch { $blocked=$true }
  if(-not $blocked){throw 'Direct game route bypassed Kill Switch'}
  $state.KillSwitch=$false
  $window.ShowPageForSnapshot('gaming');$window.UpdateLayout()
  if(@(Nodes ($type.GetField('contentHost',$flags).GetValue($window)) | Where-Object {$_ -is [Windows.Controls.ComboBox] -and $_.IsEnabled}).Count){throw 'Session inputs not locked'}
  $type.GetField('gamingSession',$flags).SetValue($window,$null)
  if($ScreenshotPath) {
    $window.Width=1100;$window.Height=900;$window.ShowPageForSnapshot('gaming');$window.UpdateLayout()
    $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$window.ActualWidth),([int]$window.ActualHeight),96,96,([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($window)
    $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.File]::Create([IO.Path]::GetFullPath($ScreenshotPath))
    try {$encoder.Save($stream)} finally {$stream.Dispose()}
  }
  Write-Output 'PASS: game cards at 3 widths, no manual route controls, one Play action only for a valid automatic result, changed-server invalidation, Kill Switch and session lock. Snapshot state only.'
} finally {$window.Close();$app.Shutdown()}

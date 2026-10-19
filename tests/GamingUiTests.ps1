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
  foreach($width in @(760,1100,1450)) {
    $window.Width=$width; $window.ShowPageForSnapshot('gaming'); $window.UpdateLayout()
    $root=$type.GetField('contentHost',$flags).GetValue($window)
    $nodes=@(Nodes $root)
    $labels=@($nodes | Where-Object {$_ -is [Windows.Controls.TextBlock]} | ForEach-Object Text)
    if(-not ($labels -contains 'Игровой режим')) {throw 'Gaming page missing'}
    $check=@($nodes | Where-Object {$_ -is [Windows.Controls.CheckBox]})
    if($check.Count -ne 1 -or $check[0].IsChecked) {throw 'Direct probes must default to opt-out'}
    if(@($nodes | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Включить игровой режим' -and $_.IsEnabled}).Count) {throw 'Apply enabled without measurements'}
  }
  $state=$type.GetField('state',$flags).GetValue($window)
  $state.KillSwitch=$true
  $window.ShowPageForSnapshot('gaming');$window.UpdateLayout()
  $nodes=@(Nodes ($type.GetField('contentHost',$flags).GetValue($window)))
  if(@($nodes | Where-Object {$_ -is [Windows.Controls.CheckBox] -and $_.IsEnabled}).Count) {throw 'Kill Switch allows direct probing'}
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
  Write-Output 'PASS: gaming page at 3 widths, default direct opt-out, measurement gate, Kill Switch and session lock. Snapshot state only.'
} finally {$window.Close();$app.Shutdown()}

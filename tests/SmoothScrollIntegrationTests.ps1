param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object Windows.Application
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$stateProperty=$assembly.GetType('NovaVpn.SmoothScrollBehavior').GetField('StateProperty',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
function Find-Scroll($node) {
 if($node -is [Windows.Controls.ScrollViewer] -and $null -ne $node.GetValue($stateProperty)) {return $node}
 for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++) {
  $found=Find-Scroll ([Windows.Media.VisualTreeHelper]::GetChild($node,$i))
  if($found){return $found}
 }
}
$window=[Activator]::CreateInstance($type,@($true))
$window.Left=-32000;$window.Top=-32000;$window.ShowInTaskbar=$false;$window.ShowActivated=$false
try {
 $window.Show();$count=0
 foreach($page in @('home','servers','routing','settings','diagnostics','engine')) {
  $window.ShowPageForSnapshot($page);$window.UpdateLayout()
  $scroll=Find-Scroll ($type.GetField('contentHost',$flags).GetValue($window))
  if($scroll) {
   if($null -eq $scroll.GetValue($stateProperty)){throw "Page scroll not attached: $page"}
   $count++
  }
 }
 if($count -lt 4){throw 'Insufficient actual page coverage'}
 Write-Output "PASS: smooth scroll attached to $count actual snapshot pages. No personal profiles loaded or VPN started."
} finally {$window.Close();$app.Shutdown()}

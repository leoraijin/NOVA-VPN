param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
$a=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$a
$app=New-Object Windows.Application;$app.ShutdownMode='OnExplicitShutdown'
$w=[Activator]::CreateInstance($a.GetType('NovaVpn.MainWindow'),[object[]]@($true))
$w.ShowInTaskbar=$false;$w.ShowActivated=$false;$w.WindowStartupLocation='Manual';$w.Left=-32000;$w.Top=-32000
function Buttons($node){if($node -is [Windows.Controls.Button]){$node};for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++){Buttons ([Windows.Media.VisualTreeHelper]::GetChild($node,$i))}}
try {
 $w.Show()
 foreach($theme in $a.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)) {
  foreach($width in @(800,1280)) {
   $w.Width=$width;$w.ConfigureSnapshot($theme,$null,$null,$null,1.4,$false);$w.UpdateLayout()
   $controls=@(Buttons $w | Where-Object {$_.Content -in @('Импорт VPN-конфигов','Экспорт VPN-конфигов')})
   if($controls.Count -ne 2){throw "Missing home transfer controls: $theme / $width"}
   $rects=@($controls|ForEach-Object {$point=$_.TranslatePoint([Windows.Point]::new(0,0),$w);[Windows.Rect]::new($point,[Windows.Size]::new($_.ActualWidth,$_.ActualHeight))})
   if($rects[0].IntersectsWith($rects[1])){throw "Overlapping home transfer controls: $theme / $width"}
   foreach($rect in $rects){if($rect.Width -lt 40 -or $rect.Height -lt 20 -or $rect.Right -gt $w.ActualWidth){throw 'Clipped home transfer control'}}
   $w.ShowPageForSnapshot('servers')
   $w.UpdateLayout()
   $serverButtons=@(Buttons $w)
   $transfer=@($serverButtons|Where-Object {$_.Name -in @('ServerConfigImport','ServerConfigExport')})
   if($transfer.Count -ne 2){throw 'Missing server transfer controls'}
   foreach($button in $serverButtons){
    $label=[Windows.Automation.AutomationProperties]::GetName($button)
    if($label -in @('Мой VPS','Корпоративный прокси')){throw 'Removed server button remains visible'}
   }
   foreach($button in $transfer){$point=$button.TranslatePoint([Windows.Point]::new(0,0),$w);if($point.X+$button.ActualWidth -gt $w.ActualWidth){throw 'Clipped server transfer control'}}
   $w.ShowPageForSnapshot('home')
  }
 }
 'PASS: home import/export controls in all five themes, narrow/wide windows and 140% text size.'
}finally{$w.Close();$app.Shutdown()}

param([string]$AssemblyPath,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Runtime.Serialization
$app=New-Object System.Windows.Application
$app.ShutdownMode=[System.Windows.ShutdownMode]::OnExplicitShutdown
$a=[Reflection.Assembly]::LoadFrom($AssemblyPath)
[System.Windows.Application]::ResourceAssembly=$a
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=$a.GetType('NovaVpn.MainWindow'); $vt=$a.GetType('NovaVpn.VisualPreferences'); $pt=$a.GetType('NovaVpn.ThemePalette')
$w=[Activator]::CreateInstance($type,@($true)); $w.Left=-32000; $w.Top=-32000; $w.ShowActivated=$false; $w.ShowInTaskbar=$false
$state=$type.GetField('state',$flags).GetValue($w)
$results=New-Object 'System.Collections.Generic.List[string]'
function Assert($ok,$label) { if(-not $ok){throw $label};$results.Add("PASS $label") }
function Pump { $w.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle); $w.UpdateLayout() }
function Nodes($node) {
 if($null -eq $node){return}
 $node
 for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++){ Nodes ([Windows.Media.VisualTreeHelper]::GetChild($node,$i)) }
}
try {
 $w.Show()
 foreach($theme in $pt.GetField('ThemeNames').GetValue($null)) {
  foreach($dark in @($false,$true)) {
   $state.Visual.LoadThemeSettings($theme); $state.Visual.DarkMode=$dark
   $w.Width=720; $w.Height=620; $state.Visual.TextScale=1.35
   $type.GetMethod('BuildShell',$flags).Invoke($w,@())|Out-Null
   $type.GetMethod('Navigate',$flags).Invoke($w,@('home',$false))|Out-Null; Pump
   $button=$type.GetField('connectButton',$flags).GetValue($w)
   $rect=$button.TransformToAncestor($w).TransformBounds([Windows.Rect]::new(0,0,$button.ActualWidth,$button.ActualHeight))
   Assert ($rect.Bottom -lt $w.ActualHeight -and $rect.Top -gt 50 -and $rect.Right -lt $w.ActualWidth) "Connection button visible: $theme dark=$dark bounds=$rect"
   $palette=$pt.GetMethod('Create').Invoke($null,@($state.Visual))
   Assert ($palette.IsLight -ne $dark) "Theme mode: $theme dark=$dark"
   $ratio=$pt.GetMethod('ContrastRatio',[Reflection.BindingFlags]'Static,NonPublic')
   foreach($pair in @(@($palette.Text,$palette.Surface),@($palette.Muted,$palette.Surface),@($palette.AccentForeground,$palette.Accent))) {
    $contrast=$ratio.Invoke($null,@($pair[0],$pair[1])); Assert ($contrast -ge 4.5) "Text contrast $theme dark=$dark ratio=$([Math]::Round($contrast,2))"
   }
  }
 }
 $state.Visual.LoadThemeSettings('Material You'); $state.Visual.DarkMode=$true; $state.Visual.MaterialHue=160
 $dialog=[Activator]::CreateInstance($a.GetType('NovaVpn.AppearanceWindow'),@($state.Visual,$false))
 $dialog.Left=-32000;$dialog.Top=-32000;$dialog.ShowInTaskbar=$false;$dialog.ShowActivated=$false;$dialog.Show();$dialog.UpdateLayout()
 $slider=@(Nodes $dialog | Where-Object {$_ -is [Windows.Controls.Slider] -and $_.Maximum -eq 360})[0]
 $slider.Value=210
 $dialog.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
 Assert ($dialog.Result.MaterialHue -eq 210 -and $dialog.Result.DarkMode) 'Hue slider changes hue without changing dark mode'
 $switch=$dialog.GetType().GetMethod('SwitchTheme',$flags)
 $switch.Invoke($dialog,@('iOS 27 · Liquid Glass'))|Out-Null; $dialog.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle); $dialog.UpdateLayout()
 $slider=@(Nodes $dialog | Where-Object {$_ -is [Windows.Controls.Slider] -and $_.Maximum -eq 100 -and $_.Minimum -eq 0})[0]
 $slider.Value=100
 Write-Output ('SLIDER value=' + $slider.Value + ' frost=' + $dialog.Result.GlassFrost + ' blur=' + $dialog.Result.GlassBlur)
 Assert ($dialog.Result.GlassFrost -eq 1 -and $dialog.Result.GlassBlur -eq 32) 'Glass slider matte endpoint'
 $slider.Value=0
 Assert ($dialog.Result.GlassFrost -eq 0 -and $dialog.Result.GlassBlur -eq 1) 'Glass slider clear endpoint'
 $switch.Invoke($dialog,@('Material You'))|Out-Null
 Assert ($dialog.Result.MaterialHue -eq 210 -and $dialog.Result.DarkMode) 'Style switch preserves hue and dark mode'
 $dialog.UpdateLayout()
 $light=@(Nodes $dialog | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Светлая'})[0]
 $light.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
 Assert (-not $dialog.Result.DarkMode -and $dialog.Result.MaterialHue -eq 210) 'Light mode control preserves hue'
 $dialog.UpdateLayout()
 $darkButton=@(Nodes $dialog | Where-Object {$_ -is [Windows.Controls.Button] -and $_.Content -eq 'Тёмная'})[0]
 $darkButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
 Assert ($dialog.Result.DarkMode) 'Dark mode control'
 $copy=$dialog.Result.Clone(); Assert ($copy.MaterialHue -eq 210 -and $copy.DarkMode) 'Clone preserves independent appearance fields'
 $serializer=[Runtime.Serialization.DataContractSerializer]::new($vt)
 $mem=[IO.MemoryStream]::new();$serializer.WriteObject($mem,$copy);$mem.Position=0;$round=$serializer.ReadObject($mem);$mem.Dispose()
 Assert ($round.MaterialHue -eq 210 -and $round.DarkMode -and $round.ThemeName -eq 'Material You') 'DataContract round trip'
 $normalize=$a.GetType('NovaVpn.StateStore').GetMethod('NormalizeVisual',[Reflection.BindingFlags]'Static,NonPublic')
 $round.ThemeName='Windows 11 Dark';$round.DarkMode=$false;$normalize.Invoke($null,@($round))|Out-Null
 Assert ($round.DarkMode -and $round.ThemeName -eq 'Windows 11') 'Legacy Windows Dark migration'
 $round.ThemeName='Amber Glass';$normalize.Invoke($null,@($round))|Out-Null
 Assert ($round.ThemeName -eq 'iOS 27 · Liquid Glass') 'Legacy Amber migration'
 $dialog.Close()
 $results|Set-Content -LiteralPath (Join-Path $OutputDirectory 'interaction-checks.txt') -Encoding UTF8
 "Passed $($results.Count) checks"
} finally { if($dialog){$dialog.Close()};$w.Close();$app.Shutdown() }

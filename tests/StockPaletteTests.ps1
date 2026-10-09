param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$palette=$assembly.GetType('NovaVpn.ThemePalette');$visual=$assembly.GetType('NovaVpn.VisualPreferences')
$control=$assembly.GetType('NovaVpn.ConnectionControl');$status=$assembly.GetType('NovaVpn.CoreStatus')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
foreach($theme in $palette.GetField('ThemeNames').GetValue($null)) {
 $v=[Activator]::CreateInstance($visual);$v.LoadThemeSettings($theme)
 $stock=$palette.GetMethod('Create').Invoke($null,@($v))
 $v.AccentHex='#FF00FF';$v.SuccessHex='#FFFF00'
 $forced=$palette.GetMethod('Create').Invoke($null,@($v))
 if($forced.Accent -ne $stock.Accent -or $forced.Success -ne $stock.Success){throw 'Legacy overrides changed stock palette'}
 $button=[Activator]::CreateInstance($control)
 foreach($state in @('Disconnected','Connected')) {
  $button.Apply($v,[Enum]::Parse($status,$state),$false)
  $face=$control.GetField('face',$flags).GetValue($button)
  if($theme -eq 'Amber Glass') {
   if($face.Background -isnot [System.Windows.Media.LinearGradientBrush]){throw "Amber Glass should use its warm gradient orb in $state state"}
   if($button.ActionButton.Width -ne $button.ActionButton.Height -or $face.CornerRadius.TopLeft -ne ($button.ActionButton.Width/2)){throw 'Amber Glass connection control should remain circular'}
  } elseif($theme -eq 'iOS 27 · Liquid Glass') {
   if($button.ActionButton.Width -ne $button.ActionButton.Height -or $face.CornerRadius.TopLeft -ne ($button.ActionButton.Width/2)){throw 'iOS connection control should remain circular'}
  } elseif($face.Background.Color -ne $stock.Accent){throw "Connection state changed the stock accent: $theme $state actual=$($face.Background.Color) expected=$($stock.Accent)"}
 }
}
Write-Output 'PASS: custom accent/success overrides ignored in all five palettes; both glass connection buttons are circular and the warm theme uses its stock gradient.'

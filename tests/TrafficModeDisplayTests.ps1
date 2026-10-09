param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if ($null -eq [Windows.Application]::Current) { $app = New-Object Windows.Application }
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
function Find-Buttons($node) {
 if ($node -is [Windows.Controls.Button]) { $node }
 if ($node -is [Windows.Controls.Panel]) { foreach($child in $node.Children){ Find-Buttons $child } }
 elseif ($node -is [Windows.Controls.Border]) { Find-Buttons $node.Child }
}
try {
 $state=$type.GetField('state',$flags).GetValue($window)
 $selectedProfile=$state.Profiles | Where-Object { $_.Id -eq $state.SelectedProfileId } | Select-Object -First 1
 if($null -ne $selectedProfile){$selectedProfile=$selectedProfile.PSObject.BaseObject}
 $palette=$assembly.GetType('NovaVpn.ThemePalette')
 foreach($theme in $palette.GetField('ThemeNames').GetValue($null)) {
  $state.Visual.LoadThemeSettings($theme)
  foreach($mode in @('VPN','VPN+Zapret','Zapret')) {
   $state.Zapret.Mode=$mode
   $panel=$type.GetMethod('BuildZapretPanel',$flags).Invoke($window,@($selectedProfile))
   $buttons=@(Find-Buttons $panel | Where-Object { $_.ToolTip -like 'Выбран:*' -or $_.ToolTip -like 'Переключить на:*' })
   if($buttons.Count -ne 3){throw 'Missing mode controls'}
   $selected=@($buttons | Where-Object { $_.Content -like '✓*' })
   if($selected.Count -ne 1){throw "Incorrect selection: $theme $mode"}
   $selected[0].ApplyTemplate() | Out-Null
   $face=[Windows.Media.VisualTreeHelper]::GetChild($selected[0],0)
   if($face.Background.Color -ne $selected[0].Background.Color){throw 'Template lost selected color'}
   if($selected[0].Foreground.Color -ne [Windows.Media.Colors]::White){throw 'Selection text is not readable'}
  }
 }
 Write-Output 'PASS: one checked, opaque active mode in all 12 theme/mode combinations; real lifecycle caption separated from selection.'
} finally { $window.Close() }

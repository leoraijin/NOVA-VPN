param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Runtime.Serialization
$app=New-Object System.Windows.Application
$app.ShutdownMode=[System.Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$type=$assembly.GetType('NovaVpn.VisualPreferences')
$v=[Activator]::CreateInstance($type)
$glass=$assembly.GetType('NovaVpn.ThemePalette').GetField('IosLiquidGlass').GetRawConstantValue()
$v.LoadThemeSettings($glass)
$design=$assembly.GetType('NovaVpn.VisualDesign')
$resource=$assembly.GetType('NovaVpn.VisualResourceManager')
$paletteType=$assembly.GetType('NovaVpn.ThemePalette')
function Assert($condition,$text){if(-not $condition){throw $text}}
try {
  $v.GlassFrost=.23; $v.StoreActiveThemeSettings();$v.LoadThemeSettings('Windows 11 Light');$v.LoadThemeSettings($glass)
  Assert ([Math]::Abs($v.GlassFrost-.23) -lt .001) 'Frost must be saved per profile'
  $serializer=[Runtime.Serialization.Json.DataContractJsonSerializer]::new($type)
  $stream=New-Object IO.MemoryStream
  try {$serializer.WriteObject($stream,$v);$stream.Position=0;$restored=$serializer.ReadObject($stream)}finally{$stream.Dispose()}
  Assert ([Math]::Abs($restored.GlassFrost-.23) -lt .001) 'Frost must survive serialization'
  $alphas=@()
  foreach($frost in @(0,.5,1)) {
    $v.GlassFrost=$frost
    $fill=$design.GetMethod('GlassFill').Invoke($null,@($v))
    $alphas+=$fill.GradientStops[0].Color.A
  }
  Assert ($alphas[0] -lt $alphas[1] -and $alphas[1] -lt $alphas[2]) 'Increasing frost must visibly diffuse the material'
  $v.GlassFrost=0
  $clearPalette=$paletteType.GetMethod('Create').Invoke($null,@($v))
  $v.GlassFrost=1
  $opaquePalette=$paletteType.GetMethod('Create').Invoke($null,@($v))
  Assert ($clearPalette.Surface.A -lt 80 -and $opaquePalette.Surface.A -gt 200) 'The transparency setting must expose the backdrop through cards and sidebar'
  $palette=$paletteType.GetMethod('Create').Invoke($null,@($v))
  $resource.GetMethod('Apply').Invoke($null,@($app.PSObject.BaseObject,$palette,$v))|Out-Null
  $button=New-Object System.Windows.Controls.Button
  $button.Content='Glass key';$button.Width=160;$button.Height=48
  $button.Background=New-Object System.Windows.Media.SolidColorBrush($palette.Accent)
  $window=New-Object System.Windows.Window
  $window.Content=$button;$window.Width=260;$window.Height=170;$window.Left=-32000;$window.Top=-32000;$window.ShowInTaskbar=$false
  try {
    $window.Show();$window.UpdateLayout();$button.ApplyTemplate()|Out-Null
    $face=$button.Template.FindName('GlassFace',$button)
    $body=$button.Template.FindName('KeyBody',$button)
    Assert ($null -ne $face) 'Implicit button style must provide glass material'
    Assert ($face.Effect.Direction -eq 315 -and $face.Effect.ShadowDepth -eq 4) 'Upper-left light must cast a lower-right shadow'
    $key=[System.Windows.Controls.Primitives.ButtonBase].GetField('IsPressedPropertyKey',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $button.SetValue($key,$true);$window.UpdateLayout()
    Assert ($body.RenderTransform.Y -eq 2 -and $face.Effect.ShadowDepth -eq 1) 'Pressed key must depress and shorten shadow'
    $button.SetValue($key,$false);$window.UpdateLayout()
    Assert ($null -eq $body.ReadLocalValue([System.Windows.UIElement]::RenderTransformProperty) -or $body.RenderTransform.Value.OffsetY -eq 0) 'Released key must return to original depth'
  } finally {$window.Close()}
  $connectionType=$assembly.GetType('NovaVpn.ConnectionControl')
  $connection=[Activator]::CreateInstance($connectionType)
  $disconnected=[Enum]::ToObject($assembly.GetType('NovaVpn.CoreStatus'),0)
  foreach($corner in @(8,18,32)) {
    $v.CornerRadius=$corner
    $connectionType.GetMethod('Apply').Invoke($connection,@($v,$disconnected,$false))|Out-Null
    $connectionFace=$connectionType.GetField('face',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($connection)
    Assert ($connection.ActionButton.Width -eq $connection.ActionButton.Height -and $connectionFace.CornerRadius.TopLeft -eq ($connection.ActionButton.Width/2)) 'The iOS connection button must stay a circle at every corner setting'
  }
  $v.LoadThemeSettings('Amber Glass')
  $warmPalette=$paletteType.GetMethod('Create').Invoke($null,@($v))
  $warmFill=$design.GetMethod('AmberConnectionFill').Invoke($null,@())
  Assert ($warmPalette.IsGlass -and $warmPalette.IsLight -and $warmFill -is [System.Windows.Media.LinearGradientBrush] -and $warmFill.GradientStops.Count -eq 3) 'Amber Glass must expose its warm palette and three-stop connection gradient'
  $connectionType.GetMethod('Apply').Invoke($connection,@($v,$disconnected,$false))|Out-Null
  $warmFace=$connectionType.GetField('face',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($connection)
  Assert ($connection.ActionButton.Width -eq $connection.ActionButton.Height -and $warmFace.CornerRadius.TopLeft -eq ($connection.ActionButton.Width/2)) 'Amber Glass connection button must remain circular'
  Assert ($warmFace.Background -is [System.Windows.Media.LinearGradientBrush] -and $connectionType.GetField('highlight',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($connection).Visibility -eq [System.Windows.Visibility]::Visible) 'Amber Glass connection button should show its gradient orb and glass highlight'
  Write-Output 'PASS: glass range, persistence, physical keys, circular connection geometry and Amber Glass gradient orb.'
}finally{$app.Shutdown()}

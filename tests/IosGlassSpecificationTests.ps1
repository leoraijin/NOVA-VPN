param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Runtime.Serialization
$app=New-Object System.Windows.Application
$app.ShutdownMode=[System.Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$visualType=$assembly.GetType('NovaVpn.VisualPreferences')
$theme=$assembly.GetType('NovaVpn.ThemePalette').GetField('IosLiquidGlass').GetRawConstantValue()
$design=$assembly.GetType('NovaVpn.VisualDesign')
function Assert($condition,$message){if(-not $condition){throw $message}}
function Nodes($node){
  if($node -is [System.Windows.DependencyObject]){
    $node
    foreach($child in [System.Windows.LogicalTreeHelper]::GetChildren($node)){if($child -is [System.Windows.DependencyObject]){Nodes $child}}
  }
}
$v=[Activator]::CreateInstance($visualType);$v.LoadThemeSettings($theme)
$appearanceType=$assembly.GetType('NovaVpn.AppearanceWindow')
$dialog=[Activator]::CreateInstance($appearanceType,@($v,$false))
$dialog.Left=-32000;$dialog.Top=-32000;$dialog.ShowInTaskbar=$false
try {
  $dialog.Show();$dialog.UpdateLayout()
  $body=$appearanceType.GetField('body',$flags).GetValue($dialog)
  function Move-Slider($title,$value){
    $card=@($body.Children|Where-Object{@(Nodes $_|Where-Object{$_ -is [System.Windows.Controls.TextBlock] -and $_.Text -eq $title}).Count -gt 0})[0]
    $slider=@(Nodes $card|Where-Object{$_ -is [System.Windows.Controls.Slider]})[0]
    Assert ($null -ne $slider) "Missing control: $title"
    $slider.Value=$value
    $appearanceType.GetMethod('FlushPreview',$flags).Invoke($dialog,@())|Out-Null
    $dialog.UpdateLayout()
  }
  Move-Slider 'Прозрачность стекла' 25
  $beforeFill=$dialog.Resources['NovaGlassFill'].GradientStops[0].Color.A
  Move-Slider 'Матовость стекла' 18
  Assert ($dialog.Result.GlassFrost -eq .75 -and $dialog.Result.GlassBlur -eq 18) 'Transparency and frosting must remain independent'
  Assert ($dialog.Resources['NovaGlassFill'].GradientStops[0].Color.A -eq $beforeFill) 'Changing blur must not secretly change opacity'
  $connection=$appearanceType.GetField('previewConnection',$flags).GetValue($dialog)
  $connectionType=$assembly.GetType('NovaVpn.ConnectionControl')
  $backdrop=$connectionType.GetField('backdrop',$flags).GetValue($connection)
  Assert ($backdrop.AppliedBlur -eq 18) 'Frost slider must reach actual backdrop effect'
  Move-Slider 'Сила теней' 0
  $face=$connectionType.GetField('face',$flags).GetValue($connection)
  Assert ($face.Effect.Opacity -eq 0) 'Shadow slider must remove the visible shadow'
  Move-Slider 'Сила бликов' 0
  Move-Slider 'Мягкое усиление контраста' 65
  Assert ($dialog.Result.SoftContrast -eq .65 -and -not $dialog.Result.HighContrast) 'Soft contrast must be independent of the accessibility high-contrast switch'
  $paletteType=$assembly.GetType('NovaVpn.ThemePalette')
  $normal=$v.Clone();$normal.SoftContrast=0
  $enhanced=$normal.Clone();$enhanced.SoftContrast=1
  $normalPalette=$paletteType.GetMethod('Create').Invoke($null,@($normal))
  $enhancedPalette=$paletteType.GetMethod('Create').Invoke($null,@($enhanced))
  Assert ($enhancedPalette.Muted.R -lt $normalPalette.Muted.R -and $enhancedPalette.Border.R -lt $normalPalette.Border.R) 'Soft contrast must darken secondary text and boundaries in the light profile'
  Assert ($dialog.Resources['NovaGlassSheen'].GradientStops[0].Color.A -eq 0 -and $dialog.Resources['NovaGlassRim'].GradientStops[0].Color.A -eq 0) 'Highlight slider must affect sheen and rim'
  Assert ($v.GlassBlur -eq 10 -and $v.GlassShadow -eq 1 -and $v.GlassHighlight -eq 1) 'Preview must not mutate live preferences'
  $draft=$dialog.Result.Clone();$draft.StoreActiveThemeSettings()
  $draft.LoadThemeSettings('Windows 11 Light');$draft.LoadThemeSettings($theme)
  Assert ($draft.GlassBlur -eq 18 -and $draft.GlassShadow -eq 0 -and $draft.GlassHighlight -eq 0 -and $draft.GlassFrost -eq .75) 'Glass controls must be preserved per profile'
  Assert ($draft.SoftContrast -eq .65) 'Soft contrast must be preserved per profile'
  $serializer=[Runtime.Serialization.Json.DataContractJsonSerializer]::new($visualType)
  $stream=New-Object IO.MemoryStream
  try{$serializer.WriteObject($stream,$draft);$stream.Position=0;$restored=$serializer.ReadObject($stream)}finally{$stream.Dispose()}
  Assert ($restored.GlassBlur -eq 18 -and $restored.ThemeOverrides[$theme].GlassShadow -eq 0) 'Glass preferences must survive export serialization'
  Assert ($restored.SoftContrast -eq .65 -and $restored.ThemeOverrides[$theme].SoftContrast -eq .65) 'Soft contrast must survive configuration export'
}finally{$dialog.Close()}

$state=[Activator]::CreateInstance($assembly.GetType('NovaVpn.AppState'))
$state.SchemaVersion=9;$state.Visual=$v.Clone();$state.Visual.GlassBlur=0;$state.Visual.GlassShadow=0;$state.Visual.GlassHighlight=0
$state.Routing.ProxyDomains.Add('youtube.com')
$assembly.GetType('NovaVpn.StateStore').GetMethod('NormalizeForSerialization').Invoke($null,@($state))|Out-Null
Assert ($state.SchemaVersion -eq 10 -and $state.Visual.GlassBlur -eq 10 -and $state.Visual.GlassShadow -eq 1 -and $state.Routing.ProxyDomains.Contains('youtube.com')) 'Upgrade must initialize only new appearance settings and retain routing'
$state.Visual.GlassBlur=[double]::NaN;$state.Visual.GlassHighlight=4;$state.Visual.GlassShadow=-2
$assembly.GetType('NovaVpn.StateStore').GetMethod('NormalizeForSerialization').Invoke($null,@($state))|Out-Null
Assert ($state.Visual.GlassBlur -eq 10 -and $state.Visual.GlassHighlight -eq 1 -and $state.Visual.GlassShadow -eq 0) 'Invalid imported glass values must normalize safely'

# A sharp black/white backdrop proves that blur changes the sampled background,
# while a red foreground marker proves that foreground content remains sharp.
$root=New-Object System.Windows.Controls.Grid;$root.Width=240;$root.Height=120
$source=New-Object System.Windows.Controls.Grid;$source.Tag='NovaDecorativeBackdrop'
$brush=New-Object System.Windows.Media.LinearGradientBrush
$brush.StartPoint=[Windows.Point]::new(0,0);$brush.EndPoint=[Windows.Point]::new(1,0)
$brush.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.Colors]::Black,0))
$brush.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.Colors]::Black,.5))
$brush.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.Colors]::White,.5))
$brush.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.Colors]::White,1))
$source.Background=$brush;$root.Children.Add($source)|Out-Null
$layer=[Activator]::CreateInstance($assembly.GetType('NovaVpn.GlassBackdropLayer'))
$layer.Width=160;$layer.Height=80;$layer.MaterialFill=$false
$root.Children.Add($layer)|Out-Null
$marker=New-Object System.Windows.Shapes.Rectangle;$marker.Fill=[Windows.Media.Brushes]::Red
$marker.Width=18;$marker.Height=18;$marker.HorizontalAlignment='Left';$marker.VerticalAlignment='Top';$marker.Margin=[Windows.Thickness]::new(65,51,0,0)
$root.Children.Add($marker)|Out-Null
$window=New-Object System.Windows.Window;$window.Content=$root;$window.SizeToContent='WidthAndHeight';$window.WindowStyle='None';$window.Left=-32000;$window.Top=-32000;$window.ShowInTaskbar=$false
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
try{
  $window.Show();$window.UpdateLayout();$values=@()
  foreach($radius in @(0,18)){
    $options=$v.Clone();$options.GlassBlur=$radius;$layer.Preferences=$options;$window.UpdateLayout()
    if($radius -gt 0){
      $layerType=$layer.GetType();$privateFlags=[Reflection.BindingFlags]'Instance,NonPublic'
      $brushField=$layerType.GetField('brush',$privateFlags);$originalBrush=$brushField.GetValue($layer)
      $styleOnly=$options.Clone();$styleOnly.TextScale=1.2;$styleOnly.CornerRadius=24;$styleOnly.AnimationFrameRate=60
      $layer.Preferences=$styleOnly;$window.UpdateLayout()
      Assert ([object]::ReferenceEquals($originalBrush,$brushField.GetValue($layer))) 'Unrelated appearance edits must not rebuild the expensive backdrop sampling brush'
      $blurOnly=$styleOnly.Clone();$blurOnly.GlassBlur=17
      $layer.Preferences=$blurOnly;$window.UpdateLayout()
      Assert ([object]::ReferenceEquals($originalBrush,$brushField.GetValue($layer)) -and $layer.AppliedBlur -eq 17) 'Changing blur radius must reuse the backdrop brush and update the existing effect'
      $options=$options.Clone();$options.GlassBlur=18;$layer.Preferences=$options;$window.UpdateLayout()
    }
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(240,120,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($root)
    $pixels=New-Object byte[] (240*120*4);$bitmap.CopyPixels($pixels,960,0)
    $values+=$pixels[(60*240+116)*4]
    $index=(60*240+70)*4
    Assert ($pixels[$index+2] -gt 245 -and $pixels[$index] -lt 10) 'Backdrop blur must not soften foreground content'
    if($radius -gt 0){Assert ($layer.HasBackdrop -and $layer.AppliedBlur -eq 18) 'Glass must sample an actual backdrop'}
    $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder;$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.File]::Create((Join-Path $WorkPath "blur-$radius.png"));try{$encoder.Save($stream)}finally{$stream.Dispose()}
  }
  Assert (($values[1]-$values[0]) -gt 15) 'Frosting must visibly soften a sharp background edge'
}finally{$window.Close();$app.Shutdown()}
"PASS: independent sliders, actual backdrop blur, sharp foreground, per-profile storage, export, migration and normalization. Edge samples: $($values -join ', ')"

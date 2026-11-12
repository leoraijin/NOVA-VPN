param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object Windows.Application
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
function Assert($value,$message){if(-not $value){throw $message}}
function Card($values,$key){
 $type.GetMethod('RouteEditorCard',$flags).Invoke($window,@('Правила маршрутизации','Отдельная ячейка для каждого правила',$values,[Windows.Media.Colors]::MediumPurple,$key))
}
function Editor($card){$card.Child.Children[1]}
function Rows($editor){return ,$editor.Children[0].Content.Children}
function Click($button){$button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))}
function Paste($cell,$text){
 $data=[Windows.DataObject]::new([Windows.DataFormats]::UnicodeText,$text)
 $event=[Windows.DataObjectPastingEventArgs]::new($data,$false,[Windows.DataFormats]::UnicodeText)
 $cell.RaiseEvent($event)
 Assert $event.CommandCancelled 'Multiline paste must create individual cells'
}
try {
 $state=$type.GetField('state',$flags).GetValue($window)
 $keys=[ordered]@{
  'proxy-domains'='ProxyDomains';'direct-domains'='DirectDomains';'block-domains'='BlockDomains'
  'proxy-apps'='ProxyProcesses';'direct-apps'='DirectProcesses'
  'proxy-paths'='ProxyProcessPaths';'direct-paths'='DirectProcessPaths';'advanced'='AdvancedRules'
 }
 foreach($key in $keys.Keys){
  $values=$state.Routing.($keys[$key]);$values.Clear()
  $values.Add('first.test');$values.Add('second.test')
  $card=Card $values $key;$editor=Editor $card;$rows=Rows $editor
  Assert ($rows.Count -eq 2) "$key lost existing rules"
  foreach($row in $rows){Assert (-not $row.Children[0].AcceptsReturn) "$key is still multiline"}
  $rows[0].Children[0].Text=' edited.test '
  Assert ($values.Count -eq 2 -and $values[0] -eq 'edited.test' -and $values[1] -eq 'second.test') "$key edit changed another rule"
  Click $editor.Children[1];$rows=Rows $editor
  Assert ($rows.Count -eq 3 -and $values.Count -eq 2) "$key blank cell changed stored rules"
  Click $editor.Children[1]
  Assert ((Rows $editor).Count -eq 3) "$key accumulates unused blank cells"
  $rows[2].Children[0].Text='third.test'
  Assert ($values.Count -eq 3 -and $values[2] -eq 'third.test') "$key add did not persist"
  Click $rows[1].Children[1];$rows=Rows $editor
  Assert ($values.Count -eq 2 -and $values[1] -eq 'third.test') "$key removed wrong rule"
  $cell=$rows[0].Children[0];$cell.SelectAll()
  Paste $cell "one.test`r`n`r`ntwo.test`nthree.test`r"
  Assert (($values -join '|') -eq 'one.test|two.test|three.test|third.test') "$key paste lost or reordered entries"
  Assert ((Rows $editor).Count -eq 4) "$key paste stayed in one cell"
  for($i=0;$i -lt 4;$i++){Click (Rows $editor)[0].Children[1]}
  Assert ($values.Count -eq 0 -and (Rows $editor).Count -eq 1) "$key cannot delete last rule"
  (Rows $editor)[0].Children[0].Text='  '
  Assert ($values.Count -eq 0) "$key stores blank rules"
 }
 # Selection-based paste must preserve surrounding text and neighbouring entries.
 $values=$state.Routing.ProxyDomains;$values.Clear();$values.Add('prefixOLDsuffix');$values.Add('keep.test')
 $editor=Editor (Card $values 'proxy-domains');$cell=(Rows $editor)[0].Children[0]
 $cell.Select(6,3);Paste $cell "ONE`r`nTWO"
 Assert (($values -join '|') -eq 'prefixONE|TWOsuffix|keep.test') 'Paste discarded text outside the selection'
 # All protected lists remain locked while Zapret has temporarily moved routes.
 $state.Zapret.RoutingChangesApplied=$true
 foreach($key in @('proxy-domains','direct-domains','proxy-apps','direct-apps','proxy-paths','direct-paths')){
  $values=$state.Routing.($keys[$key]);$values.Clear();$values.Add('locked.test')
  $editor=Editor (Card $values $key);$row=(Rows $editor)[0]
  Assert ($row.Children[0].IsReadOnly -and -not $row.Children[1].IsEnabled -and -not $editor.Children[1].IsEnabled) "$key Zapret lock broken"
  $row.Children[0].Text='attempt.test';Click $row.Children[1];Click $editor.Children[1]
  Assert ($values.Count -eq 1 -and $values[0] -eq 'locked.test') "$key protected list modified"
 }
 $state.Zapret.RoutingChangesApplied=$false
 # Duplicate warnings are informational and never remove entries from either list.
 $state.Routing.DirectDomains.Clear();$state.Routing.DirectDomains.Add('same.test')
 $state.Routing.ProxyDomains.Clear()
 $editor=Editor (Card $state.Routing.ProxyDomains 'proxy-domains')
 (Rows $editor)[0].Children[0].Text='https://SAME.test/path'
 $warning=$type.GetField('lastRoutingDuplicateWarning',$flags).GetValue($window)
 Assert ($warning.Contains('Сайты напрямую')) 'Missing domain duplicate warning'
 Assert ($state.Routing.DirectDomains.Count -eq 1 -and $state.Routing.ProxyDomains.Count -eq 1) 'Domain conflict deleted a rule'
 $state.Routing.DirectProcessPaths.Clear();$state.Routing.DirectProcessPaths.Add('C:\Apps\ChatGPT.exe')
 $state.Routing.ProxyProcesses.Clear()
 $editor=Editor (Card $state.Routing.ProxyProcesses 'proxy-apps')
 (Rows $editor)[0].Children[0].Text='CHATGPT.exe'
 $warning=$type.GetField('lastRoutingDuplicateWarning',$flags).GetValue($window)
 Assert ($warning.Contains('Пути напрямую')) 'Missing executable name/path warning'
 Assert ($state.Routing.DirectProcessPaths.Count -eq 1 -and $state.Routing.ProxyProcesses.Count -eq 1) 'App conflict deleted a rule'
 (Rows $editor)[0].Children[0].Text='bad|name'
 # Render realistic domain/app cards in both palettes, normal and enlarged/narrow.
 foreach($dark in @($false,$true)){
  foreach($scale in @(1.0,1.4)){
   $state.Visual.DarkMode=$dark;$state.Visual.TextScale=$scale
   $type.GetMethod('ApplyVisualPalette',$flags).Invoke($window,@())|Out-Null
   $domains=$state.Routing.ProxyDomains;$domains.Clear();$domains.Add('example.com');$domains.Add('*.example.org');$domains.Add('api.example.net')
   $apps=$state.Routing.ProxyProcesses;$apps.Clear();$apps.Add('discord.exe');$apps.Add('browser.exe')
   $panel=[Windows.Controls.StackPanel]::new();$panel.Margin=[Windows.Thickness]::new(18)
   $panel.Children.Add((Card $domains 'proxy-domains'))|Out-Null
   $panel.Children.Add((Card $apps 'proxy-apps'))|Out-Null
   $palette=$type.GetField('palette',$flags).GetValue($window)
   $border=[Windows.Controls.Border]::new();$border.Background=[Windows.Media.SolidColorBrush]::new($palette.Bg);$border.Child=$panel
   $width=if($scale -eq 1.0){480}else{360}
   $border.Measure([Windows.Size]::new($width,[double]::PositiveInfinity))
   $height=[math]::Ceiling($border.DesiredSize.Height)
   $border.Arrange([Windows.Rect]::new(0,0,$width,$height));$border.UpdateLayout()
   foreach($card in $panel.Children){
    foreach($row in (Rows (Editor $card))){Assert ($row.Children[0].ActualWidth -gt 100) 'Input clipped in narrow layout'}
   }
   $image=[Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32);$image.Render($border)
   $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
   $mode=if($dark){'dark'}else{'light'}
   $stream=[IO.File]::Create((Join-Path $WorkPath "routing-$mode-$scale.png"));try{$encoder.Save($stream)}finally{$stream.Dispose()}
  }
 }
 'PASS: eight lists; existing rules, independent edits, add/delete/empty lists, multiline/selection paste; six Zapret locks; light/dark and narrow enlarged layouts. Snapshot data and isolated store only; no user profiles, settings or VPN changed.'
} finally {$window.Close();$app.Shutdown()}

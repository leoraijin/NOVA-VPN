param([Parameter(Mandatory=$true)][string]$AssemblyPath,
      [Parameter(Mandatory=$true)][string]$CorePath,
      [Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
trap { Write-Output $_.ScriptStackTrace; break }
Add-Type -AssemblyName PresentationCore,PresentationFramework,System.Net.Http
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$profileType=$assembly.GetType('NovaVpn.VpnProfile')
$healthType=$assembly.GetType('NovaVpn.ServerMapHealth')
$mapService=$assembly.GetType('NovaVpn.ServerRegionMapService')
$builder=$assembly.GetType('NovaVpn.ConfigBuilder')
function Assert($condition,$message){if(-not $condition){throw "ASSERT: $message"}}
function Profile($name){$p=[Activator]::CreateInstance($profileType);$p.Name=$name;return $p}
$profile=Profile 'DE Germany - Frankfurt'; $profile.Protocol='shadowsocks';$profile.Server='127.0.0.1';$profile.Port=1
$profile.Method='aes-128-gcm';$profile.Password='map-test-fixture-only'
$config=$builder.GetMethod('BuildProbe').Invoke($null,@($profile,19876,'fixture','fixture-password'))|ConvertFrom-Json
Assert ($config.inbounds.Count -eq 1 -and $config.inbounds[0].type -eq 'mixed') 'probe must not create a TUN'
Assert ($config.inbounds[0].listen -eq '127.0.0.1' -and $config.inbounds[0].users.Count -eq 1) 'probe proxy must use loopback authentication'
Assert ($config.outbounds.Count -eq 1 -and $config.outbounds[0].type -eq 'shadowsocks' -and $config.route.final -eq 'proxy') 'probe must have no direct fallback'
$other=Profile 'PL Poland - Warsaw';$third=Profile 'DE Germany - Frankfurt'
$profiles=[Array]::CreateInstance($profileType,3);$profiles.SetValue($profile,0);$profiles.SetValue($other,1);$profiles.SetValue($third,2)
$dictType=[Collections.Generic.Dictionary``2].MakeGenericType([string],$healthType)
$health=[Activator]::CreateInstance($dictType)
$good=[Activator]::CreateInstance($healthType);$good.Success=$true;$good.LatencyMs=25;$good.CheckedUtc=[DateTime]::UtcNow;$health.Add($profile.Id,$good)
$bad=[Activator]::CreateInstance($healthType);$bad.CheckedUtc=[DateTime]::UtcNow;$bad.Message='HTTPS failed';$health.Add($other.Id,$bad)
$script:chosen=$null
$choose=[Action[object]]{param($p) $script:chosen=$p}
$themes=$assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)
foreach($theme in $themes){
    $visual=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences'));$visual.ThemeName=$theme
    $palette=$assembly.GetType('NovaVpn.ThemePalette').GetMethod('Create').Invoke($null,@($visual))
    $map=$mapService.GetMethod('BuildInteractiveCanvas').Invoke($null,@($profiles,$profile,[double]820,[double]400,$palette,$health,$choose,$true))
    $nodes=@($map.Children|Where-Object {$_.Tag -eq 'server-node'})
    Assert ($nodes.Count -eq 2) 'same-city profiles must share a selectable marker'
    Assert (@($nodes|Where-Object {$_.Fill.Color.ToString() -eq '#FF2DAE70'}).Count -eq 1) 'fresh HTTPS success is green'
    Assert (@($nodes|Where-Object {$_.Fill.Color.ToString() -eq '#FFE04C53' -and $_.Opacity -eq 1}).Count -eq 1) 'fresh failure is clearly red'
    $country=@($map.Children|Where-Object {$_.Tag -eq 'PL'})[0]
    $old=$country.Fill
    $enter=[Windows.Input.MouseEventArgs]::new([Windows.Input.InputManager]::Current.PrimaryMouseDevice,0)
    $enter.RoutedEvent=[Windows.UIElement]::MouseEnterEvent;$country.RaiseEvent($enter)
    Assert ($country.Fill -ne $old) 'hover must highlight the country'
    $click=[Windows.Input.MouseButtonEventArgs]::new([Windows.Input.InputManager]::Current.PrimaryMouseDevice,0,[Windows.Input.MouseButton]::Left)
    $click.RoutedEvent=[Windows.UIElement]::MouseLeftButtonUpEvent;$country.RaiseEvent($click)
    Assert ($script:chosen.Id -eq $other.Id) 'country click must select its server'
    $before=$map.Children.Count;$good.CheckedUtc=[DateTime]::UtcNow.AddMinutes(-4);$map.RefreshStatus()
    Assert (@($nodes|Where-Object {$_.Fill.Color.ToString() -eq '#FF2DAE70'}).Count -eq 0) 'stale success must not remain green'
    $bad.CheckedUtc=[DateTime]::UtcNow.AddMinutes(-4);$map.RefreshStatus()
    Assert (@($nodes|Where-Object {$_.Fill.Color.ToString() -eq '#FFE04C53'}).Count -eq 0) 'stale failure must not remain red'
    $bad.CheckedUtc=[DateTime]::UtcNow
    Assert ($map.Children.Count -eq $before) 'health updates must reuse country geometry and nodes'
    $good.CheckedUtc=[DateTime]::UtcNow;$map.RefreshStatus()
    $map.Background=[Windows.Media.SolidColorBrush]::new($palette.Bg)
    $map.Measure([Windows.Size]::new(820,400));$map.Arrange([Windows.Rect]::new(0,0,820,400));$map.UpdateLayout()
    $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(820,400,96,96,[Windows.Media.PixelFormats]::Pbgra32);$image.Render($map)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
    $file=[IO.File]::Create((Join-Path $WorkPath (($theme-replace '[^a-zA-Z0-9]','-')+'.png')))
    try{$encoder.Save($file)}finally{$file.Dispose()}
}
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start()
$port=$listener.LocalEndpoint.Port;$listener.Stop();$profile.Port=$port
$serverConfig=@{log=@{level='error'};inbounds=@(@{type='shadowsocks';listen='127.0.0.1';listen_port=$port;method=$profile.Method;password=$profile.Password});outbounds=@(@{type='direct';tag='direct'});route=@{final='direct'}}|ConvertTo-Json -Depth 10
$serverFile=Join-Path $WorkPath 'fixture-server.json'
[IO.File]::WriteAllText($serverFile,$serverConfig,[Text.UTF8Encoding]::new($false))
$process=Start-Process -FilePath $CorePath -ArgumentList @('run','-c',('"'+$serverFile+'"')) -WindowStyle Hidden -PassThru
$test=$assembly.GetType('NovaVpn.ServerMapHealthService').GetMethod('TestAsync')
$runtime=[string](Join-Path $WorkPath 'private-runtime')
try{
    Start-Sleep -Milliseconds 800
    $task=$test.Invoke($null,@($profile,$CorePath,$runtime,[Threading.CancellationToken]::None));$result=$task.GetAwaiter().GetResult()
    Assert $result.Success 'HTTPS must pass through the controlled encrypted Shadowsocks profile'
    $profile.Password='incorrect-test-password'
    $task=$test.Invoke($null,@($profile,$CorePath,$runtime,[Threading.CancellationToken]::None));$result=$task.GetAwaiter().GetResult()
    Assert (-not $result.Success) 'incorrect credentials must fail despite working direct internet'
    $cancel=[Threading.CancellationTokenSource]::new();$cancel.CancelAfter(250)
    $task=$test.Invoke($null,@($profile,$CorePath,$runtime,$cancel.Token))
    try{$task.GetAwaiter().GetResult();throw 'cancellation was ignored'}catch{Assert $task.IsCanceled 'canceled probe task must be canceled'}finally{$cancel.Dispose()}
    Assert (@(Get-ChildItem $runtime -Filter '*.json').Count -eq 0) 'temporary profile configs must be removed'
}finally{if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose();Remove-Item -LiteralPath $serverFile -ErrorAction SilentlyContinue}
Write-Output 'PASS: five palettes; hover/click; clustered markers; stale-state expiry; geometry reuse; encrypted HTTPS success; wrong-password failure; cancellation and config cleanup.'

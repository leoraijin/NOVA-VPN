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
$configKey=$assembly.GetType('NovaVpn.SubscriptionManager').GetMethod('ConfigKey')
$good.ProfileKey=$configKey.Invoke($null,@($profile.psobject.BaseObject))
$bad.ProfileKey=$configKey.Invoke($null,@($other.psobject.BaseObject))
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
$systemProbe=$builder.GetMethod('BuildProbeWithRouting',[Reflection.BindingFlags]'NonPublic,Static')
$systemConfig=$systemProbe.Invoke($null,@($profile,19876,'fixture','fixture-password',$true))|ConvertFrom-Json
Assert ($config.route.auto_detect_interface -and $systemConfig.route.auto_detect_interface) 'probe must bind its uplink independently of the TUN'
Assert ($systemConfig.dns.servers[0].type -eq 'https' -and $systemConfig.dns.servers[0].server -eq '1.1.1.1' -and $systemConfig.dns.servers[0].tls.enabled) 'active probe must not use system FakeIP DNS'
Assert ($systemConfig.outbounds.Count -eq 1 -and $systemConfig.route.final -eq 'proxy') 'VPN-active probe must still verify the candidate outbound'
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start()
$port=$listener.LocalEndpoint.Port;$listener.Stop();$profile.Port=$port
$serverConfig=@{log=@{level='error'};inbounds=@(@{type='shadowsocks';listen='127.0.0.1';listen_port=$port;method=$profile.Method;password=$profile.Password});outbounds=@(@{type='direct';tag='direct'});route=@{final='direct'}}|ConvertTo-Json -Depth 10
$serverFile=Join-Path $WorkPath 'fixture-server.json'
[IO.File]::WriteAllText($serverFile,$serverConfig,[Text.UTF8Encoding]::new($false))
$process=Start-Process -FilePath $CorePath -ArgumentList @('run','-c',('"'+$serverFile+'"')) -WindowStyle Hidden -PassThru
$test=$assembly.GetType('NovaVpn.ServerMapHealthService').GetMethod('TestAsync')
$runtime=[string](Join-Path $WorkPath ('private-runtime-'+[Guid]::NewGuid().ToString('N')))
try{
    Start-Sleep -Milliseconds 800
    $task=$test.Invoke($null,@($profile,$CorePath,$runtime,[Threading.CancellationToken]::None));$result=$task.GetAwaiter().GetResult()
    Assert $result.Success 'HTTPS must pass through the controlled encrypted Shadowsocks profile'
    $catalogTest=$assembly.GetType('NovaVpn.ServerMapHealthService').GetMethod('TestCatalogAsync',[Reflection.BindingFlags]'NonPublic,Static')
    $scope=[Threading.CancellationTokenSource]::new()
    try {
        $first=$catalogTest.Invoke($null,@($profile,$CorePath,$runtime,$scope.Token))
        $second=$catalogTest.Invoke($null,@($profile,$CorePath,$runtime,[Threading.CancellationToken]::None))
        $scope.Cancel()
        try{$first.GetAwaiter().GetResult();throw 'catalog cancellation ignored'}catch{Assert $first.IsCanceled 'catalog request cancellation'}
        $shared=$second.GetAwaiter().GetResult()
        Assert $shared.Success 'closing one catalog subscriber stopped another'
        $shared.Success=$false
        $cached=$catalogTest.Invoke($null,@($profile,$CorePath,$runtime,[Threading.CancellationToken]::None)).GetAwaiter().GetResult()
        Assert ($cached.Success -and $cached.CheckedUtc -eq $shared.CheckedUtc) 'cached catalog result must be reused and isolated from display changes'
    } finally {$scope.Dispose()}
    $invalid=Profile 'DE Germany - Berlin'
    $invalid.Protocol=$profile.Protocol
    $invalid.Server=$profile.Server
    $invalid.Port=$profile.Port
    $invalid.Method=$profile.Method
    $invalid.Password='incorrect-batch-password'
    $silentPeer=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $silentPeer.Start();$invalid.Port=$silentPeer.LocalEndpoint.Port
    $valid=Profile 'PL Poland - Warsaw'
    $valid.Protocol=$profile.Protocol
    $valid.Server=$profile.Server
    $valid.Port=$profile.Port
    $valid.Method=$profile.Method
    $valid.Password=$profile.Password
    # A distinct but equivalent host spelling bypasses the previous cached success.
    $valid.Server='localhost'
    $batchSuccess=$catalogTest.Invoke($null,@($valid,$CorePath,$runtime,[Threading.CancellationToken]::None))
    $batchFailure=$catalogTest.Invoke($null,@($invalid,$CorePath,$runtime,[Threading.CancellationToken]::None))
    Assert $batchSuccess.GetAwaiter().GetResult().Success 'valid batch exit must pass HTTPS'
    Assert (-not $batchFailure.IsCompleted) 'healthy exit must publish before its timing-out batch neighbour'
    Assert (-not $batchFailure.GetAwaiter().GetResult().Success) 'dead batch exit must not fall through to the valid exit'
    $checkCatalog=$assembly.GetType('NovaVpn.ServerMapHealthService').GetMethod('CheckCatalogAsync',[Reflection.BindingFlags]'NonPublic,Static')
    $routeSuccess=$checkCatalog.Invoke($null,@($valid,$CorePath,$runtime,[Threading.CancellationToken]::None,$true,$true))
    $routeFailure=$checkCatalog.Invoke($null,@($invalid,$CorePath,$runtime,[Threading.CancellationToken]::None,$true,$true))
    $viaSystem=$routeSuccess.GetAwaiter().GetResult()
    $inconclusive=$routeFailure.GetAwaiter().GetResult()
    Assert ($viaSystem.Success -and $viaSystem.UsesSystemRoute) 'system routing must preserve positive HTTPS evidence for the candidate'
    Assert (-not $inconclusive.Success -and $inconclusive.Supported -and $inconclusive.UsesSystemRoute) 'independent failed HTTPS must be displayed as a completed check'
    Assert ($inconclusive.CheckedUtc -gt $batchFailure.GetAwaiter().GetResult().CheckedUtc) 'an explicit check must perform a new probe'
    $cancelled=Profile 'DE Germany - Hamburg'
    $cancelled.Protocol=$profile.Protocol
    $cancelled.Server=$profile.Server
    $cancelled.Port=$profile.Port
    $cancelled.Method=$profile.Method
    $cancelled.Password='cancelled-batch-password'
    $survivor=Profile 'FR France - Paris'
    $survivor.Protocol=$profile.Protocol
    $survivor.Server=$profile.Server
    $survivor.Port=$profile.Port
    $survivor.Method=$profile.Method
    $survivor.Password=$profile.Password
    # The Shadowsocks outbound ignores Path, but the profile identity includes it.
    $survivor.Path='/uncached-batch-survivor'
    $batchCancellation=[Threading.CancellationTokenSource]::new()
    try {
        $aborted=$catalogTest.Invoke($null,@($cancelled,$CorePath,$runtime,$batchCancellation.Token))
        $surviving=$catalogTest.Invoke($null,@($survivor,$CorePath,$runtime,[Threading.CancellationToken]::None))
        $timer=[Diagnostics.Stopwatch]::StartNew()
        while (@(Get-ChildItem $runtime -Recurse -File -Filter '*.json').Count -eq 0 -and $timer.Elapsed.TotalSeconds -lt 3) {
            Start-Sleep -Milliseconds 10
        }
        Assert (@(Get-ChildItem $runtime -Recurse -File -Filter '*.json').Count -eq 1) 'two parallel probes must start with one shared runtime config'
        $runtimeConfig=Get-ChildItem $runtime -Recurse -File -Filter '*.json' | Select-Object -First 1
        $sharedConfig=Get-Content -LiteralPath $runtimeConfig.FullName -Raw | ConvertFrom-Json
        Assert ($sharedConfig.inbounds.Count -eq 2) 'the running config must contain both independent listeners'
        $batchCancellation.Cancel()
        try { $aborted.GetAwaiter().GetResult(); throw 'catalog cancellation ignored' }
        catch { Assert $aborted.IsCanceled 'cancellation should reach the catalog caller' }
        Assert $surviving.GetAwaiter().GetResult().Success 'cancelling one exit must leave the other working'
    } finally { $batchCancellation.Dispose() }
    $profile.Password='incorrect-test-password'
    $task=$test.Invoke($null,@($profile,$CorePath,$runtime,[Threading.CancellationToken]::None));$result=$task.GetAwaiter().GetResult()
    Assert (-not $result.Success) 'incorrect credentials must fail despite working direct internet'
    $cancel=[Threading.CancellationTokenSource]::new();$cancel.CancelAfter(250)
    $task=$test.Invoke($null,@($profile,$CorePath,$runtime,$cancel.Token))
    try{$task.GetAwaiter().GetResult();throw 'cancellation was ignored'}catch{Assert $task.IsCanceled 'canceled probe task must be canceled'}finally{$cancel.Dispose()}
    Assert (@(Get-ChildItem $runtime -Recurse -File -Filter '*.json').Count -eq 0) 'temporary profile configs must be removed'
}finally{if($silentPeer){$silentPeer.Stop()};if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose();Remove-Item -LiteralPath $serverFile -ErrorAction SilentlyContinue}
Write-Output 'PASS: map styling; hover/click; clustered markers; stale-state expiry; geometry reuse; encrypted HTTPS success; wrong-password failure; cancellation and config cleanup.'

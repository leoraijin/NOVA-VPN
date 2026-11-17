param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,System.Net.Http
$a=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
function Assert($ok,$message){if(-not $ok){throw $message}}
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
$s=[Activator]::CreateInstance($a.GetType('NovaVpn.AppState'))
$p=[Activator]::CreateInstance($a.GetType('NovaVpn.VpnProfile'))
$p.Name='Fixture VLESS';$p.Protocol='vless';$p.Server='server.example';$p.Port=443;$p.UserId='00000000-0000-4000-8000-000000000001';$p.Transport='xhttp';$p.Path='/fixture';$p.Security='tls';$p.Sni='server.example';$p.IsFavorite=$true
$p.SubscriptionUrl='https://subscription.example/private';$p.HealthCoreKey='private-local-path';$s.Profiles.Add($p)
$q=[Activator]::CreateInstance($a.GetType('NovaVpn.VpnProfile'));$q.Protocol='sing-box';$q.Name='Full JSON';$q.RawJson='{"outbounds":[{"type":"socks","server":"server.example","server_port":1080,"username":"fixture","password":"fixture-only"}]}';$s.Profiles.Add($q)
$s.Routing.DirectDomains.Add('route-must-not-travel.example')
$key=$a.GetType('NovaVpn.SubscriptionManager').GetMethod('ConfigKey')
$originalId=$p.Id;$originalKey=$key.Invoke($null,@($p))
$path=[IO.Path]::GetFullPath((Join-Path $WorkPath 'servers.json'))
$a.GetType('NovaVpn.PortableConfigService').GetMethod('ExportProfiles').Invoke($null,@($s,$path))
$text=[IO.File]::ReadAllText($path)
Assert ($text -notmatch 'route-must-not-travel|private-local-path|subscription.example') 'export must contain server configs only'
Assert ($p.Id -eq $originalId -and $p.SubscriptionUrl -eq 'https://subscription.example/private') 'export must not mutate source profiles'
$result=$a.GetType('NovaVpn.LinkParser').GetMethod('ImportDetailedAsync').Invoke($null,@($text)).GetAwaiter().GetResult()
Assert ($result.Profiles.Count -eq 2 -and -not $result.IsSubscription) 'native export must import as two manual profiles'
Assert ($key.Invoke($null,@($result.Profiles[0])) -eq $originalKey) 'XHTTP and TLS parameters must survive round trip'
Assert ($result.Profiles[0].Id -ne $originalId -and $result.Profiles[0].IsFavorite) 'new ids and favorites'
Assert ($result.Profiles[1].RawJson -eq $q.RawJson) 'full JSON must survive round trip'
Assert ($result.RoutingPreviews.Count -eq 0) 'server import must not propose replacing routes'
[Windows.Application]::ResourceAssembly=$a
$app=New-Object Windows.Application;$app.ShutdownMode='OnExplicitShutdown'
$window=[Activator]::CreateInstance($a.GetType('NovaVpn.MainWindow'),[object[]]@($true))
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
try {
 $a.GetType('NovaVpn.MainWindow').GetField('state',$flags).SetValue($window,$s)
 $merge=$a.GetType('NovaVpn.MainWindow').GetMethod('MergeImportedProfiles',$flags)
 Assert ($merge.Invoke($window,@($result)) -eq 0) 'reimport must not duplicate existing configs'
 Assert ($s.Profiles.Count -eq 2 -and $s.Routing.DirectDomains.Contains('route-must-not-travel.example')) 'routes and profiles must survive reimport'
}finally {$window.Close();$app.Shutdown()}
Remove-Item -LiteralPath $path
'PASS: server-only export, XHTTP/full JSON round trip, no source mutation, duplicate merge and routes preserved.'

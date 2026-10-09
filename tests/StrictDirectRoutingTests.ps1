param([string]$AssemblyPath,[string]$CorePath,[string]$WorkPath)
$ErrorActionPreference='Stop'
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$routing=$assembly.CreateInstance('NovaVpn.RoutingSettings')
$routing.DirectDomains.Add('example.test')
$routing.ProxyDomains.Add('api.example.test')
$routing.ProxyProcesses.Add('chrome.exe')
$routing.EnableSniffing=$false
$routing.AdvancedRules.Add('domain:example.test -> proxy')
$profile=$assembly.CreateInstance('NovaVpn.VpnProfile')
$profile.Protocol='vless';$profile.Server='127.0.0.1';$profile.Port=1
$profile.UserId='11111111-1111-1111-1111-111111111111'
$builder=$assembly.GetType('NovaVpn.ConfigBuilder')
function Assert($value,$message){if(-not $value){throw $message}}
function Check($json,$name){
 $config=ConvertFrom-Json $json
 $direct=@($config.outbounds|Where-Object tag -eq 'nova-strict-direct')[0]
 Assert ($direct.type -eq 'direct' -and -not $direct.detour) 'Strict exit must not have proxy detour'
 Assert ($direct.domain_resolver -eq 'nova-strict-direct-dns') 'Direct resolver missing'
 $dns=@($config.dns.servers|Where-Object tag -eq 'nova-strict-direct-dns')[0]
 Assert ($dns.type -eq 'local' -and $dns.detour -eq 'nova-strict-direct') 'DNS must use dedicated direct exit'
 Assert ($config.dns.rules[0].server -eq 'nova-strict-direct-dns') 'DNS priority missing'
 Assert ($config.dns.rules[0].domain -contains 'example.test') 'Root DNS missing'
 Assert ($config.dns.rules[0].domain_suffix -contains '.example.test') 'Subdomain DNS missing'
 $terminal=@($config.route.rules|Where-Object {$_.action -eq 'route'})[0]
 Assert ($terminal.outbound -eq 'nova-strict-direct') 'Site must beat VPN/advanced/browser rules'
 Assert ($terminal.domain_suffix -contains '.example.test') 'Subdomain route missing'
 Assert ($config.route.rules[0].action -eq 'sniff') 'Direct site detection must not be disabled'
 $path=Join-Path $WorkPath ($name+'.json')
 [IO.File]::WriteAllText($path,$json,(New-Object Text.UTF8Encoding($false)))
 & $CorePath check -c $path
 Assert ($LASTEXITCODE -eq 0) ('Core rejected '+$name)
}
foreach($mode in @('Smart','Global','Direct')){
 $routing.Mode=$mode
 Check ($builder.GetMethod('Build').Invoke($null,@($profile,$routing))) ('normal-'+$mode)
}
$profile.Protocol='sing-box'
$profile.RawJson='{"inbounds":[{"type":"tun","tag":"tun","address":["172.19.0.1/30"]}],"outbounds":[{"type":"socks","tag":"proxy","server":"127.0.0.1","server_port":1},{"type":"direct","tag":"direct","domain_resolver":"remote"}],"dns":{"servers":[{"type":"https","tag":"remote","server":"1.1.1.1","detour":"proxy"}],"final":"remote","rules":[{"domain_suffix":[".test"],"action":"route","server":"remote"}]},"route":{"rules":[{"action":"route","outbound":"proxy"}],"final":"proxy"}}'
Check ($builder.GetMethod('Build').Invoke($null,@($profile,$routing))) 'imported-proxy-dns'
$decision=$assembly.GetType('NovaVpn.RoutingInspector').GetMethod('Explain').Invoke($null,@($routing,'api.example.test','chrome.exe'))
Assert ($decision.Route -eq 'direct') 'Inspector must agree for nested VPN conflict'
$routing.DirectDomains.Clear();$routing.DirectDomains.Add('127.0.0.1')
$ipJson=$builder.GetMethod('Build').Invoke($null,@($profile,$routing))
$ipConfig=ConvertFrom-Json $ipJson
Assert (@($ipConfig.route.rules|Where-Object outbound -eq 'nova-strict-direct')[0].ip_cidr -contains '127.0.0.1/32') 'IP route missing'
[IO.File]::WriteAllText((Join-Path $WorkPath 'ip.json'),$ipJson,(New-Object Text.UTF8Encoding($false)))
& $CorePath check -c (Join-Path $WorkPath 'ip.json')
Assert ($LASTEXITCODE -eq 0) 'Core rejected IP direct rule'
'PASS: three modes, imported proxy DNS, direct DNS, subdomains, conflicts and IP; five core schema checks.'

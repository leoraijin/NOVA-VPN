param(
 [Parameter(Mandatory=$true)][string]$AssemblyPath,
 [int]$RefreshCount=60
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$public=[Reflection.BindingFlags]'Static,Public,NonPublic'
$profileType=$assembly.GetType('NovaVpn.VpnProfile',$true)
$healthType=$assembly.GetType('NovaVpn.ServerMapHealth',$true)
$configKey=$assembly.GetType('NovaVpn.SubscriptionManager',$true).GetMethod('ConfigKey',$public)
$themeType=$assembly.GetType('NovaVpn.ThemePalette',$true)
$prefs=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences',$true))
$theme=$themeType.GetMethod('Create',$public).Invoke($null,@($prefs))
$dictionaryType=[Collections.Generic.Dictionary``2].MakeGenericType([string],$healthType)
$health=[Activator]::CreateInstance($dictionaryType)
$select=[Action``1].MakeGenericType($profileType)
$callback=[System.Management.Automation.LanguagePrimitives]::ConvertTo({},$select)
$map=[Activator]::CreateInstance($assembly.GetType('NovaVpn.InteractiveServerMap',$true),@($theme,$health,'none',$callback))
$map.Width=1000;$map.Height=600
for($index=0;$index -lt 80;$index++) {
 $profiles=[Array]::CreateInstance($profileType,25)
 for($item=0;$item -lt $profiles.Length;$item++) {
  $profile=[Activator]::CreateInstance($profileType)
  $profile.Name="Test-$index-$item";$profile.Server="test-$index-$item.example.test";$profile.Port=443;$profile.Protocol='vless'
  $sample=[Activator]::CreateInstance($healthType)
  $sample.ProfileKey=$configKey.Invoke($null,@($profile))
  $sample.CheckedUtc=[DateTime]::UtcNow;$sample.Success=$item -ne 0;$sample.LatencyMs=20+$index+$item
  $health.Add($profile.Id,$sample);$profiles.SetValue($profile,$item)
 }
 $map.AddServerNode([Windows.Point]::new(20+($index%10)*85,20+[Math]::Floor($index/10)*60),"Fixture-$index",$profiles,[Action[bool]]{})
}
$map.RefreshStatus()
$self=[Diagnostics.Process]::GetCurrentProcess()
$self.Refresh();$startCpu=$self.TotalProcessorTime.TotalMilliseconds
$watch=[Diagnostics.Stopwatch]::StartNew()
for($iteration=0;$iteration -lt $RefreshCount;$iteration++){$map.RefreshStatus()}
$watch.Stop();$self.Refresh()
[pscustomobject]@{
 version=$assembly.GetName().Version.ToString()
 profiles=$health.Count
 nodes=80
 refreshes=$RefreshCount
 processCpuMs=[Math]::Round($self.TotalProcessorTime.TotalMilliseconds-$startCpu,2)
 wallMs=[Math]::Round($watch.Elapsed.TotalMilliseconds,2)
 scope='Synthetic WPF map refresh only. No VPN process, network requests or user data.'
} | ConvertTo-Json -Compress

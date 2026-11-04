param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$folder = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
New-Item -ItemType Directory $folder | Out-Null
try {
    $source = New-Object NovaVpn.AppState
    $source.Routing.DirectDomains.Add('example.org')
    $source.Routing.DirectProcessPaths.Add('C:\Games\Example\game.exe')
    $source.Routing.ProxyProcessPaths.Add('C:\Apps\Other\other.exe')
    $path = Join-Path $folder 'roundtrip.nova-settings'
    [NovaVpn.PortableConfigService]::ExportSettingsOnly($source,$path)
    $imported = [NovaVpn.PortableConfigService]::ImportSettingsOnly($path)
    $target = New-Object NovaVpn.AppState
    $profile = New-Object NovaVpn.VpnProfile
    $target.Profiles.Add($profile)
    [NovaVpn.PortableConfigService]::ApplySettingsOnly($target,$imported)
    if (!$target.Routing.DirectDomains.Contains('example.org')) { throw 'Domain lost' }
    if (!$target.Routing.DirectProcessPaths.Contains('C:\Games\Example\game.exe')) { throw 'Direct process path lost' }
    if (!$target.Routing.ProxyProcessPaths.Contains('C:\Apps\Other\other.exe')) { throw 'Proxy process path lost' }
    if ($target.Profiles.Count -ne 1 -or ![object]::ReferenceEquals($target.Profiles[0],$profile)) { throw 'Existing profile changed' }
    if ($imported.Profiles.Count -ne 0) { throw 'Profiles leaked into settings' }
    if ($source.Routing.DirectProcessPaths.Count -ne 1) { throw 'Export modified original routing' }
    '{"Format":"nova-settings-no-secrets-v1","State":{"SchemaVersion":1}}' | Set-Content $path -Encoding utf8
    $legacy = [NovaVpn.PortableConfigService]::ImportSettingsOnly($path)
    if ($null -eq $legacy.Routing -or $null -eq $legacy.Visual -or $null -eq $legacy.ConnectionHistory) { throw 'Legacy fields not normalized' }
    Write-Output 'Settings roundtrip, process paths, profile preservation and legacy fields PASS'
} finally {
    Remove-Item -LiteralPath $folder -Recurse -Force
}

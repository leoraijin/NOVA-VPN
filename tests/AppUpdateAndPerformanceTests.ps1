param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath
)

$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadWithPartialName('System.Web.Extensions')
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERT FAILED: $message" }
}

$service = $assembly.GetType('NovaVpn.AppUpdateService')
$parse = $service.GetMethod('ParseLatestRelease', [Reflection.BindingFlags] 'Public,Static')
$validRelease = @'
{"tag_name":"v2.1.8","html_url":"https://github.com/leoraijin/NOVA-VPN/releases/tag/v2.1.8","assets":[{"name":"NOVA.VPN.Setup.2.1.7.exe","browser_download_url":"https://github.com/leoraijin/NOVA-VPN/releases/download/v2.1.8/NOVA.VPN.Setup.2.1.7.exe","size":33000000},{"name":"NOVA.VPN.Setup.2.1.8.exe","browser_download_url":"https://github.com/leoraijin/NOVA-VPN/releases/download/v2.1.8/NOVA.VPN.Setup.2.1.8.exe","size":33000000,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
'@
$release = $parse.Invoke($null, @($validRelease))
Assert-True ($release.Version.ToString() -eq '2.1.8.0') 'release tag should normalize to a four-part version'
Assert-True ($release.DownloadUrl -eq 'https://github.com/leoraijin/NOVA-VPN/releases/download/v2.1.8/NOVA.VPN.Setup.2.1.8.exe') 'updater should select only the installer matching the latest tag'
Assert-True ($release.AssetName -eq 'NOVA.VPN.Setup.2.1.8.exe' -and $release.Size -eq 33000000) 'updater should retain the expected asset name and byte size'
Assert-True ($release.Sha256 -eq ('a' * 64)) 'updater should retain the GitHub SHA-256 digest when present'

$latestPage = $service.GetProperty('LatestReleasePageUrl', [Reflection.BindingFlags] 'Public,Static').GetValue($null, $null)
Assert-True ($latestPage -eq 'https://github.com/leoraijin/NOVA-VPN/releases/latest') 'fallback should target the public latest-release page'
$olderRelease = $service.GetMethod('IsOlderThanInstalled', [Reflection.BindingFlags] 'Public,Static')
Assert-True ($olderRelease.Invoke($null, @([Version]'2.1.8.0', [Version]'2.1.7.0'))) 'updater must reject a GitHub release older than the installed build'
Assert-True (-not $olderRelease.Invoke($null, @([Version]'2.1.8.0', [Version]'2.1.8.0'))) 'updater must allow the current GitHub release without treating it as a downgrade'
$newerRelease = $service.GetMethod('IsUpdateAvailable', [Reflection.BindingFlags] 'Public,Static')
Assert-True ($newerRelease.Invoke($null, @([Version]'2.1.7.0', [Version]'2.1.8.0'))) 'startup should offer a strictly newer release'
Assert-True (-not $newerRelease.Invoke($null, @([Version]'2.1.8.0', [Version]'2.1.8.0'))) 'startup should not prompt when the installed version is current'
Assert-True (-not $newerRelease.Invoke($null, @([Version]'2.1.9.0', [Version]'2.1.8.0'))) 'startup must never offer a downgrade'

$unsafeRelease = $validRelease.Replace('https://github.com/leoraijin/NOVA-VPN/releases/download/v2.1.8/NOVA.VPN.Setup.2.1.8.exe', 'https://evil.example/installer.exe')
$unsafeRejected = $false
try { $parse.Invoke($null, @($unsafeRelease)) | Out-Null } catch { $unsafeRejected = $true }
Assert-True $unsafeRejected 'installer link on an unexpected host must be rejected'

$wrongAssetRelease = $validRelease.Replace('https://github.com/leoraijin/NOVA-VPN/releases/download/v2.1.8/NOVA.VPN.Setup.2.1.8.exe', 'https://github.com/leoraijin/NOVA-VPN/releases/download/v2.1.8/other-installer.exe')
$wrongAssetRejected = $false
try { $parse.Invoke($null, @($wrongAssetRelease)) | Out-Null } catch { $wrongAssetRejected = $true }
Assert-True $wrongAssetRejected 'release URL must point to the selected installer asset name'

$missingInstaller = $validRelease.Replace('NOVA.VPN.Setup.2.1.8.exe', 'other-installer.exe')
$missingRejected = $false
try { $parse.Invoke($null, @($missingInstaller)) | Out-Null } catch { $missingRejected = $true }
Assert-True $missingRejected 'release without a version-matching NOVA installer must be rejected'

$missingSize = $validRelease.Replace(',"size":33000000,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"', '')
$badMetadataRejected = $false
try { $parse.Invoke($null, @($missingSize)) | Out-Null } catch { $badMetadataRejected = $true }
Assert-True $badMetadataRejected 'release without a trustworthy installer size must be rejected'

$badDigest = $validRelease.Replace('sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
$badDigestRejected = $false
try { $parse.Invoke($null, @($badDigest)) | Out-Null } catch { $badDigestRejected = $true }
Assert-True $badDigestRejected 'release with an unsupported asset digest must be rejected'

$updater = [Activator]::CreateInstance($service)
$unsafeAsset = $assembly.CreateInstance('NovaVpn.AppReleaseInfo')
$unsafeAsset.Tag = 'v2.1.8'
$unsafeAsset.AssetName = 'NOVA.VPN.Setup.2.1.8.exe'
$unsafeAsset.DownloadUrl = 'https://evil.example/NOVA.VPN.Setup.2.1.8.exe'
$unsafeAsset.Size = 33000000
$unsafePath = Join-Path $env:TEMP ('NOVA-VPN-unsafe-update-' + [guid]::NewGuid().ToString('N') + '.exe')
$unsafeDownloadRejected = $false
try {
    $downloadTask = $service.GetMethod('DownloadInstallerAsync').Invoke($updater, @($unsafeAsset, $unsafePath))
    $downloadTask.GetAwaiter().GetResult()
} catch { $unsafeDownloadRejected = $true }
finally { $updater.Dispose() }
Assert-True $unsafeDownloadRejected 'installer download must reject an untrusted URL before accessing the network'
Assert-True (-not (Test-Path -LiteralPath $unsafePath)) 'rejected download must not create a file'

$mainWindow = $assembly.GetType('NovaVpn.MainWindow')
$startupPolicy = $mainWindow.GetMethod('ShouldCheckForApplicationUpdates', [Reflection.BindingFlags] 'NonPublic,Static')
Assert-True ($startupPolicy.Invoke($null, [object[]]@($false, $false, $false))) 'regular app launches should perform the startup update check'
Assert-True (-not $startupPolicy.Invoke($null, [object[]]@($true, $false, $false))) 'visual snapshot launches must not query GitHub'
Assert-True (-not $startupPolicy.Invoke($null, [object[]]@($false, $true, $false))) 'closing windows must not start an update check'
Assert-True (-not $startupPolicy.Invoke($null, [object[]]@($false, $false, $true))) 'a concurrent updater must prevent a second check'
$visualPolicy = $mainWindow.GetMethod('IsVisualSamplingAllowed', [Reflection.BindingFlags] 'NonPublic,Static')
$networkPolicy = $mainWindow.GetMethod('ShouldCollectNetworkCounters', [Reflection.BindingFlags] 'NonPublic,Static')
Assert-True ($visualPolicy.Invoke($null, [object[]]@($false, $false, 'home', $true, $false))) 'visible home page should allow live visual updates'
Assert-True (-not $visualPolicy.Invoke($null, [object[]]@($false, $false, 'servers', $true, $false))) 'other pages should stop live visual updates'
Assert-True (-not $visualPolicy.Invoke($null, [object[]]@($false, $false, 'home', $false, $false))) 'hidden window should stop live visual updates'
Assert-True (-not $visualPolicy.Invoke($null, [object[]]@($false, $false, 'home', $true, $true))) 'minimized window should stop live visual updates'
Assert-True (-not $visualPolicy.Invoke($null, [object[]]@($true, $false, 'home', $true, $false))) 'snapshot mode should not start live visual updates'
Assert-True (-not $visualPolicy.Invoke($null, [object[]]@($false, $true, 'home', $true, $false))) 'closing window should stop live visual updates'

$connected = [Enum]::Parse($assembly.GetType('NovaVpn.CoreStatus'), 'Connected')
$disconnected = [Enum]::Parse($assembly.GetType('NovaVpn.CoreStatus'), 'Disconnected')
$connecting = [Enum]::Parse($assembly.GetType('NovaVpn.CoreStatus'), 'Connecting')
Assert-True ($networkPolicy.Invoke($null, @($connected))) 'network counters should run while VPN is connected'
Assert-True (-not $networkPolicy.Invoke($null, @($disconnected))) 'network counters should not run while VPN is disconnected'
Assert-True (-not $networkPolicy.Invoke($null, @($connecting))) 'network counters should not run while VPN is connecting'

$parser = $assembly.GetType('NovaVpn.LinkParser')
$invalidJsonRejected = $false
try { $parser.GetMethod('ParseSingle').Invoke($null, @('{invalid')) | Out-Null } catch { $invalidJsonRejected = $true }
Assert-True $invalidJsonRejected 'malformed sing-box JSON must not be accepted as an importable profile'

Write-Output 'Updater link parsing and visual sampling policy tests passed.'

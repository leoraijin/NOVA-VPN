param([string]$AssemblyPath,[string]$InstallerPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if($null -eq [Windows.Application]::Current){$app=New-Object Windows.Application}
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$window=[Activator]::CreateInstance($assembly.GetType('NovaVpn.MainWindow'),@($true))
try {
 if($null -eq $window.Icon -or $window.Icon.PixelWidth -lt 16){throw 'Missing window icon'}
 $setup=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($InstallerPath))
 $method=$setup.GetType('NovaInstaller').GetMethod('CreateShortcut',[Reflection.BindingFlags]'Static,NonPublic')
 $folder=Join-Path ([IO.Path]::GetTempPath()) ('NovaIconTest-'+[Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path (Join-Path $folder 'Assets') | Out-Null
 Copy-Item -LiteralPath 'work/public-source-audit/Assets/nova-v2.ico' -Destination (Join-Path $folder 'Assets/nova-v2.ico')
 $link=Join-Path $folder 'NOVA VPN.lnk'
 $method.Invoke($null,[object[]]@([string]$link,[string][IO.Path]::GetFullPath($AssemblyPath),[string]$folder,[string]'')) | Out-Null
 $shell=New-Object -ComObject WScript.Shell
 $shortcut=$shell.CreateShortcut($link)
 if($shortcut.IconLocation -notlike '*nova-v2.ico*'){throw 'Shortcut lost explicit icon'}
 $iconPath=$shortcut.IconLocation.Substring(0,$shortcut.IconLocation.LastIndexOf(','))
 if($iconPath.Contains('"') -or -not [IO.File]::Exists($iconPath)){throw 'Shell icon path contains quotes or does not exist'}
 Write-Output ('PASS: embedded WPF window icon '+$window.Icon.PixelWidth+' px; shell shortcut points to installed ICO. Test-only shortcut: '+$link)
} finally { $window.Close() }

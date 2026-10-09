param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$AppPath
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($InstallerPath))
$stream=$assembly.GetManifestResourceStream('NOVA.Payload.zip')
if($null -eq $stream){throw 'Missing embedded payload'}
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read)
try {
    $names=@($zip.Entries | ForEach-Object {$_.FullName.Replace('\','/')})
    foreach($required in @('NOVA VPN.exe','core/sing-box.exe','zapret/current/general.bat','zapret/current/nova-version.txt')) {
        if($names -notcontains $required){throw ('Missing component: '+$required)}
    }
    foreach($name in $names) {
        if($name -match '(^|/)(state|settings|profiles|subscription|backup)\.(json|db|txt)$'){throw 'Private state in package'}
        if($name -match '(^|/)\.\.?(/|$)' -or [IO.Path]::IsPathRooted($name)){throw 'Unsafe archive path'}
    }
    $entry=$zip.GetEntry('NOVA VPN.exe').Open()
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$embeddedHash=[BitConverter]::ToString($sha.ComputeHash($entry)).Replace('-','')} finally {$sha.Dispose();$entry.Dispose()}
    if($embeddedHash -ne (Get-FileHash -LiteralPath $AppPath -Algorithm SHA256).Hash){throw 'Payload differs from tested application'}
    $appVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($AppPath)).FileVersion
    if($assembly.GetName().Version.ToString() -ne $appVersion){throw 'Installer/application version mismatch'}
    $changelogEntry=$zip.GetEntry('CHANGELOG.txt')
    if($null -eq $changelogEntry){throw 'Missing bundled changelog'}
    $reader=[IO.StreamReader]::new($changelogEntry.Open())
    try {$changelog=$reader.ReadToEnd()} finally {$reader.Dispose()}
    if($changelog -notmatch ('NOVA VPN '+([Version]$appVersion).ToString(3))){throw 'Bundled changelog does not include the current release summary'}
    if($assembly.GetManifestResourceNames() -notcontains 'NOVA.Uninstaller.exe'){throw 'Missing uninstaller'}
    Write-Output ('PASS: embedded build '+$appVersion+', '+$names.Count+' files, core and Zapret included, no private state paths, safe archive paths.')
} finally {$zip.Dispose();$stream.Dispose()}

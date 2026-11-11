function Assert-NovaFileInventory {
    param([Parameter(Mandatory=$true)][string]$Root,[Parameter(Mandatory=$true)]$Files,[string[]]$Ignore=@())
    Set-StrictMode -Version Latest
    $Root=[IO.Path]::GetFullPath($Root).TrimEnd('\');$prefix=$Root+'\'
    $item=Get-Item -LiteralPath $Root -Force
    if(! $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid package root'}
    $expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $Files){
        $relative=[string]$file.path
        if(!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or @($relative.Split([char[]]'\/') | Where-Object {$_ -eq '.' -or $_ -eq '..' -or $_ -eq ''}).Count){throw 'Unsafe inventory path'}
        $target=[IO.Path]::GetFullPath((Join-Path $Root $relative))
        if(!$target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or !$expected.Add($relative.Replace('/','\'))){throw 'Duplicate or escaping inventory path'}
        if(([string]$file.sha256) -notmatch '^[0-9A-Fa-f]{64}$'){throw 'Invalid inventory checksum'}
        $actual=Get-Item -LiteralPath $target -Force
        if($actual.PSIsContainer -or ($actual.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $actual.Length -ne [long]$file.bytes -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256){throw "Package file differs: $relative"}
    }
    foreach($entry in Get-ChildItem -LiteralPath $Root -Recurse -Force){
        if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Package contains a reparse point'}
        if(!$entry.PSIsContainer){$relative=$entry.FullName.Substring($prefix.Length);if(!$expected.Contains($relative) -and $Ignore -notcontains $relative){throw "Unlisted package file: $relative"}}
    }
}
function Get-NovaFileInventory {
    param([string]$Root,[string[]]$Ignore=@())
    $Root=[IO.Path]::GetFullPath($Root).TrimEnd('\');$prefix=$Root+'\'
    foreach($item in Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName){$relative=$item.FullName.Substring($prefix.Length);if($Ignore -contains $relative){continue};[pscustomobject]@{path=$relative;bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash}}
}
function Assert-NovaCorePackage {
    param([Parameter(Mandatory=$true)][string]$PackageDirectory)
    Set-StrictMode -Version Latest
    $manifest=Get-Content -LiteralPath (Join-Path $PackageDirectory 'core-package.json') -Raw | ConvertFrom-Json
    if($manifest.format -ne 'nova-patched-core-package-v1' -or $manifest.version -ne '1.13.14-nova-halfclose.2' -or $manifest.sourceCommit -ne '25a600db24f7680ad9806ce5427bd0ab8afe1114'){throw 'Unsupported core package'}
    $required=@('core\sing-box.exe','core\LICENSE-sing-box.txt','core\LICENSE-Go.txt','core\NOVA-core-build.json','core\source\sing-box-source-kit.zip')
    if(@($manifest.files).Count -ne $required.Count){throw 'Unexpected core package inventory'}
    foreach($name in $required){if(@($manifest.files|Where-Object {$_.path -eq $name}).Count -ne 1){throw "Required package file absent: $name"}}
    Assert-NovaFileInventory -Root $PackageDirectory -Files $manifest.files -Ignore @('core-package.json')
    $metadata=Get-Content -LiteralPath (Join-Path $PackageDirectory 'core\NOVA-core-build.json') -Raw|ConvertFrom-Json
    if($metadata.version -ne $manifest.version -or $metadata.coreSHA256 -ne (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'core\sing-box.exe') -Algorithm SHA256).Hash){throw 'Core metadata differs from package'}
    return $manifest
}

function Copy-NovaCorePackageToPayload {
    param([Parameter(Mandatory=$true)][string]$PackageDirectory,[Parameter(Mandatory=$true)][string]$PayloadDirectory)
    $manifest=Assert-NovaCorePackage -PackageDirectory $PackageDirectory
    $PayloadDirectory=[IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
    $packageRoot=[IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
    if($PayloadDirectory -eq $packageRoot -or $PayloadDirectory.StartsWith($packageRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $packageRoot.StartsWith($PayloadDirectory+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package and payload must be separate trees'}
    $root=Get-Item -LiteralPath $PayloadDirectory -Force
    if(!$root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid payload root'}
    foreach($entry in Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -Force){if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Payload contains a reparse point'}}
    foreach($member in $manifest.files){
        $target=Join-Path $PayloadDirectory $member.path
        New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null
        Copy-Item -LiteralPath (Join-Path $packageRoot $member.path) -Destination $target -Force
    }
    $others=@(Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File|ForEach-Object {
        $relative=$_.FullName.Substring($PayloadDirectory.Length+1)
        if($manifest.files.path -notcontains $relative){$relative}
    })
    Assert-NovaFileInventory -Root $PayloadDirectory -Files $manifest.files -Ignore $others
}
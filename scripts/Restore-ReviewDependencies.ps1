param([string]$Destination)
$ErrorActionPreference='Stop'
$source=Split-Path -Parent $PSScriptRoot
if(!$Destination){$Destination=Join-Path $source 'artifacts\dependencies'}
$version='18.1.0'
$sha256='59FFE65ADE67AD9D886267F877B634A450363CB81B94E19DB9CA4C36461416F6'
$package=Join-Path $Destination "yamldotnet.$version.zip"
$extract=Join-Path $Destination "yamldotnet.$version"
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
if(!(Test-Path -LiteralPath $package)){
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/yamldotnet/$version/yamldotnet.$version.nupkg" -OutFile $package
}
if((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $sha256){throw 'YamlDotNet package checksum mismatch'}
if(!(Test-Path -LiteralPath (Join-Path $extract 'lib\net47\YamlDotNet.dll'))){Expand-Archive -LiteralPath $package -DestinationPath $extract -Force}
Copy-Item -LiteralPath (Join-Path $extract 'lib\net47\YamlDotNet.dll') -Destination (Join-Path $Destination 'YamlDotNet.dll') -Force
Write-Output (Join-Path $Destination 'YamlDotNet.dll')

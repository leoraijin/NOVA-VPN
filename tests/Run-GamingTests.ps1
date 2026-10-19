param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$CorePath,
    [Parameter(Mandatory=$true)][string]$WorkPath,
    [Parameter(Mandatory=$true)][string]$CscPath
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $WorkPath -Force | Out-Null
$testExe = Join-Path $WorkPath 'GamingTests.exe'
$testApp = Join-Path $WorkPath 'NOVA VPN.exe'
Copy-Item -LiteralPath $AssemblyPath -Destination $testApp -Force
Copy-Item -LiteralPath (Join-Path (Split-Path ([IO.Path]::GetFullPath($AssemblyPath))) 'NOVA Core Host.exe') -Destination (Join-Path $WorkPath 'NOVA Core Host.exe') -Force
& $CscPath /nologo /target:exe /langversion:latest "/out:$testExe" "/reference:$testApp" /reference:System.Web.Extensions.dll /reference:System.Runtime.Serialization.dll (Join-Path $PSScriptRoot 'GamingTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Gaming test compilation failed' }
& $testExe ([IO.Path]::GetFullPath($CorePath)) ([IO.Path]::GetFullPath((Join-Path $WorkPath 'runtime')))
if ($LASTEXITCODE -ne 0) { throw 'Gaming tests failed' }

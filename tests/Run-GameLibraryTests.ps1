param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath,[Parameter(Mandatory=$true)][string]$CscPath,[string]$CorePath)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $WorkPath -Force | Out-Null
$testApp=Join-Path $WorkPath 'NOVA VPN.exe'
Copy-Item -LiteralPath $AssemblyPath -Destination $testApp -Force
Copy-Item -LiteralPath (Join-Path (Split-Path ([IO.Path]::GetFullPath($AssemblyPath))) 'NOVA Core Host.exe') -Destination (Join-Path $WorkPath 'NOVA Core Host.exe') -Force
foreach($name in @('GameLibraryTests','AutomaticGamingTests','GameUdpDiscoveryTests','UdpTrafficFixture','CoreLifetimeTests','GameTunIsolationTests','GameTrafficFixture')) {
  & $CscPath /nologo /target:exe /langversion:latest "/out:$(Join-Path $WorkPath ($name+'.exe'))" "/reference:$testApp" /reference:System.Web.Extensions.dll /reference:System.Runtime.Serialization.dll (Join-Path $PSScriptRoot ($name+'.cs'))
  if($LASTEXITCODE -ne 0){throw 'Extended test compilation failed'}
}
& $CscPath /nologo /target:exe "/out:$(Join-Path $WorkPath 'CoreTreeFixture.exe')" (Join-Path $PSScriptRoot 'CoreTreeFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
& (Join-Path $WorkPath 'GameLibraryTests.exe') (Join-Path $WorkPath 'library-fixtures')
if($LASTEXITCODE -ne 0){throw 'Game library/policy tests failed'}
& (Join-Path $WorkPath 'AutomaticGamingTests.exe')
if($LASTEXITCODE -ne 0){throw 'Automatic selection tests failed'}
& (Join-Path $WorkPath 'GameUdpDiscoveryTests.exe') ([IO.Path]::GetFullPath($WorkPath))
if($LASTEXITCODE -ne 0){throw 'UDP discovery tests failed'}
& (Join-Path $WorkPath 'CoreLifetimeTests.exe') (Join-Path $WorkPath 'CoreTreeFixture.exe') $WorkPath
if($LASTEXITCODE -ne 0){throw 'Core lifetime tests failed'}
if($CorePath) {
  & (Join-Path $WorkPath 'GameTunIsolationTests.exe') ([IO.Path]::GetFullPath($CorePath)) ([IO.Path]::GetFullPath($WorkPath))
  if($LASTEXITCODE -ne 0){throw 'Real TUN isolation test failed'}
}

param(
    [string]$CompilerPath,
    [string]$OutputPath,
    [string]$DependencyPath,
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
if (!$OutputPath) { $OutputPath = Join-Path $source 'artifacts\review' }
if (!$CompilerPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($msbuild) { $CompilerPath = Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe' }
    }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)) { throw 'Pass -CompilerPath with a Roslyn csc.exe path, or install Visual Studio Build Tools.' }
if (!$DependencyPath) { $DependencyPath = Join-Path $source 'artifacts\dependencies' }
if (!(Test-Path -LiteralPath (Join-Path $DependencyPath 'YamlDotNet.dll'))) { & (Join-Path $PSScriptRoot 'Restore-ReviewDependencies.ps1') -Destination $DependencyPath | Out-Null }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$resourcePath = Join-Path $OutputPath 'NOVA VPN.g.resources'
$writer = New-Object System.Resources.ResourceWriter($resourcePath)
try {
    foreach ($asset in @('nova-mark.png','nova-backup.png','nova-v2.ico','nova-desktop-v3.ico')) {
        $stream = [IO.File]::OpenRead((Join-Path $source "Assets\$asset"))
        $writer.AddResource("assets/$asset", $stream, $true)
    }
    $writer.Generate()
} finally { $writer.Dispose() }
$manifest = if ($Release) { 'app.manifest' } else { 'app-test.manifest' }
$compilerArgs = @('/nologo','/target:winexe','/langversion:latest','/optimize+','/platform:anycpu',
    "/out:$OutputPath\NOVA VPN.exe", "/win32icon:$source\Assets\nova-desktop-v3.ico",
    "/win32manifest:$source\$manifest", "/resource:$resourcePath,NOVA VPN.g.resources")
foreach ($reference in @('Microsoft.CSharp','System','System.Core','System.IO.Compression','System.IO.Compression.FileSystem',
    'System.Drawing','System.Net.Http','System.Runtime.Serialization','System.Security','System.Web.Extensions','System.Windows.Forms','System.Xml','System.Xaml')) {
    $compilerArgs += "/reference:$framework\$reference.dll"
}
foreach ($reference in @('PresentationCore','PresentationFramework','WindowsBase')) { $compilerArgs += "/reference:$framework\WPF\$reference.dll" }
foreach ($asset in @('ne_110m_admin_0_countries.geojson','ne_50m_populated_places.geojson')) {
    $compilerArgs += "/resource:$source\NovaVpn\Assets\$asset,NovaVpn.Assets.$asset"
}
$compilerArgs += "/resource:$source\NovaVpn\Assets\vps-managed-agent.py,NovaVpn.Assets.vps-managed-agent.py"
[xml]$project = Get-Content -LiteralPath (Join-Path $source 'NOVA VPN.csproj') -Raw
foreach($item in $project.SelectNodes("//*[local-name()='Compile']")) { $compilerArgs += Join-Path $source ([string]$item.Include) }
$compilerArgs += "/reference:$source\libs\zxing.dll"
$compilerArgs += "/reference:$DependencyPath\YamlDotNet.dll"
& $CompilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Client compilation failed: $LASTEXITCODE" }
foreach ($file in @('zxing.dll','LICENSE-ZXing.txt')) { Copy-Item -LiteralPath "$source\libs\$file" -Destination $OutputPath -Force }
Copy-Item -LiteralPath (Join-Path $DependencyPath 'YamlDotNet.dll') -Destination $OutputPath -Force
Copy-Item -LiteralPath (Join-Path $source 'libs\LICENSE-YamlDotNet.txt') -Destination $OutputPath -Force
& $CompilerPath /nologo /target:exe /langversion:latest "/out:$OutputPath\NOVA Core Host.exe" "/reference:$OutputPath\NOVA VPN.exe" "$source\packaging\CoreHostEntry.cs"
if ($LASTEXITCODE -ne 0) { throw 'Core host compilation failed.' }
& $CompilerPath /nologo /target:exe /langversion:latest "/out:$OutputPath\NOVA Traffic Guard.exe" "/reference:$OutputPath\NOVA VPN.exe" "/reference:$framework\System.ServiceProcess.dll" "$source\packaging\TrafficGuardServiceEntry.cs"
if ($LASTEXITCODE -ne 0) { throw 'Traffic guard service compilation failed.' }
$recoverySources = @('TrafficGuardPolicy','TrafficGuardNative','TrafficGuardReadback','TrafficGuardRecovery','TrafficGuardMachineJournalSecurity','TrafficGuardServiceIdentity','TrafficGuardOwnerPolicy','PrivateRuntimeFiles') | ForEach-Object { Join-Path $source "NovaVpn\$_.cs" }
& $CompilerPath /nologo /target:exe /langversion:latest "/out:$OutputPath\NOVA Guard Recovery.exe" "/win32manifest:$source\$manifest" "/reference:$framework\System.Security.dll" "/reference:$framework\System.Web.Extensions.dll" @recoverySources "$source\packaging\GuardRecoveryEntry.cs"
if ($LASTEXITCODE -ne 0) { throw 'Standalone recovery compilation failed.' }
& $CompilerPath /nologo /target:exe /langversion:latest "/out:$OutputPath\NOVA Guard Setup.exe" "/win32manifest:$source\$manifest" "/reference:$framework\System.Security.dll" "/reference:$framework\System.Web.Extensions.dll" @recoverySources "$source\NovaVpn\TrafficGuardRegistration.cs" "$source\packaging\GuardSetupEntry.cs"
if ($LASTEXITCODE -ne 0) { throw 'Standalone guard setup compilation failed.' }
Write-Output "Built client, core host, inactive service and standalone guard recovery/setup: $OutputPath"

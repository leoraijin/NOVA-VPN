param([Parameter(Mandatory=$true)][string]$AssemblyPath,
 [string]$CompilerPath,
 [string[]]$Cases=@('MultiServerGamingTests','MultiServerRoutingHistoryTests','MultiExitHealthTests','MultiExitHealthDisplayTests','MultiServerSettingsTests','FullStateRestorePlanTests','ApplicationBandwidthPolicyTests','MultiServerEditorTests','RoutingApplyTransactionTests'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$assembly=(Resolve-Path -LiteralPath $AssemblyPath).Path
$out=Split-Path $assembly -Parent
$csc=$CompilerPath
if(!$csc){
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
 if(Test-Path -LiteralPath $vswhere){
  $msbuild=& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
  if($msbuild){$csc=Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe'}
 }
}
if(!$csc){$csc=Join-Path (Split-Path $repo -Parent) 'handoff-2026-10-28\toolchain\roslyn\csc.exe'}
if(!(Test-Path -LiteralPath $csc -PathType Leaf)){throw 'Pass -CompilerPath with a Roslyn csc.exe path.'}
$expected=@{RoutingCardsTests=36;MultiServerGamingTests=27;MultiServerRoutingHistoryTests=70;MultiExitHealthTests=58;MultiExitHealthDisplayTests=19;MultiServerSettingsTests=21;FullStateRestorePlanTests=44;ApplicationBandwidthPolicyTests=96;MultiServerEditorTests=107;RoutingApplyTransactionTests=27;MultiServerConfigTests=59;FakeIpNetworkPreflightTests=2015}
if(!$Cases.Count -or @($Cases|Select-Object -Unique).Count -ne $Cases.Count -or @($Cases|Where-Object{!$expected.ContainsKey($_)}).Count){throw 'Unknown or duplicate test selection'}
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@("/reference:$assembly")
foreach($ref in @('System','System.Core','System.Web.Extensions','System.Runtime.Serialization','System.Security','System.Drawing','System.Net.Http','System.Windows.Forms','System.Xaml','Microsoft.CSharp')){$refs+="/reference:$fx\$ref.dll"}
foreach($ref in @('WindowsBase','PresentationCore','PresentationFramework')){$refs+="/reference:$fx\WPF\$ref.dll"}
$results=@()
foreach($case in $Cases){
 $dir=Join-Path $out $case
 if(Test-Path -LiteralPath $dir){throw "Fresh test evidence directory required: $case"}
 & $csc /nologo /langversion:latest /platform:x64 "/out:$out\$case.exe" @refs "$repo\tests\$case.cs"
 if($LASTEXITCODE -ne 0){throw "Compile failed: $case"}
 New-Item -ItemType Directory -Path $dir|Out-Null
 & "$out\$case.exe" $dir *> "$dir\run.log"
 $code=$LASTEXITCODE
 $text=Get-Content -LiteralPath "$dir\run.log" -Raw
 $results+=@{name=$case;exit=$code;expectedChecks=$expected[$case];log="$case/run.log";logSha256=(Get-FileHash -LiteralPath "$dir\run.log").Hash.ToLowerInvariant()}
 $results|ConvertTo-Json -Depth 5|Set-Content -LiteralPath "$out\regression-runs.json" -Encoding utf8
 Write-Output "$case exit=$code";Get-Content -LiteralPath "$dir\run.log" -Tail 2
 if($code -ne 0 -or $text -notmatch ('\b'+$expected[$case]+'\b')){throw "Unexpected test result: $case"}
}

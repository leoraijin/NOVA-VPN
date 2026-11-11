param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'VERIFY.ps1')
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-kit.json') -Raw|ConvertFrom-Json
if($manifest.format -ne 'nova-core-source-kit-v1' -or $manifest.version -ne '1.13.14-nova-halfclose.2' -or $manifest.sourceDirectory -ne 'sing-box-25a600db24f7680ad9806ce5427bd0ab8afe1114'){throw 'Unsupported source kit'}
Assert-NovaFileInventory -Root $PSScriptRoot -Files $manifest.files -Ignore @('source-kit.json')
if(!$OutputDirectory){$OutputDirectory=Join-Path (Split-Path $PSScriptRoot) ('rebuilt-core-'+[Guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$sourcePrefix=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')+'\'
if($OutputDirectory.StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase) -or $OutputDirectory -eq [IO.Path]::GetFullPath($PSScriptRoot)){throw 'Build output must be outside the source kit'}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Build output already exists'}
New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop | Out-Null
Expand-Archive -LiteralPath (Join-Path $PSScriptRoot 'toolchain\go1.26.4.windows-amd64.zip') -DestinationPath (Join-Path $OutputDirectory 'toolchain')
$go=Join-Path $OutputDirectory 'toolchain\go\bin\go.exe'
if((& $go version) -ne 'go version go1.26.4 windows/amd64'){throw 'Unexpected bundled toolchain'}
$proxy=[Uri]::new(([IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'module-proxy'))+'\')).AbsoluteUri
$temp=Join-Path $OutputDirectory 'temp';New-Item -ItemType Directory -Path $temp|Out-Null
$settings=@{GOPATH=(Join-Path $OutputDirectory 'go-cache');GOCACHE=(Join-Path $OutputDirectory 'build-cache');GOTMPDIR=$temp;GOTOOLCHAIN='local';CGO_ENABLED='0';GOOS='windows';GOARCH='amd64';GOAMD64='v1';GOENV='off';GOWORK='off';GOFLAGS='';GOPROXY=$proxy;GOSUMDB='off';GOVCS='*:off';GOPRIVATE='';GONOPROXY='';GONOSUMDB=''};$old=@{}
foreach($name in $settings.Keys){$old[$name]=[Environment]::GetEnvironmentVariable($name,'Process');[Environment]::SetEnvironmentVariable($name,$settings[$name],'Process')}
Push-Location (Join-Path $PSScriptRoot $manifest.sourceDirectory)
try{
    & $go test -buildvcs=false -mod=readonly -count=1 -ldflags '-X internal/godebug.defaultGODEBUG=multipathtcp=0 -checklinkname=0' -tags $manifest.tags -run '^TestNovaTracked' ./route
    if($LASTEXITCODE -ne 0){throw 'Offline route tests failed'}
    $binary=Join-Path $OutputDirectory 'sing-box.exe'
    & $go build -buildvcs=false -mod=readonly -trimpath -tags $manifest.tags -ldflags ('-X internal/godebug.defaultGODEBUG=multipathtcp=0 -checklinkname=0 -X github.com/sagernet/sing-box/constant.Version='+$manifest.version) -o $binary ./cmd/sing-box
    if($LASTEXITCODE -ne 0){throw 'Offline core rebuild failed'}
    Assert-NovaFileInventory -Root $PSScriptRoot -Files $manifest.files -Ignore @('source-kit.json')
    $hash=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    if($hash -ne $manifest.expectedCoreSHA256){throw "Offline rebuild differs: $hash"}
    Write-Output "PASS offline rebuilt core matches $hash"
}finally{Pop-Location;foreach($name in $settings.Keys){[Environment]::SetEnvironmentVariable($name,$old[$name],'Process')}}

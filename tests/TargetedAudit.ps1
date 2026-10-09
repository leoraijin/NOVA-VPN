param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$WorkPath
)

$ErrorActionPreference = 'Stop'
[IO.Directory]::CreateDirectory($WorkPath) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)

function New-TypeInstance([string]$typeName) {
    return $assembly.CreateInstance("NovaVpn.$typeName")
}

$domainService = $assembly.GetType('NovaVpn.ZapretDomainService')
$inspector = $assembly.GetType('NovaVpn.RoutingInspector')

$wildcardState = New-TypeInstance 'AppState'
$wildcardState.Routing.DirectDomains.Add('*.example.com')
$added = $domainService.GetMethod('AddToVpnRoutesFromSource').Invoke($null, @($wildcardState, [string[]]@('api.example.com')))
$decision = $inspector.GetMethod('Explain').Invoke($null, @($wildcardState.Routing, 'api.example.com', ''))

$removalState = New-TypeInstance 'AppState'
$removalState.Routing.DirectDomains.Add('keep-direct.example')
$removalState.Routing.ProxyDomains.AddRange([string[]]@('remove.example', 'keep-proxy.example'))
$removed = $domainService.GetMethod('RemoveFromVpnRoutesFromSource').Invoke($null, @($removalState, [string[]]@('remove.example')))

$result = [ordered]@{
    date = (Get-Date).ToUniversalTime().ToString('o')
    assembly = $AssemblyPath
    wildcardConflict = [ordered]@{
        addedCount = [int]$added
        proxyDomains = @($wildcardState.Routing.ProxyDomains)
        directDomains = @($wildcardState.Routing.DirectDomains)
        route = [string]$decision.Route
        reason = [string]$decision.Reason
        finding = ([string]$decision.Route -eq 'direct')
    }
    removalIsolation = [ordered]@{
        removedCount = [int]$removed
        directDomains = @($removalState.Routing.DirectDomains)
        proxyDomains = @($removalState.Routing.ProxyDomains)
        directListUntouched = ($removalState.Routing.DirectDomains -contains 'keep-direct.example')
        unrelatedProxyPreserved = ($removalState.Routing.ProxyDomains -contains 'keep-proxy.example')
    }
}
$output = Join-Path $WorkPath 'targeted-audit.json'
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding UTF8
if ($result.wildcardConflict.finding) { throw 'Wildcard routing conflict remains: exact VPN rule was shadowed by a direct wildcard.' }
if (-not $result.removalIsolation.directListUntouched -or -not $result.removalIsolation.unrelatedProxyPreserved) { throw 'Removal isolation invariant failed.' }
Write-Output "Targeted audit written to $output"

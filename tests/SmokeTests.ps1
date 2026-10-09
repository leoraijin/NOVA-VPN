param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$CorePath,
    [Parameter(Mandatory = $true)][string]$WorkPath
)

$ErrorActionPreference = 'Stop'
[IO.Directory]::CreateDirectory($WorkPath) | Out-Null
$testAssemblyPath = Join-Path $WorkPath 'NOVA-VPN-Test-Assembly.dll'
Copy-Item -LiteralPath $AssemblyPath -Destination $testAssemblyPath -Force
$assembly = [Reflection.Assembly]::LoadFrom($testAssemblyPath)

function New-TypeInstance([string]$typeName) {
    return $assembly.CreateInstance("NovaVpn.$typeName")
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERT FAILED: $message" }
}

$profileSwitchHarness = @'
using System;
using System.Threading.Tasks;
using NovaVpn;

public static class NovaVpnProfileSwitchHarness
{
    private static AppState CreateState(out VpnProfile oldProfile, out VpnProfile newProfile)
    {
        AppState state = new AppState();
        oldProfile = new VpnProfile { Id = "old", Name = "Old" };
        newProfile = new VpnProfile { Id = "new", Name = "New" };
        state.Profiles.Add(oldProfile);
        state.Profiles.Add(newProfile);
        state.SelectedProfileId = oldProfile.Id;
        return state;
    }

    public static string SuccessfulSwitch()
    {
        VpnProfile oldProfile, newProfile;
        AppState state = CreateState(out oldProfile, out newProfile);
        int stopCount = 0;
        string started = "";
        Func<VpnProfile, Task> start = async profile => { await Task.Yield(); started = profile.Id; };
        ProfileSwitchResult result = ProfileSwitchService.SwitchAsync(state, newProfile, () => stopCount++, start).GetAwaiter().GetResult();
        return string.Join("|", new string[] { state.SelectedProfileId, started, stopCount.ToString(), result.TargetConnected.ToString(), result.PreviousRestored.ToString() });
    }

    public static string RestoresPreviousOnFailure()
    {
        VpnProfile oldProfile, newProfile;
        AppState state = CreateState(out oldProfile, out newProfile);
        int stopCount = 0;
        string started = "";
        Func<VpnProfile, Task> start = async profile =>
        {
            await Task.Yield();
            started = profile.Id;
            if (profile.Id == newProfile.Id) throw new InvalidOperationException("new config failed");
        };
        ProfileSwitchResult result = ProfileSwitchService.SwitchAsync(state, newProfile, () => stopCount++, start).GetAwaiter().GetResult();
        return string.Join("|", new string[] { state.SelectedProfileId, started, stopCount.ToString(), result.TargetConnected.ToString(), result.PreviousRestored.ToString(), (result.TargetError != null).ToString(), (result.RestoreError == null).ToString() });
    }

    public static string KeepsRequestedTargetIfRollbackFails()
    {
        VpnProfile oldProfile, newProfile;
        AppState state = CreateState(out oldProfile, out newProfile);
        int stopCount = 0;
        Func<VpnProfile, Task> start = async profile => { await Task.Yield(); throw new InvalidOperationException(profile.Id + " failed"); };
        ProfileSwitchResult result = ProfileSwitchService.SwitchAsync(state, newProfile, () => stopCount++, start).GetAwaiter().GetResult();
        return string.Join("|", new string[] { state.SelectedProfileId, stopCount.ToString(), result.TargetConnected.ToString(), result.PreviousRestored.ToString(), (result.TargetError != null).ToString(), (result.RestoreError != null).ToString() });
    }
}
'@
Add-Type -AssemblyName System.Runtime.Serialization
$profileSwitchReferences = @($testAssemblyPath, [System.Runtime.Serialization.DataContractAttribute].Assembly.Location, [System.Threading.Tasks.Task].Assembly.Location)
Add-Type -TypeDefinition $profileSwitchHarness -ReferencedAssemblies $profileSwitchReferences
$switchSuccess = [NovaVpnProfileSwitchHarness]::SuccessfulSwitch().Split('|')
Assert-True ($switchSuccess[0] -eq 'new' -and $switchSuccess[1] -eq 'new' -and $switchSuccess[2] -eq '1' -and $switchSuccess[3] -eq 'True' -and $switchSuccess[4] -eq 'False') 'profile switch must stop once and reconnect the newly selected config'
$switchRollback = [NovaVpnProfileSwitchHarness]::RestoresPreviousOnFailure().Split('|')
Assert-True ($switchRollback[0] -eq 'old' -and $switchRollback[1] -eq 'old' -and $switchRollback[2] -eq '2' -and $switchRollback[3] -eq 'False' -and $switchRollback[4] -eq 'True' -and $switchRollback[5] -eq 'True' -and $switchRollback[6] -eq 'True') 'a failed new config must reconnect the previous config and restore its selection'
$switchDoubleFailure = [NovaVpnProfileSwitchHarness]::KeepsRequestedTargetIfRollbackFails().Split('|')
Assert-True ($switchDoubleFailure[0] -eq 'new' -and $switchDoubleFailure[1] -eq '2' -and $switchDoubleFailure[2] -eq 'False' -and $switchDoubleFailure[3] -eq 'False' -and $switchDoubleFailure[4] -eq 'True' -and $switchDoubleFailure[5] -eq 'True') 'if both starts fail, selection and both errors must remain available for recovery'

$routing = New-TypeInstance 'RoutingSettings'
$routing.ProxyDomains.Add('*.example.com')
$routing.DirectDomains.Add('direct.test')
$routing.BlockDomains.Add('blocked.test')
$routing.AdvancedRules.Add('block|udp|443')
$inspector = $assembly.GetType('NovaVpn.RoutingInspector')
$proxyDecision = $inspector.GetMethod('Explain').Invoke($null, @($routing, 'https://api.example.com/path', ''))
$directDecision = $inspector.GetMethod('Explain').Invoke($null, @($routing, 'direct.test', ''))
$blockDecision = $inspector.GetMethod('Explain').Invoke($null, @($routing, 'blocked.test', ''))
Assert-True ($proxyDecision.Route -eq 'proxy') 'wildcard domain should use proxy'
Assert-True ($directDecision.Route -eq 'direct') 'direct domain should bypass proxy'
Assert-True ($blockDecision.Route -eq 'block') 'blocked domain should be rejected'

$transitionState = New-TypeInstance 'AppState'
$transitionState.Routing.ProxyDomains.Add('www.youtube.com')
$transitionState.Routing.ProxyDomains.Add('*.cdn.discordapp.com')
$transitionState.Routing.ProxyDomains.Add('api.example.com')
$transitionState.Routing.ProxyDomains.Add('cdn.api.example.com')
$transitionState.Routing.ProxyDomains.Add('*.example.com')
$transitionState.Routing.ProxyDomains.Add('*.unrelated.example.net')
$transitionState.Routing.DirectDomains.Add('existing.direct.test')
$transitionState.Routing.ProxyProcesses.Add('discord.exe')
$transitionState.Routing.ProxyProcesses.Add('browser.exe')
$transitionState.Routing.ProxyProcessPaths.Add('C:\Apps\Discord\discord.exe')
$transitionState.Routing.ProxyProcessPaths.Add('C:\Apps\Browser\browser.exe')
$transitionState.Routing.DirectProcessPaths.Add('C:\Apps\ExistingDirect\direct.exe')
$transitionState.Zapret.Mode = 'VPN+Zapret'
$transitionState.Zapret.StandardDomains.Add('api.example.com')
$transitionState.Zapret.StandardDomains.Add('video.example.net')
$transitionState.Zapret.StandardDomains.Add('youtube.com')
$transitionState.Zapret.StandardDomains.Add('discord.com')
$transitionState.Zapret.StandardDomains.Add('discord.gg')
$transitionService = $assembly.GetType('NovaVpn.ZapretRoutingTransitionService')
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($transitionState, $true)) | Out-Null
Assert-True ($transitionState.Zapret.RoutingChangesApplied) 'Zapret activation must persist a reversible routing transition'
Assert-True (-not $transitionState.Routing.ProxyDomains.Contains('www.youtube.com') -and -not $transitionState.Routing.ProxyDomains.Contains('*.cdn.discordapp.com')) 'VPN rules for Discord/YouTube domains must be removed while Zapret is active'
Assert-True ($transitionState.Routing.ProxyDomains.Contains('api.example.com') -and $transitionState.Routing.ProxyDomains.Contains('cdn.api.example.com') -and $transitionState.Routing.ProxyDomains.Contains('*.unrelated.example.net')) 'non-Discord/YouTube VPN domain routes must remain intact'
Assert-True ($transitionState.Routing.DirectDomains.Contains('*.youtube.com') -and $transitionState.Routing.DirectDomains.Contains('*.discord.com') -and $transitionState.Routing.DirectDomains.Contains('existing.direct.test') -and -not $transitionState.Routing.DirectDomains.Contains('*.video.example.net')) 'only Discord/YouTube domains must be inserted into direct routes'
Assert-True ($transitionState.Routing.ProxyProcessPaths.Contains('C:\Apps\Browser\browser.exe') -and $transitionState.Routing.DirectProcessPaths.Contains('C:\Apps\Discord\discord.exe') -and $transitionState.Routing.DirectProcessPaths.Contains('C:\Apps\ExistingDirect\direct.exe')) 'only Discord app paths move to Direct; browser routes remain unchanged'
Assert-True ($transitionState.Routing.ProxyProcesses.Contains('browser.exe') -and -not $transitionState.Routing.ProxyProcesses.Contains('discord.exe') -and $transitionState.Routing.DirectProcesses.Contains('discord.exe')) 'only Discord process rules move to Direct while browser process rules stay in VPN'
Assert-True ($transitionState.Routing.DirectProcesses.Contains('discordptb.exe') -and $transitionState.Routing.DirectProcesses.Contains('discordcanary.exe')) 'unlisted Discord variants must also reach Zapret directly'
$transitionInspector = $inspector.GetMethod('Explain')
$effectiveDecision = $transitionInspector.Invoke($null, @($transitionState.Routing, 'www.youtube.com', 'browser.exe'))
Assert-True ($effectiveDecision.Route -eq 'direct') 'YouTube domains must reach Zapret even when their browser is assigned to VPN'
$discordDecision = $transitionInspector.Invoke($null, @($transitionState.Routing, 'gateway.discord.gg', 'discord.exe'))
Assert-True ($discordDecision.Route -eq 'direct') 'Discord domains and app must reach Zapret'
$unrelatedDecision = $transitionInspector.Invoke($null, @($transitionState.Routing, 'other.example.com', 'browser.exe'))
Assert-True ($unrelatedDecision.Route -eq 'proxy') 'other browser traffic must retain its regular VPN route'
$overlay = $assembly.GetType('NovaVpn.RoutingOverlayService')
$effectiveRouting = $overlay.GetMethod('BuildEffective').Invoke($null, [object[]]@($transitionState, $true))
Assert-True ($effectiveRouting.ProxyDomains.Count -eq $transitionState.Routing.ProxyDomains.Count -and $effectiveRouting.ZapretDirectDomains.Contains('*.youtube.com')) 'the core receives narrow persisted Zapret exceptions with no broad overlay'
$directCount = $transitionState.Routing.DirectDomains.Count
$directProcessCount = $transitionState.Routing.DirectProcessPaths.Count
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($transitionState, $true)) | Out-Null
Assert-True ($transitionState.Routing.DirectDomains.Count -eq $directCount -and $transitionState.Routing.DirectProcessPaths.Count -eq $directProcessCount) 'repeated activation must not create duplicate direct entries'
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($transitionState, $false)) | Out-Null
Assert-True (-not $transitionState.Zapret.RoutingChangesApplied) 'disabling Zapret must clear the transition snapshot'
Assert-True ($transitionState.Routing.ProxyDomains.Contains('www.youtube.com') -and $transitionState.Routing.ProxyDomains.Contains('*.cdn.discordapp.com') -and $transitionState.Routing.ProxyDomains.Contains('api.example.com')) 'disabling Zapret must restore the original VPN domain routes'
Assert-True (-not $transitionState.Routing.DirectDomains.Contains('*.youtube.com') -and -not $transitionState.Routing.DirectDomains.Contains('*.discord.com') -and $transitionState.Routing.DirectDomains.Contains('existing.direct.test')) 'disabling Zapret must remove only its temporary Discord/YouTube routes'
Assert-True ($transitionState.Routing.ProxyProcesses.Contains('discord.exe') -and $transitionState.Routing.ProxyProcesses.Contains('browser.exe') -and $transitionState.Routing.ProxyProcessPaths.Contains('C:\Apps\Discord\discord.exe') -and $transitionState.Routing.ProxyProcessPaths.Contains('C:\Apps\Browser\browser.exe') -and $transitionState.Routing.DirectProcessPaths.Contains('C:\Apps\ExistingDirect\direct.exe')) 'disabling Zapret restores Discord VPN entries and preserves all other app routes'
Assert-True (-not $transitionState.Routing.DirectProcesses.Contains('discordptb.exe') -and -not $transitionState.Routing.DirectProcesses.Contains('discordcanary.exe')) 'Zapret-only Discord variants must be removed from Direct when Zapret stops'

$suffixState = New-TypeInstance 'AppState'
$suffixState.Routing.ProxyDomains.Add('api.youtube.com')
$suffixState.Routing.ProxyDomains.Add('*.youtube.com')
$suffixState.Routing.ProxyDomains.Add('*.cdn.youtube.com')
$suffixState.Routing.ProxyDomains.Add('*.com')
$suffixState.Routing.ProxyDomains.Add('*.unrelated.example.net')
$suffixState.Zapret.StandardDomains.Add('*.youtube.com')
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($suffixState, $true)) | Out-Null
Assert-True (-not $suffixState.Routing.ProxyDomains.Contains('api.youtube.com') -and -not $suffixState.Routing.ProxyDomains.Contains('*.youtube.com') -and -not $suffixState.Routing.ProxyDomains.Contains('*.cdn.youtube.com')) 'a targeted YouTube wildcard removes only same-scope/narrower VPN rules'
Assert-True ($suffixState.Routing.ProxyDomains.Contains('*.com') -and $suffixState.Routing.ProxyDomains.Contains('*.unrelated.example.net') -and $suffixState.Routing.DirectDomains.Contains('*.youtube.com')) 'a targeted wildcard preserves broad/unrelated VPN rules and inserts only its own direct exception'
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($suffixState, $false)) | Out-Null
Assert-True ($suffixState.Routing.ProxyDomains.Count -eq 5 -and $suffixState.Routing.DirectDomains.Count -eq 0) 'targeted wildcard routes must return exactly to their original lists when Zapret stops'

$legacyTransitionState = New-TypeInstance 'AppState'
$legacyTransitionState.Zapret.RoutingChangesApplied = $true
$legacyTransitionState.Zapret.StandardDomains.Add('youtube.com')
$legacyTransitionState.Zapret.StandardDomains.Add('discord.com')
$legacyTransitionState.Routing.ProxyDomains.Add('*.youtube.com')
$legacyTransitionState.Routing.ProxyDomains.Add('*.discord.com')
$legacyTransitionState.Routing.ProxyDomains.Add('*.example.org')
$legacyTransitionState.Routing.DirectDomains.Add('*.facebook.com')
$legacyTransitionState.Routing.DirectProcesses.Add('browser.exe')
$legacyTransitionState.Routing.DirectProcesses.Add('discord.exe')
$legacyTransitionState.Routing.ProxyDomains.Add('*.youtube.com')
$legacyTransitionState.Zapret.SavedProxyDomains.Add('*.youtube.com')
$legacyTransitionState.Zapret.SavedProxyDomains.Add('*.facebook.com')
$legacyTransitionState.Zapret.SavedProxyProcesses.Add('browser.exe')
$legacyTransitionState.Zapret.SavedProxyProcesses.Add('discord.exe')
$legacyTransitionState.Zapret.AddedDirectProcesses.Add('browser.exe')
$legacyTransitionState.Zapret.AddedDirectProcesses.Add('discord.exe')
$legacyTransitionState.Zapret.AddedDirectDomains.Add('*.facebook.com')
$legacyTransitionState.Zapret.AddedDirectDomains.Add('*.youtube.com')
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($legacyTransitionState, $true)) | Out-Null
Assert-True ($legacyTransitionState.Routing.ProxyProcesses.Contains('browser.exe') -and -not $legacyTransitionState.Routing.DirectProcesses.Contains('browser.exe') -and $legacyTransitionState.Routing.DirectProcesses.Contains('discord.exe')) 'upgrade must undo the old all-app direct transition while retaining Discord in Zapret'
Assert-True ($legacyTransitionState.Routing.DirectDomains.Contains('*.youtube.com') -and -not $legacyTransitionState.Routing.DirectDomains.Contains('*.facebook.com') -and $legacyTransitionState.Routing.ProxyDomains.Contains('*.facebook.com')) 'upgrade must restore non-target VPN domain rules removed by the old broad transition'

$globalZapretRouting = New-TypeInstance 'RoutingSettings'
$globalZapretRouting.Mode = 'Global'
$globalZapretRouting.ProxyProcesses.Add('discord.exe')
$globalZapretRouting.DirectDomains.Add('*.discord.com')
$globalZapretDecision = $inspector.GetMethod('Explain').Invoke($null, @($globalZapretRouting, 'cdn.discord.com', 'discord.exe'))
Assert-True ($globalZapretDecision.Route -eq 'direct') 'an explicit direct domain must apply in global mode and precede VPN application rules'

$specificityRouting = New-TypeInstance 'RoutingSettings'
$specificityRouting.DirectDomains.Add('*.example.com')
$specificityRouting.ProxyDomains.Add('api.example.com')
$specificityDecision = $inspector.GetMethod('Explain').Invoke($null, @($specificityRouting, 'api.example.com', ''))
Assert-True ($specificityDecision.Route -eq 'direct') 'strict direct site exception must override a nested VPN domain rule'

$sanitizer = $assembly.GetType('NovaVpn.LogSanitizer')
$sanitized = $sanitizer.GetMethod('Sanitize').Invoke($null, @('uuid=123e4567-e89b-12d3-a456-426614174000 password=secret {"password":"json-secret"}'))
Assert-True (-not $sanitized.Contains('123e4567-e89b-12d3-a456-426614174000')) 'UUID must be redacted'
Assert-True (-not $sanitized.Contains('secret')) 'password must be redacted'

$parser = $assembly.GetType('NovaVpn.LinkParser')
$profile = $parser.GetMethod('ParseSingle').Invoke($null, @('ss://YWVzLTEyOC1nY206dGVzdC1wYXNzd29yZA==@127.0.0.1:8388#Smoke'))
Assert-True ($profile.Protocol -eq 'shadowsocks') 'Shadowsocks parser protocol'
Assert-True ($profile.Port -eq 8388) 'Shadowsocks parser port'
$invalidJsonRejected = $false
try { $parser.GetMethod('ParseSingle').Invoke($null, @('{invalid')) | Out-Null } catch { $invalidJsonRejected = $true }
Assert-True $invalidJsonRejected 'malformed JSON must be rejected'
$multilineJson = @'
{
  "inbounds": [],
  "outbounds": []
}
'@
$importTask = $parser.GetMethod('ImportDetailedAsync').Invoke($null, @($multilineJson))
$multilineResult = $importTask.GetAwaiter().GetResult()
Assert-True ($multilineResult.Profiles.Count -eq 1 -and $multilineResult.Profiles[0].RawJson.Contains('outbounds')) 'multiline sing-box JSON must stay intact'
$tooManyInput = ((1..2001) | ForEach-Object { 'vless://123e4567-e89b-12d3-a456-426614174000@127.0.0.1:443?security=none&type=tcp#Profile' + $_ }) -join "`n"
$tooManyRejected = $false
try { $parser.GetMethod('ImportDetailedAsync').Invoke($null, @($tooManyInput)).GetAwaiter().GetResult() | Out-Null } catch { $tooManyRejected = $true }
Assert-True $tooManyRejected 'profile count limit must reject the 2001st profile'
$jsonA = $parser.GetMethod('ParseSingle').Invoke($null, @('{"inbounds":[],"outbounds":[]}'))
$jsonB = $parser.GetMethod('ParseSingle').Invoke($null, @('{"inbounds":[],"outbounds":[{"type":"direct"}]}'))
Assert-True ($jsonA.Source -ne $jsonB.Source) 'different sing-box JSON must have different identities'

$builder = $assembly.GetType('NovaVpn.ConfigBuilder')
$json = $builder.GetMethod('Build').Invoke($null, @($profile, $routing))
Assert-True ($json.Contains('"strict_route": true')) 'strict routing must be generated'
Assert-True ($json.Contains('"cache_capacity": 4096')) 'DNS cache must be generated'
Assert-True ($json.Contains('"network": "udp"')) 'advanced UDP rule must be generated'
$applicationRoutes = $assembly.GetType('NovaVpn.ApplicationRoutingService')
$applicationRouting = New-TypeInstance 'RoutingSettings'
$applicationRouting.Mode = 'Global'
$applicationPath = 'C:\Apps\Discord\discord.exe'
Assert-True ($applicationRoutes.GetMethod('GetRoute').Invoke($null, @($applicationRouting, $applicationPath)) -eq 'Auto') 'unconfigured app must inherit its mode'
$applicationRoutes.GetMethod('SetRoute').Invoke($null, @($applicationRouting, $applicationPath, 'Proxy')) | Out-Null
Assert-True ($applicationRouting.ProxyProcessPaths.Count -eq 1 -and $applicationRouting.DirectProcessPaths.Count -eq 0) 'VPN toggle must create one path rule'
$applicationRouting.DirectProcesses.Add('discord.exe')
$specificAppDecision = $inspector.GetMethod('Explain').Invoke($null, @($applicationRouting, 'unrelated.example', $applicationPath))
Assert-True ($specificAppDecision.Route -eq 'proxy') 'a specific app path must override the same executable name even in Global mode'
$applicationRoutes.GetMethod('SetRoute').Invoke($null, @($applicationRouting, $applicationPath, 'Direct')) | Out-Null
Assert-True ($applicationRouting.ProxyProcessPaths.Count -eq 0 -and $applicationRouting.DirectProcessPaths.Count -eq 1) 'direct toggle must remove the old VPN path rule'
$applicationRoutes.GetMethod('SetRoute').Invoke($null, @($applicationRouting, $applicationPath, 'Auto')) | Out-Null
Assert-True ($applicationRouting.ProxyProcessPaths.Count -eq 0 -and $applicationRouting.DirectProcessPaths.Count -eq 0) 'automatic state must remove both explicit path rules'
$applicationRoutes.GetMethod('SetRoute').Invoke($null, @($applicationRouting, $applicationPath, 'Proxy')) | Out-Null
$applicationRouting.DirectDomains.Add('*.discord.com')
$zapretAppDecision = $inspector.GetMethod('Explain').Invoke($null, @($applicationRouting, 'media.discord.com', $applicationPath))
Assert-True ($zapretAppDecision.Route -eq 'direct') 'Zapret site rule must override an explicit app VPN route'
$applicationJson = $builder.GetMethod('Build').Invoke($null, @($profile, $applicationRouting))
$applicationConfig = ConvertFrom-Json -InputObject $applicationJson
$appPathRule = @($applicationConfig.route.rules | Where-Object { $_.process_path -and @($_.process_path) -contains $applicationPath })
Assert-True ($appPathRule.Count -eq 1 -and $appPathRule[0].outbound -eq 'proxy') 'Global mode must emit selected application paths'
$appPathIndex = -1
$appNameIndex = -1
$appZapretIndex = -1
for ($i = 0; $i -lt $applicationConfig.route.rules.Count; $i++) {
    $rule = $applicationConfig.route.rules[$i]
    if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $appPathIndex = $i }
    if ($rule.process_name -and @($rule.process_name) -contains 'discord.exe') { $appNameIndex = $i }
    if ($rule.domain_suffix -and @($rule.domain_suffix) -contains '.discord.com') { $appZapretIndex = $i }
}
Assert-True ($appZapretIndex -ge 0 -and $appPathIndex -gt $appZapretIndex -and $appNameIndex -gt $appPathIndex) 'explicit direct-domain rules must precede application paths and process-name rules'
$applicationConfigPath = Join-Path $WorkPath 'application-routing.json'
[IO.File]::WriteAllText($applicationConfigPath, $applicationJson, [Text.UTF8Encoding]::new($false))
& $CorePath check -c $applicationConfigPath
if ($LASTEXITCODE -ne 0) { throw 'sing-box rejected generated application routing' }
$rawProfile = New-TypeInstance 'VpnProfile'
$rawProfile.Protocol = 'sing-box'
$rawProfile.RawJson = $json
$rawJson = $builder.GetMethod('Build').Invoke($null, @($rawProfile, $applicationRouting))
$rawConfig = ConvertFrom-Json -InputObject $rawJson
$rawAppRule = @($rawConfig.route.rules | Where-Object { $_.process_path -and @($_.process_path) -contains $applicationPath })
$rawZapretRule = @($rawConfig.route.rules | Where-Object { $_.domain_suffix -and @($_.domain_suffix) -contains '.discord.com' } | Select-Object -First 1)
Assert-True ($rawAppRule.Count -eq 1 -and $rawAppRule[0].outbound -eq 'proxy' -and $rawZapretRule.Count -eq 1 -and $rawZapretRule[0].outbound -in @('direct','nova-strict-direct')) 'imported sing-box JSON must receive app and Zapret rules'
$rawConfigPath = Join-Path $WorkPath 'imported-application-routing.json'
[IO.File]::WriteAllText($rawConfigPath, $rawJson, [Text.UTF8Encoding]::new($false))
& $CorePath check -c $rawConfigPath
if ($LASTEXITCODE -ne 0) { throw 'sing-box rejected imported application routing' }
$rawZapretRouting = New-TypeInstance 'RoutingSettings'
$rawZapretRouting.ProxyProcesses.Add('browser.exe')
$rawZapretRouting.ZapretDirectDomains.Add('*.youtube.com')
$rawZapretJson = $builder.GetMethod('Build').Invoke($null, @($rawProfile, $rawZapretRouting))
$rawZapretConfig = ConvertFrom-Json -InputObject $rawZapretJson
$rawYoutubeIndex = -1
$rawBrowserIndex = -1
for ($i = 0; $i -lt $rawZapretConfig.route.rules.Count; $i++) {
	$rule = $rawZapretConfig.route.rules[$i]
	if ($rule.domain_suffix -and @($rule.domain_suffix) -contains '.youtube.com') { $rawYoutubeIndex = $i }
	if ($rule.process_name -and @($rule.process_name) -contains 'browser.exe') { $rawBrowserIndex = $i }
}
Assert-True ($rawYoutubeIndex -ge 0 -and $rawBrowserIndex -gt $rawYoutubeIndex -and $rawZapretConfig.route.rules[$rawYoutubeIndex].outbound -eq 'direct') 'imported sing-box configs must direct only YouTube before the browser VPN rule'
$rawProfile.RawJson = '{"inbounds":[],"outbounds":[{"type":"direct","tag":"direct"},{"type":"socks","tag":"proxy","server":"127.0.0.1","server_port":1080}]}'
$missingTunRejected = $false
try { $builder.GetMethod('Build').Invoke($null, @($rawProfile, $applicationRouting)) | Out-Null } catch { $missingTunRejected = $true }
Assert-True $missingTunRejected 'imported JSON without TUN must report that application routing cannot be applied'
$catalog = $assembly.GetType('NovaVpn.ApplicationCatalog')
$manualEntry = $catalog.GetMethod('FromFile').Invoke($null, @([IO.Path]::GetFullPath($CorePath)))
Assert-True ($manualEntry.ExecutablePath.EndsWith('sing-box.exe') -and $manualEntry.Source.Length -gt 0) 'manual app picker must accept a real executable'
$savedAppPath = [string[]]@('C:\Apps\Sample\sample.exe')
$discovered = $catalog.GetMethod('Discover').Invoke($null, [object[]](,$savedAppPath))
Assert-True (@($discovered | Where-Object { $_.ExecutablePath -eq $savedAppPath[0] }).Count -eq 1) 'saved application must remain visible even when it is no longer installed'
$effectiveJson = $builder.GetMethod('Build').Invoke($null, @($profile, $effectiveRouting))
$effectiveConfig = ConvertFrom-Json -InputObject $effectiveJson
$zapretDirectRule = @($effectiveConfig.route.rules | Where-Object {
	$_.domain_suffix -and @($_.domain_suffix) -contains '.youtube.com'
} | Select-Object -First 1)
Assert-True ($zapretDirectRule.Count -eq 1 -and $zapretDirectRule[0].outbound -in @('direct','nova-strict-direct')) 'generated sing-box config must emit the Zapret-only YouTube domain as direct'
$globalJson = $builder.GetMethod('Build').Invoke($null, @($profile, $globalZapretRouting))
$globalConfig = ConvertFrom-Json -InputObject $globalJson
$globalZapretRule = @($globalConfig.route.rules | Where-Object {
	$_.domain_suffix -and @($_.domain_suffix) -contains '.discord.com'
} | Select-Object -First 1)
Assert-True ($globalZapretRule.Count -eq 1 -and $globalZapretRule[0].outbound -in @('direct','nova-strict-direct')) 'global VPN config must preserve explicitly direct domains'
$discordProcessRuleIndex = -1
$zapretDirectRuleIndex = -1
for ($i = 0; $i -lt $effectiveConfig.route.rules.Count; $i++) {
	$rule = $effectiveConfig.route.rules[$i]
	if ($rule.process_name -and @($rule.process_name) -contains 'discord.exe') { $discordProcessRuleIndex = $i }
	if ($zapretDirectRuleIndex -lt 0 -and $rule.domain_suffix -and @($rule.domain_suffix) -contains '.youtube.com') { $zapretDirectRuleIndex = $i }
}
$discordProcessRule = @($effectiveConfig.route.rules | Where-Object { $_.process_name -and @($_.process_name) -contains 'discord.exe' } | Select-Object -First 1)
$discordPathRule = @($effectiveConfig.route.rules | Where-Object { $_.process_path -and @($_.process_path) -contains $applicationPath } | Select-Object -First 1)
$browserProcessRuleIndex = -1
for ($i = 0; $i -lt $effectiveConfig.route.rules.Count; $i++) {
	$rule = $effectiveConfig.route.rules[$i]
	if ($rule.process_name -and @($rule.process_name) -contains 'browser.exe') { $browserProcessRuleIndex = $i }
}
Assert-True ($zapretDirectRuleIndex -ge 0 -and $discordProcessRule.Count -eq 1 -and $discordProcessRule[0].outbound -eq 'direct' -and $discordPathRule.Count -eq 1 -and $discordPathRule[0].outbound -eq 'direct' -and $browserProcessRuleIndex -gt $zapretDirectRuleIndex) ("only Discord and YouTube get Zapret routing ahead of unaffected VPN-routed browser traffic (youtube=$zapretDirectRuleIndex, discordProc=$($discordProcessRule.Count), discordPath=$($discordPathRule.Count), browser=$browserProcessRuleIndex)")
$effectiveConfigPath = Join-Path $WorkPath 'zapret-direct-routing-config.json'
[IO.File]::WriteAllText($effectiveConfigPath, $effectiveJson, [Text.UTF8Encoding]::new($false))
& $CorePath check -c $effectiveConfigPath
if ($LASTEXITCODE -ne 0) { throw "sing-box rejected the direct-routing config: $LASTEXITCODE" }
$orderedRouting = New-TypeInstance 'RoutingSettings'
$orderedRouting.ProxyDomains.Add('*.example.com')
$orderedRouting.DirectDomains.Add('api.example.com')
$orderedJson = $builder.GetMethod('Build').Invoke($null, @($profile, $orderedRouting))
Assert-True ($orderedJson.IndexOf('"api.example.com"') -lt $orderedJson.IndexOf('".example.com"')) 'specific domain rules must precede broader suffix rules in sing-box config'

# Site-specific rules must outrank browser-wide routes, for either destination.
$sitePriority = New-TypeInstance 'RoutingSettings'
$sitePriority.Mode = 'Smart'
$sitePriority.ProxyDomains.Add('stream.example.test')
$sitePriority.DirectDomains.Add('video.example.test')
$sitePriority.DirectProcessPaths.Add($applicationPath)
$sitePriority.ProxyProcesses.Add('browser.exe')
$priorityDecision = $inspector.GetMethod('Explain').Invoke($null, @($sitePriority, 'stream.example.test', $applicationPath))
$directDecision = $inspector.GetMethod('Explain').Invoke($null, @($sitePriority, 'video.example.test', $applicationPath))
Assert-True ($priorityDecision.Route -eq 'proxy' -and $directDecision.Route -eq 'direct') 'site rules must override opposing browser path/name routes in the routing inspector'
$priorityConfig = ConvertFrom-Json -InputObject ($builder.GetMethod('Build').Invoke($null, @($profile, $sitePriority)))
$streamIndex = -1
$videoIndex = -1
$browserPathIndex = -1
$browserNameIndex = -1
for ($i = 0; $i -lt $priorityConfig.route.rules.Count; $i++) {
	$rule = $priorityConfig.route.rules[$i]
	if ($rule.domain -and @($rule.domain) -contains 'stream.example.test') { $streamIndex = $i }
	if ($rule.domain -and @($rule.domain) -contains 'video.example.test') { $videoIndex = $i }
	if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $browserPathIndex = $i }
	if ($rule.process_name -and @($rule.process_name) -contains 'browser.exe') { $browserNameIndex = $i }
}
Assert-True ($streamIndex -ge 0 -and $videoIndex -gt $streamIndex -and $browserPathIndex -gt $videoIndex -and $browserNameIndex -gt $browserPathIndex) 'generated site routes must precede browser path and executable-name routes'
$sitePriority.Mode = 'Global'
$globalPriorityDecision = $inspector.GetMethod('Explain').Invoke($null, @($sitePriority, 'stream.example.test', $applicationPath))
$globalPriorityConfig = ConvertFrom-Json -InputObject ($builder.GetMethod('Build').Invoke($null, @($profile, $sitePriority)))
$globalStreamIndex = -1
$globalBrowserPathIndex = -1
for ($i = 0; $i -lt $globalPriorityConfig.route.rules.Count; $i++) {
	$rule = $globalPriorityConfig.route.rules[$i]
	if ($rule.domain -and @($rule.domain) -contains 'stream.example.test') { $globalStreamIndex = $i }
	if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $globalBrowserPathIndex = $i }
}
Assert-True ($globalPriorityDecision.Route -eq 'proxy' -and $globalStreamIndex -ge 0 -and $globalBrowserPathIndex -gt $globalStreamIndex) 'explicit VPN site routes must also beat a directly-routed browser in Global mode'
$sitePriority.Mode = 'Smart'
$priorityRawProfile = New-TypeInstance 'VpnProfile'
$priorityRawProfile.Protocol = 'sing-box'
$priorityRawProfile.RawJson = '{"inbounds":[{"type":"tun"}],"outbounds":[{"type":"socks","tag":"proxy","server":"127.0.0.1","server_port":1080},{"type":"direct","tag":"direct"}],"route":{"rules":[{"process_path":["' + $applicationPath.Replace('\','\\') + '"],"action":"route","outbound":"direct"}],"final":"proxy"}}'
$priorityRawConfig = ConvertFrom-Json -InputObject ($builder.GetMethod('Build').Invoke($null, @($priorityRawProfile, $sitePriority)))
$rawStreamIndex = -1
$rawBrowserPathIndex = -1
for ($i = 0; $i -lt $priorityRawConfig.route.rules.Count; $i++) {
	$rule = $priorityRawConfig.route.rules[$i]
	if ($rule.domain -and @($rule.domain) -contains 'stream.example.test') { $rawStreamIndex = $i }
	if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $rawBrowserPathIndex = $i }
}
Assert-True ($rawStreamIndex -ge 0 -and $rawBrowserPathIndex -gt $rawStreamIndex) 'site routes must be inserted before existing browser routes in imported sing-box configs'
$sitePriority.Mode = 'Global'
$globalRawConfig = ConvertFrom-Json -InputObject ($builder.GetMethod('Build').Invoke($null, @($priorityRawProfile, $sitePriority)))
$globalRawStreamIndex = -1
$globalRawBrowserPathIndex = -1
for ($i = 0; $i -lt $globalRawConfig.route.rules.Count; $i++) {
	$rule = $globalRawConfig.route.rules[$i]
	if ($rule.domain -and @($rule.domain) -contains 'stream.example.test') { $globalRawStreamIndex = $i }
	if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $globalRawBrowserPathIndex = $i }
}
Assert-True ($globalRawStreamIndex -ge 0 -and $globalRawBrowserPathIndex -gt $globalRawStreamIndex) 'Global mode site overrides must precede existing browser routes in imported sing-box configs'

$sitePriority = New-TypeInstance 'RoutingSettings'
$sitePriority.Mode = 'Smart'
$sitePriority.ProxyDomains.Add('stream.example.test')
$sitePriority.DirectDomains.Add('video.example.test')
$sitePriority.DirectProcessPaths.Add($applicationPath)
$sitePriority.ProxyProcesses.Add('browser.exe')
$priorityDecision = $inspector.GetMethod('Explain').Invoke($null, @($sitePriority, 'stream.example.test', $applicationPath))
$directDecision = $inspector.GetMethod('Explain').Invoke($null, @($sitePriority, 'video.example.test', $applicationPath))
Assert-True ($priorityDecision.Route -eq 'proxy' -and $directDecision.Route -eq 'direct') 'site rules must override opposing browser path/name routes in the routing inspector'
$priorityConfig = ConvertFrom-Json -InputObject ($builder.GetMethod('Build').Invoke($null, @($profile, $sitePriority)))
$streamIndex = -1
$videoIndex = -1
$browserPathIndex = -1
$browserNameIndex = -1
for ($i = 0; $i -lt $priorityConfig.route.rules.Count; $i++) {
	$rule = $priorityConfig.route.rules[$i]
	if ($rule.domain -and @($rule.domain) -contains 'stream.example.test') { $streamIndex = $i }
	if ($rule.domain -and @($rule.domain) -contains 'video.example.test') { $videoIndex = $i }
	if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $browserPathIndex = $i }
	if ($rule.process_name -and @($rule.process_name) -contains 'browser.exe') { $browserNameIndex = $i }
}
Assert-True ($streamIndex -ge 0 -and $videoIndex -gt $streamIndex -and $browserPathIndex -gt $videoIndex -and $browserNameIndex -gt $browserPathIndex) 'generated site routes must precede browser path and executable-name routes'
$priorityRawProfile = New-TypeInstance 'VpnProfile'
$priorityRawProfile.Protocol = 'sing-box'
$priorityRawProfile.RawJson = '{"inbounds":[{"type":"tun"}],"outbounds":[{"type":"socks","tag":"proxy","server":"127.0.0.1","server_port":1080},{"type":"direct","tag":"direct"}],"route":{"rules":[{"process_path":["' + $applicationPath.Replace('\','\\') + '"],"action":"route","outbound":"direct"}],"final":"proxy"}}'
$priorityRawConfig = ConvertFrom-Json -InputObject ($builder.GetMethod('Build').Invoke($null, @($priorityRawProfile, $sitePriority)))
$rawStreamIndex = -1
$rawBrowserPathIndex = -1
for ($i = 0; $i -lt $priorityRawConfig.route.rules.Count; $i++) {
	$rule = $priorityRawConfig.route.rules[$i]
	if ($rule.domain -and @($rule.domain) -contains 'stream.example.test') { $rawStreamIndex = $i }
	if ($rule.process_path -and @($rule.process_path) -contains $applicationPath) { $rawBrowserPathIndex = $i }
}
Assert-True ($rawStreamIndex -ge 0 -and $rawBrowserPathIndex -gt $rawStreamIndex) 'site routes must be inserted before existing browser routes in imported sing-box configs'
$configPath = Join-Path $WorkPath 'smoke-config.json'
[IO.File]::WriteAllText($configPath, $json, [Text.UTF8Encoding]::new($false))
& $CorePath check -c $configPath
if ($LASTEXITCODE -ne 0) { throw "sing-box rejected generated config: $LASTEXITCODE" }

$vmessJson = '{"v":"2","ps":"VMess Smoke","add":"127.0.0.1","port":"443","id":"123e4567-e89b-12d3-a456-426614174000","scy":"auto","net":"tcp","tls":"none"}'
$vmess = 'vmess://' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($vmessJson))
$links = @(
    'vless://123e4567-e89b-12d3-a456-426614174000@127.0.0.1:443?security=none&type=tcp#VLESS',
    'trojan://test-password@127.0.0.1:443?security=tls&sni=example.com#Trojan',
    'hysteria2://test-password@127.0.0.1:443?sni=example.com#Hysteria2',
    'tuic://123e4567-e89b-12d3-a456-426614174000:test-password@127.0.0.1:443?sni=example.com#TUIC',
    $vmess
)
$index = 0
foreach ($link in $links) {
    $candidate = $parser.GetMethod('ParseSingle').Invoke($null, @($link))
    $candidateJson = $builder.GetMethod('Build').Invoke($null, @($candidate, $routing))
    $candidatePath = Join-Path $WorkPath ("smoke-protocol-{0}.json" -f $index)
    [IO.File]::WriteAllText($candidatePath, $candidateJson, [Text.UTF8Encoding]::new($false))
    & $CorePath check -c $candidatePath
    if ($LASTEXITCODE -ne 0) { throw "sing-box rejected protocol $($candidate.Protocol)" }
    $index++
}

$importer = $assembly.GetType('NovaVpn.RoutingListImporter')
$lines = [string[]]@('0.0.0.0 ads.example.com', 'https://www.example.org/path', '# comment')
$domains = $importer.GetMethod('Parse').Invoke($null, [object[]]@(,$lines))
Assert-True ($domains.Contains('ads.example.com')) 'hosts file import'
Assert-True ($domains.Contains('www.example.org')) 'URL normalization'

$visual = New-TypeInstance 'VisualPreferences'
$themeType = $assembly.GetType('NovaVpn.ThemePalette')
$createTheme = $themeType.GetMethod('Create')
$defaultTheme = $createTheme.Invoke($null, @($visual))
$visual.ThemeName = 'Light'
$legacyTheme = $createTheme.Invoke($null, @($visual))
Assert-True ($defaultTheme.IsLight -and $defaultTheme.Name -eq 'Windows 11 Light') 'default visual theme should be Windows 11 Light'
Assert-True ($legacyTheme.IsLight -and $legacyTheme.Name -eq 'Windows 11 Light') 'legacy light theme names should migrate to Windows 11 Light'
$themeNames = $themeType.GetField('ThemeNames', [Reflection.BindingFlags]'Public,Static').GetValue($null)
$glassName = $themeType.GetField('IosLiquidGlass', [Reflection.BindingFlags]'Public,Static').GetRawConstantValue()
Assert-True ($themeNames.Count -eq 5 -and $themeNames -contains $glassName -and $themeNames -contains 'Amber Glass' -and $themeNames -contains 'One UI 8.0' -and $themeNames -contains 'Windows 11 Light' -and $themeNames -contains 'Windows 11 Dark') 'all five visual profiles should be selectable'
foreach ($themeName in $themeNames) {
    $profile = New-TypeInstance 'VisualPreferences'
    $profile.ThemeName = $themeName
    $profilePalette = $createTheme.Invoke($null, @($profile))
    Assert-True ($profilePalette.Name -eq $themeName) "theme palette should resolve $themeName"
    Assert-True (($themeName -eq 'Windows 11 Dark') -eq (-not $profilePalette.IsLight)) "light/dark classification should match $themeName"
}
$visual.ThemeName = 'Windows 11 Light'
$lightStock = $createTheme.Invoke($null, @($visual))
$visual.AccentHex = '#753EAF'
$legacyCustomPalette = $createTheme.Invoke($null, @($visual))
Assert-True ($legacyCustomPalette.Accent -eq $lightStock.Accent) 'legacy custom accent values must not alter a stock theme palette'
$visual.ThemeName = 'Windows 11 Dark'
$darkStock = $createTheme.Invoke($null, @($visual))
$visual.AccentHex = '#28713F'
$darkLegacyCustom = $createTheme.Invoke($null, @($visual))
Assert-True ($darkLegacyCustom.Accent -eq $darkStock.Accent) 'legacy custom accents must be ignored consistently in the dark profile'

$state = New-TypeInstance 'AppState'
Assert-True ($state.SchemaVersion -ge 5) 'visual settings schema version'
Assert-True ($null -ne $state.Visual) 'visual preferences should be initialized'
Assert-True ($state.SchemaVersion -ge 6) 'Zapret settings schema version'
Assert-True ($null -ne $state.Zapret) 'Zapret settings should be initialized'
Assert-True ($state.Zapret.RepositoryOwner -eq 'Flowseal' -and $state.Zapret.RepositoryName -eq 'zapret-discord-youtube') 'Zapret GitHub repository should be configured'
$appStateType = $assembly.GetType('NovaVpn.AppState')
Assert-True ($null -ne $appStateType.GetField('ShowTrainingOnStartup') -and $null -ne $appStateType.GetField('ShowTrainingHints')) 'training preferences should be persisted'
$mainWindowType = $assembly.GetType('NovaVpn.MainWindow')
Assert-True ($null -ne $mainWindowType.GetMethod('ShowPageForSnapshot')) 'main window navigation should be available'
Assert-True ($null -ne $mainWindowType.GetMethod('BuildLearningPage', [Reflection.BindingFlags] 'Instance,NonPublic')) 'learning tab should be implemented'

$state.Zapret.StandardDomains.Add('*.example-zapret.test')
$domainService = $assembly.GetType('NovaVpn.ZapretDomainService')
$added = $domainService.GetMethod('AddToVpnExceptions').Invoke($null, @($state))
Assert-True ($added -eq 1 -and $state.Routing.ProxyDomains.Contains('*.example-zapret.test') -and -not $state.Routing.DirectDomains.Contains('*.example-zapret.test')) 'Zapret domains should be routed through VPN'
$removed = $domainService.GetMethod('RemoveFromVpnExceptions').Invoke($null, @($state))
Assert-True ($removed -eq 1 -and -not $state.Routing.ProxyDomains.Contains('*.example-zapret.test')) 'Zapret domains should be removed from VPN routing'
$source = New-Object 'System.Collections.Generic.List[string]'
$source.Add('*.source-only.test')
$state.Routing.DirectDomains.Add('*.source-only.test')
$beforeZapret = @($state.Zapret.StandardDomains)
$addFromSource = $domainService.GetMethod('AddToVpnRoutesFromSource').Invoke($null, @($state, [string[]]$source.ToArray()))
Assert-True ($addFromSource -eq 1 -and $state.Routing.ProxyDomains.Contains('*.source-only.test') -and -not $state.Routing.DirectDomains.Contains('*.source-only.test')) 'source domains should be forced through VPN without changing Zapret lists'
$removeFromSource = $domainService.GetMethod('RemoveFromVpnRoutesFromSource').Invoke($null, @($state, [string[]]$source.ToArray()))
Assert-True ($removeFromSource -eq 1 -and -not $state.Routing.ProxyDomains.Contains('*.source-only.test')) 'source domains should be removed only from VPN routing'
Assert-True (@($state.Zapret.StandardDomains) -join ',' -eq ($beforeZapret -join ',')) 'Zapret standard domain list must remain unchanged'
$portable = $assembly.GetType('NovaVpn.PortableConfigService')
$state.Visual.ThemeName = 'Windows 11 Light'
$state.Visual.AccentHex = '#593F9D'
$state.Visual.StoreActiveThemeSettings()
$state.Visual.LoadThemeSettings('Windows 11 Dark')
$state.Visual.AccentHex = '#31704A'
$state.Visual.StoreActiveThemeSettings()
$state.Visual.LoadThemeSettings('Windows 11 Light')
$state.Routing.ProxyProcessPaths.Add($applicationPath)
$state.Zapret.StandardDomains.Add('persisted-zapret.test')
$transitionService.GetMethod('SetActive').Invoke($null, [object[]]@($state, $true)) | Out-Null
$portableRoundTripPath = Join-Path $WorkPath 'roundtrip.nova-config'
$portable.GetMethod('Export').Invoke($null, [object[]]@($state, [string]$portableRoundTripPath)) | Out-Null
$portableRoundTrip = $portable.GetMethod('Import').Invoke($null, [object[]]@([string]$portableRoundTripPath))
Assert-True ($portableRoundTrip.SchemaVersion -ge 7 -and $portableRoundTrip.Routing.StrictRoute) 'portable config roundtrip must preserve routing state'
Assert-True ($portableRoundTrip.Zapret.RoutingChangesApplied -and $portableRoundTrip.Zapret.SavedProxyProcessPaths.Contains($applicationPath)) 'full portable config must preserve the reversible Zapret routing snapshot'
Assert-True ($portableRoundTrip.Visual.ThemeName -eq 'Windows 11 Light' -and $portableRoundTrip.Visual.ThemeOverrides['Windows 11 Dark'].AccentHex -eq '') 'portable config should preserve theme profiles but discard unsupported custom-color overrides'
$settingsOnlyPath = Join-Path $WorkPath 'settings-only.nova-settings'
$portable.GetMethod('ExportSettingsOnly').Invoke($null, [object[]]@($state, [string]$settingsOnlyPath)) | Out-Null
$settingsOnly = $portable.GetMethod('ImportSettingsOnly').Invoke($null, [object[]]@([string]$settingsOnlyPath))
Assert-True ($settingsOnly.Routing.ProxyProcessPaths.Count -eq 0) 'settings-only export must omit machine-specific application paths'
Assert-True (-not $settingsOnly.Zapret.RoutingChangesApplied -and $settingsOnly.Zapret.SavedProxyProcessPaths.Count -eq 0 -and $settingsOnly.Zapret.Mode -eq 'VPN') 'settings-only export must not leak or retain machine-specific transition snapshots'
Assert-True ($settingsOnly.Visual.ThemeOverrides['Windows 11 Light'].AccentHex -eq '' -and $settingsOnly.Visual.ThemeOverrides['Windows 11 Dark'].AccentHex -eq '') 'settings-only export should retain stock profiles without unsupported custom-color overrides'
$invalidPortablePath = Join-Path $WorkPath 'invalid-portable.nova-config'
[IO.File]::WriteAllText($invalidPortablePath, '{"unrelated":true}')
$invalidPortableRejected = $false
try { $portable.GetMethod('Import').Invoke($null, @($invalidPortablePath)) | Out-Null } catch { $invalidPortableRejected = $true }
Assert-True $invalidPortableRejected 'unrelated JSON must not be accepted as portable state'
$mergeState = New-TypeInstance 'AppState'
$manual = New-TypeInstance 'VpnProfile'
$manual.Name = 'Manual'
$manual.Protocol = 'vless'
$manual.Server = 'same.example'
$manual.Port = 443
$manual.UserId = 'manual-user'
$manual.Source = 'manual-source'
$mergeState.Profiles.Add($manual)
$incoming = New-TypeInstance 'VpnProfile'
$incoming.Name = 'Manual'
$incoming.Protocol = 'vless'
$incoming.Server = 'same.example'
$incoming.Port = 443
$incoming.UserId = 'subscription-user'
$incoming.Source = 'subscription-source'
$incoming.SubscriptionUrl = 'https://subscription.example/list'
$incomingList = [System.Collections.Generic.List[NovaVpn.VpnProfile]]::new()
$incomingList.Add($incoming)
$importResultType = $assembly.GetType('NovaVpn.ImportResult')
$importCtor = $importResultType.GetConstructor(@([System.Collections.Generic.List[NovaVpn.VpnProfile]], [string]))
$subscriptionImport = $importCtor.Invoke([object[]]@($incomingList, 'https://subscription.example/list'))
$subscriptionManager = $assembly.GetType('NovaVpn.SubscriptionManager')
$subscriptionManager.GetMethod('MergeImported').Invoke($null, @($mergeState, $subscriptionImport)) | Out-Null
Assert-True ($mergeState.Profiles.Count -eq 2 -and $mergeState.Profiles.Contains($manual)) 'manual profile must survive subscription merge'
$zapretManager = New-TypeInstance 'ZapretManager'
$slowFavorite = New-TypeInstance 'VpnProfile'
$slowFavorite.LastLatency = 300
$slowFavorite.AverageLatency = 300
$slowFavorite.IsFavorite = $true
$fastServer = New-TypeInstance 'VpnProfile'
$fastServer.LastLatency = 20
$fastServer.AverageLatency = 20
$healthService = $assembly.GetType('NovaVpn.HealthCheckService')
$bestInput = [System.Collections.Generic.List[NovaVpn.VpnProfile]]::new()
$bestInput.Add($slowFavorite)
$bestInput.Add($fastServer)
$bestArgs = New-Object object[] 1
$bestArgs[0] = $bestInput
$best = $healthService.GetMethod('BestAvailable').Invoke($null, $bestArgs)
Assert-True ($best.Id -eq $fastServer.Id) 'best config should prioritize lower latency over favorite flag'
Assert-True ($zapretManager.IsUpdateAvailable('1.10.0', '1.9.9b')) 'newer Zapret release should be detected'
Assert-True (-not $zapretManager.IsUpdateAvailable('1.9.9b', '1.10.0')) 'older Zapret release should not replace a newer install'
$bundleRoot = Join-Path $WorkPath 'zapret-bundle'
$bundleBin = Join-Path $bundleRoot 'bundle\bin'
[IO.Directory]::CreateDirectory($bundleBin) | Out-Null
[IO.File]::WriteAllText((Join-Path $bundleBin 'winws.exe'), 'marker')
[IO.File]::WriteAllText((Join-Path $bundleRoot 'bundle\general.bat'), 'echo test')
$findRoot = $assembly.GetType('NovaVpn.ZapretManager').GetMethod('FindBundleRoot', [Reflection.BindingFlags] 'NonPublic,Static')
$resolvedRoot = $findRoot.Invoke($null, [object[]]@([string]$bundleRoot))
Assert-True (([string]$resolvedRoot).TrimEnd('\').EndsWith('\bundle', [StringComparison]::OrdinalIgnoreCase)) 'Zapret update should keep batch scripts in bundle root'

# Disconnect writes connection history. Verify legacy boundary dates cannot
# break the AppState JSON path used by RecordHistory -> StateStore.Save.
$dateProfile = New-TypeInstance 'VpnProfile'
$dateProfile.AddedAt = [DateTime]::MaxValue
$dateProfile.LastCheckedUtc = [DateTime]::MinValue
$dateProfile.SubscriptionUpdatedUtc = [DateTime]::MaxValue
$state.Profiles.Add($dateProfile)
$dateHistory = New-TypeInstance 'ConnectionHistoryItem'
$dateHistory.TimestampUtc = [DateTime]::MaxValue
$state.ConnectionHistory.Add($dateHistory)
$state.LastSubscriptionUpdateUtc = [DateTime]::MaxValue
$state.PauseUntilUtc = [DateTime]::MinValue
$stateStore = $assembly.GetType('NovaVpn.StateStore')
$normalize = $stateStore.GetMethod('Normalize', [Reflection.BindingFlags] 'NonPublic,Static')
$normalize.Invoke($null, [object[]]@($state)) | Out-Null
$stateStream = New-Object IO.MemoryStream
$stateSerializer = New-Object Runtime.Serialization.Json.DataContractJsonSerializer($state.GetType())
$stateSerializer.WriteObject($stateStream, $state)
Assert-True ($stateStream.Length -gt 0) 'boundary DateTime state must serialize safely'

$iconFactory = $assembly.GetType('NovaVpn.IconFactory')
$createIcon = $iconFactory.GetMethod('Create')
foreach ($iconName in @('home','servers','routing','diagnostics','settings','power','star','edit','trash','plus','check','shield','globe','speed','palette','grid','list')) {
    $icon = $createIcon.Invoke($null, [object[]]@($iconName, [double]16, $defaultTheme.Accent, $false))
    Assert-True ($null -ne $icon.Child) "vector icon $iconName should render"
}
$resourceStream = $assembly.GetManifestResourceStream('NOVA VPN.g.resources')
Assert-True ($null -ne $resourceStream) 'release assembly must contain compiled WPF resources'
$resourceReader = New-Object System.Resources.ResourceReader($resourceStream)
$resourceEnumerator = $resourceReader.GetEnumerator()
$resourceNames = [System.Collections.Generic.List[string]]::new()
while ($resourceEnumerator.MoveNext()) { $resourceNames.Add([string]$resourceEnumerator.Key) }
$resourceReader.Close()
Assert-True ($resourceNames.Contains('assets/nova-mark.png') -and $resourceNames.Contains('assets/nova-backup.png')) 'required NOVA brand assets should be embedded'
Assert-True (-not $resourceNames.Contains('assets/nova-aurora.png') -and -not $resourceNames.Contains('assets/nova-pearl.png')) 'removed legacy background themes must not be embedded'

Write-Output 'Smoke tests passed.'

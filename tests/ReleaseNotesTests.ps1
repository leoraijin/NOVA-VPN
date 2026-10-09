param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$service = $assembly.GetType('NovaVpn.ReleaseNotesService', $true)
$flags = [Reflection.BindingFlags]'Public,Static'
$current = $service.GetProperty('CurrentVersion', $flags).GetValue($null, $null)
$currentText = $current.ToString(3)

if (-not $service.GetMethod('HasNotesFor', $flags).Invoke($null, @($current))) {
    throw "Release notes are missing for the current app version $currentText."
}

$getPending = $service.GetMethod('GetPendingNotes', $flags)
$allNotes = $getPending.Invoke($null, [object[]]@($null, $current))
if ($allNotes.Count -lt 1) { throw 'A fresh upgrade should have at least one release note entry.' }
if ($allNotes[$allNotes.Count - 1].Version.ToString(3) -ne $currentText) {
    throw 'The current version release note is not included last in the pending notes.'
}

$seenCurrent = $getPending.Invoke($null, [object[]]@($currentText, $current))
if ($seenCurrent.Count -ne 0) { throw 'Release notes should not reappear after the current version was acknowledged.' }

$previous = [Version]::new($current.Major, $current.Minor, $current.Build - 1)
$afterPrevious = $getPending.Invoke($null, [object[]]@($previous.ToString(3), $current))
if ($afterPrevious.Count -ne 1 -or $afterPrevious[0].Version.ToString(3) -ne $currentText) {
    throw 'Only versions newer than the last-seen release should be shown.'
}

$stateStore = $assembly.GetType('NovaVpn.StateStore', $true)
$createDefault = $stateStore.GetMethod('CreateDefault', [Reflection.BindingFlags]'NonPublic,Static')
$freshState = $createDefault.Invoke($null, @())
$marker = $freshState.GetType().GetField('LastSeenReleaseNotesVersion').GetValue($freshState)
if ($marker -ne $currentText) { throw 'A fresh install should start with the current release marker and not show historical updates.' }

Write-Output "PASS: release notes exist for $currentText, unseen notes are filtered, seen notes stay dismissed, and new installs do not receive an upgrade popup."

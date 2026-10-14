param([Parameter(Mandatory=$true)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$source=Get-Content (Join-Path $SourceRoot 'NovaVpn\MainWindow.cs') -Raw
if(([regex]::Matches($source,'SettingCard\([^\r\n]*state.StartWithWindows,')).Count -ne 1){throw 'Exactly one ordinary Windows startup card must remain'}
if($source.Contains('SetStartup(state.StartWithWindows)')){throw 'Lower legacy duplicate remains'}
if(-not $source.Contains('await SetWindowsStartupAsync(false);')){throw 'Upper ordinary startup action changed'}
if(-not $source.Contains('await SetWindowsStartupAsync(true);')){throw 'Background startup action removed'}
'PASS: lower duplicate removed; both upper startup actions retained. No Windows settings changed.'

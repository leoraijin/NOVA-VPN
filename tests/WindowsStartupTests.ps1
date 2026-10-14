param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Core,System.Xml,PresentationCore,PresentationFramework,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$service=$assembly.GetType('NovaVpn.WindowsStartupService')
function Assert($v,$m){if(-not $v){throw $m}}
foreach($connect in @($false,$true)){
 [xml]$xml=$service.GetMethod('BuildTaskXml').Invoke($null,@('D:\Apps & Tools\NOVA VPN.exe','S-1-5-21-1-2-3-1001',$connect))
 Assert ($xml.Task.Principals.Principal.RunLevel -eq 'HighestAvailable') 'scheduled app needs elevation without a logon UAC popup'
 Assert ($xml.Task.Principals.Principal.LogonType -eq 'InteractiveToken') 'no stored password or machine service account'
 Assert ($xml.Task.Triggers.LogonTrigger.UserId -eq 'S-1-5-21-1-2-3-1001') 'only current user logon'
 Assert ($xml.Task.Actions.Exec.Command -eq 'D:\Apps & Tools\NOVA VPN.exe') 'spaces and XML escaping'
 Assert ($xml.Task.Settings.ExecutionTimeLimit -eq 'PT0S') 'scheduler must not kill VPN after default time limit'
 Assert ($xml.Task.Actions.Exec.Arguments.Contains('--background-connect') -eq $connect) 'distinct normal and background launch modes'
 Assert ($xml.Task.Actions.NamespaceURI -eq $xml.Task.NamespaceURI) 'all elements must use the Task Scheduler schema namespace'
 $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
 $definition=$scheduler.NewTask(0)
 $definition.XmlText=$xml.OuterXml.Replace('S-1-5-21-1-2-3-1001',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
 Assert ($definition.Actions.Count -eq 1) 'native scheduler must accept the definition without registering a task'
 [Runtime.InteropServices.Marshal]::FinalReleaseComObject($definition)|Out-Null
 [Runtime.InteropServices.Marshal]::FinalReleaseComObject($scheduler)|Out-Null
}
$pType=$assembly.GetType('NovaVpn.VpnProfile')
$one=[Activator]::CreateInstance($pType);$one.Name='Last';$two=[Activator]::CreateInstance($pType);$two.Name='Fast';$three=[Activator]::CreateInstance($pType);$three.Name='Slow'
$profiles=[Array]::CreateInstance($pType,3);$profiles.SetValue($one,0);$profiles.SetValue($two,1);$profiles.SetValue($three,2)
$reference=Join-Path $SourceRoot 'obj\Startup-Test-Reference.dll'
Copy-Item -LiteralPath $AssemblyPath -Destination $reference
Add-Type -Path (Join-Path $SourceRoot 'tests\StartupSelectorFixture.cs') -ReferencedAssemblies @($reference,'System.Core.dll')
$r=[StartupSelectorFixture]::Run($profiles,$one.Id,$true,$false).GetAwaiter().GetResult()
Assert ($r.Id -eq $one.Id) 'working last profile wins even when another server is faster'
$r=[StartupSelectorFixture]::Run($profiles,$one.Id,$false,$false).GetAwaiter().GetResult()
Assert ($r.Id -eq $two.Id) 'failed last profile must choose fastest HTTPS-success candidate'
$r=[StartupSelectorFixture]::Run($profiles,$one.Id,$false,$true).GetAwaiter().GetResult()
Assert ($null -eq $r) 'all failed must never select a random unverified candidate'
Assert ([StartupSelectorFixture]::Cancellation($profiles)) 'startup selection must be cancellable'
$program=Get-Content (Join-Path $SourceRoot 'NovaVpn\Program.cs') -Raw
Assert ($program.Contains('application.Run();') -and $program.Contains('RunWindowsBackgroundStartupAsync')) 'hidden path must not call Run(window)'
$window=Get-Content (Join-Path $SourceRoot 'NovaVpn\MainWindow.cs') -Raw
Assert ($window.Contains('!WindowsStartupLaunch && state.AutoConnect')) 'normal Windows startup must not inherit manual auto-connect setting'
'PASS: task XML, elevation/user scope, escaped path, launch modes, last-profile preference, HTTPS fallback, all-offline, cancellation and hidden-path source checks. No scheduled task or live VPN changed.'

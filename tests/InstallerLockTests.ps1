param([Parameter(Mandatory=$true)][string]$InstallerPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($InstallerPath))
$type=$assembly.GetType('NovaInstaller',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$target=Join-Path $WorkPath 'target'
$other=Join-Path $WorkPath 'other'
New-Item -ItemType Directory -Path (Join-Path $target 'core'),(Join-Path $other 'core') -Force | Out-Null
$fixture=Join-Path $WorkPath 'Sleep.cs'
[IO.File]::WriteAllText($fixture,'class Fixture { static void Main() { System.Threading.Thread.Sleep(120000); } }')
$csc=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ownedExe=Join-Path $target 'core\sing-box.exe'
$otherExe=Join-Path $other 'core\sing-box.exe'
& $csc /nologo /target:exe "/out:$ownedExe" $fixture
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
Copy-Item -LiteralPath $ownedExe -Destination $otherExe -Force
$ownedProcess=$null;$otherProcess=$null
try {
  $ownedProcess=Start-Process -FilePath $ownedExe -WindowStyle Hidden -PassThru
  $otherProcess=Start-Process -FilePath $otherExe -WindowStyle Hidden -PassThru
  Start-Sleep -Milliseconds 300
  $type.GetMethod('StopInstalledComponents',$flags).Invoke($null,[object[]]@([string]$target))
  if(-not $ownedProcess.WaitForExit(3000)){throw 'Owned core was not stopped'}
  if($otherProcess.HasExited){throw 'Foreign core was incorrectly stopped'}
  # The actual image file must now be writable; no unrelated process was stopped.
  $file=[IO.File]::Open($ownedExe,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$file.Dispose()
  $first=Join-Path $target 'first.txt';$locked=Join-Path $target 'locked.txt'
  [IO.File]::WriteAllText($first,'original');[IO.File]::WriteAllText($locked,'locked')
  $memory=New-Object IO.MemoryStream
  $zip=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
  foreach($name in @('first.txt','locked.txt')){ $entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('replacement');$writer.Dispose() }
  $zip.Dispose();$memory.Position=0
  $zip=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read,$true)
  $lock=[IO.File]::Open($locked,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  try {
    $rejected=$false
    try {$type.GetMethod('PreflightInstallFiles',$flags).Invoke($null,[object[]]@([IO.Compression.ZipArchive]$zip,[string]$target))} catch { if($_.Exception.GetBaseException() -is [IO.IOException]){$rejected=$true}else{throw} }
    if(-not $rejected){throw 'Locked payload was not detected'}
    if([IO.File]::ReadAllText($first) -ne 'original'){throw 'Preflight changed an existing file'}
  } finally {$lock.Dispose()}
  $type.GetMethod('PreflightInstallFiles',$flags).Invoke($null,[object[]]@([IO.Compression.ZipArchive]$zip,[string]$target))
  $zip.Dispose();$memory.Dispose()
  $ownerSource=Join-Path $WorkPath 'RespawnOwner.cs'
  [IO.File]::WriteAllText($ownerSource,'using System; using System.Diagnostics; using System.IO; using System.Threading; class Owner { static void Main() { string core=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"core","sing-box.exe"); Process child=null; while(true) { if(child==null || child.HasExited) child=Process.Start(new ProcessStartInfo(core){UseShellExecute=false,CreateNoWindow=true}); Thread.Sleep(40); } } }')
  $ownerExe=Join-Path $target 'NOVA VPN.exe'
  & $csc /nologo /target:exe "/out:$ownerExe" $ownerSource
  if($LASTEXITCODE -ne 0){throw 'Respawn owner fixture compilation failed'}
  $ownerProcess=Start-Process -FilePath $ownerExe -WindowStyle Hidden -PassThru
  try {
    Start-Sleep -Milliseconds 700
    $type.GetMethod('StopInstalledComponents',$flags).Invoke($null,[object[]]@([string]$target))
    if(-not $ownerProcess.WaitForExit(3000)){throw 'Installed owner was left alive to respawn core'}
    Start-Sleep -Milliseconds 700
    $file=[IO.File]::Open($ownedExe,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$file.Dispose()
    if($otherProcess.HasExited){throw 'Foreign same-name core was stopped during owner shutdown'}
  } finally {
    if(-not $ownerProcess.HasExited){$ownerProcess.Kill();$ownerProcess.WaitForExit(3000)|Out-Null};$ownerProcess.Dispose()
    $type.GetMethod('StopInstalledComponents',$flags).Invoke($null,[object[]]@([string]$target))
  }
  Write-Output 'PASS: installed auto-reconnect owner stopped before core; no respawn and unrelated core survived.'
  Write-Output 'PASS: orphan core at exact installation path stopped, foreign same-name core preserved, executable unlocked, payload locks detected before replacement, retry succeeds.'
} finally {
  foreach($process in @($ownedProcess,$otherProcess)){if($process){try{if(-not $process.HasExited){$process.Kill();$process.WaitForExit(3000)|Out-Null}}finally{$process.Dispose()}}}
}

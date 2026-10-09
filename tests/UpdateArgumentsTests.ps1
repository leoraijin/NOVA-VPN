param([string]$AssemblyPath,[string]$InstallerPath)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeArguments {
 [DllImport("shell32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string command, out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
 public static string[] Parse(string command) {
  int count; IntPtr pointer=CommandLineToArgvW(command,out count);
  if(pointer==IntPtr.Zero) throw new Exception("Parse failed");
  try { var result=new string[count]; for(int i=0;i<count;i++) result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size)); return result; }
  finally { LocalFree(pointer); }
 }
}
'@
$app=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$quote=$app.GetType('NovaVpn.AppUpdateService').GetMethod('QuoteWindowsArgument')
$setup=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($InstallerPath))
$normalize=$setup.GetType('NovaInstaller').GetMethod('NormalizeLegacyUpdateArguments',[Reflection.BindingFlags]'NonPublic,Static')
$cases=@('C:\Program Files\NOVA VPN\','D:\NOVA VPN\','C:\','D:\Apps\NOVA','C:\Users\Roman\VPN\\')
foreach($path in $cases) {
 $quoted=$quote.Invoke($null,@($path))
 $parsed=[NativeArguments]::Parse(('setup.exe --update-path '+$quoted+' --wait-pid 34736 --delete-self-on-reboot'))
 if($parsed.Count -ne 6 -or $parsed[2] -cne $path -or $parsed[4] -ne '34736'){throw 'Windows argument roundtrip failed'}
}
$legacy=[string[]]@('--update-path','C:\Program Files\NOVA VPN" --wait-pid 34736 --delete-self-on-reboot')
$fixed=$normalize.Invoke($null,@(,$legacy))
if($fixed.Count -ne 5 -or $fixed[1] -ne 'C:\Program Files\NOVA VPN' -or $fixed[3] -ne '34736'){throw 'Legacy recovery failed'}
$invalid=[string[]]@('--update-path','relative" --wait-pid 12 --delete-self-on-reboot')
$unchanged=$normalize.Invoke($null,@(,$invalid))
if($unchanged.Count -ne 2){throw 'Unsafe legacy argument accepted'}
Write-Output 'PASS: native Windows parsing of 5 directory cases, PID and cleanup flags separated; legacy invocation recovered; relative malformed path not repaired.'

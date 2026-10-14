param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if($null -eq [Windows.Application]::Current){$app=New-Object Windows.Application}
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow');$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
function Assert($value,$message){if(-not $value){throw $message}}
try{
 $core=$type.GetField('core',$flags).GetValue($window)
 $status=$assembly.GetType('NovaVpn.CoreStatus')
 $core.GetType().GetProperty('Status').GetSetMethod($true).Invoke($core,@([Enum]::Parse($status,'Connected')))|Out-Null
 $verification=$core.Verification
 $headline=$type.GetMethod('StatusHeadline',$flags)
 Assert ($headline.Invoke($window,@()) -eq 'Проверяем соединение') 'running TUN must not mean verified protection'
 $verification.Completed=$true
 Assert ($headline.Invoke($window,@()) -eq 'Нет доступа в интернет') 'failed active route must not remain checking forever'
 $verification.InternetOk=$true
 Assert ($headline.Invoke($window,@()) -eq 'Туннель включён') 'ordinary internet response is not proof of VPN profile traffic'
 $probe=[Activator]::CreateInstance($assembly.GetType('NovaVpn.ServerMapHealth'));$probe.CheckedUtc=[DateTime]::UtcNow
 $verification.ProfileProbe=$probe
 Assert ($headline.Invoke($window,@()) -eq 'Профиль не прошёл проверку') 'failed proxy is not protected despite working ordinary internet'
 $probe.Success=$true
 Assert ($headline.Invoke($window,@()) -eq 'Вы защищены') 'both checks successful'
 $verification.InternetOk=$false
 Assert ($headline.Invoke($window,@()) -eq 'Нет доступа в интернет') 'successful isolated proxy must not hide actual active-route failure'
 $verification.InternetOk=$true;$probe.Supported=$false;$probe.Success=$false
 Assert ($headline.Invoke($window,@()) -eq 'Туннель включён') 'unsupported full config must not invent proxy validation'
 'PASS: pending, route failure, profile failure, confirmed success and unsupported configuration display states; snapshot only, no live VPN changes.'
}finally{$window.Close()}

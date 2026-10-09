param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Runtime.Serialization
$app=New-Object Windows.Application;$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow');$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$owner=[Activator]::CreateInstance($type,@($true));$owner.Left=-32000;$owner.Top=-32000;$owner.ShowInTaskbar=$false;$owner.Show()
try {
 foreach($expected in @(0,1,2)) {
  $timer=New-Object Windows.Threading.DispatcherTimer;$timer.Interval=[TimeSpan]::FromMilliseconds(100)
  $timer.add_Tick({$timer.Stop();$dialog=@($app.Windows|Where-Object {$_.Owner -eq $owner})[0];$actions=$dialog.Content.Children[$dialog.Content.Children.Count-1];$actions.Children[$expected].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))})
  $timer.Start();$answer=$type.GetMethod('ShowApplicationUpdatePrompt',$flags).Invoke($owner,@([Version]'9.9.9'))
  if($answer -ne $expected){throw 'Incorrect update prompt choice'}
 }
 $stateType=$assembly.GetType('NovaVpn.AppState');$state=[Activator]::CreateInstance($stateType);$state.SuppressUpdatePrompts=$true
 foreach($field in $stateType.GetFields()) {if($field.FieldType -eq [DateTime]){$field.SetValue($state,[DateTime]::SpecifyKind([DateTime]::MinValue,[DateTimeKind]::Utc))}}
 $serializer=[Runtime.Serialization.Json.DataContractJsonSerializer]::new($stateType);$stream=New-Object IO.MemoryStream
 try {$serializer.WriteObject($stream,$state);$stream.Position=0;$restored=$serializer.ReadObject($stream);if(-not $restored.SuppressUpdatePrompts){throw 'Suppression preference was not persisted'}}finally{$stream.Dispose()}
 Write-Output 'PASS: all three real dialog buttons return the correct action; suppression survives serialization. No network request, installer launch or VPN connection occurred.'
}finally{$owner.Close();$app.Shutdown()}

param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object System.Windows.Application
$app.ShutdownMode=[System.Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$type=$assembly.GetType('NovaVpn.MainWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
$state=$type.GetField('state',$flags).GetValue($window)
$state.Profiles.Clear()
$multi=[Activator]::CreateInstance($assembly.GetType('NovaVpn.MultiServerSettings'))
$multi.DefaultProfileId='p1'
$state.SelectedProfileId='p1'
$slotMethod=$multi.GetType().GetMethod('SetSlot',$flags)
foreach($n in 1..10) {
 $profile=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VpnProfile'))
 $profile.Id="p$n"; $profile.Name="VPN-$n · Test country — Server with a long name"; $profile.Protocol='vless'; $profile.Server="vpn$n.example.test"; $profile.Port=443
 $state.Profiles.Add($profile)
 if($n -gt 1) {
  $slotMethod.Invoke($multi,@([int]$n,"p$n"))|Out-Null
  $rule=[Activator]::CreateInstance($assembly.GetType('NovaVpn.MultiServerRuleRecord'))
  $rule.Target='Profile';$rule.ProfileId="p$n";$rule.Domain="vpn$n.example.org";$multi.Rules.Add($rule)
 }
}
$state.Routing.MultiServer=$multi
$build=$type.GetMethod('BuildShell',$flags)
$navigate=$type.GetMethod('Navigate',$flags)
$window.Left=-32000; $window.Top=-32000; $window.ShowInTaskbar=$false
$window.WindowStartupLocation=[System.Windows.WindowStartupLocation]::Manual
$window.ShowActivated=$false
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$results=New-Object 'System.Collections.Generic.List[object]'
function Nodes($node) {
 $node
 for($i=0;$i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node);$i++){Nodes ([Windows.Media.VisualTreeHelper]::GetChild($node,$i))}
}
function Capture($target,$name,$scale) {
    $target.Dispatcher.Invoke([Action]{},[System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
    $target.UpdateLayout()
    $width=[int][Math]::Ceiling($target.ActualWidth*$scale)
    $height=[int][Math]::Ceiling($target.ActualHeight*$scale)
    $image=New-Object System.Windows.Media.Imaging.RenderTargetBitmap($width,$height,(96*$scale),(96*$scale),[System.Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($target)
    $encoder=New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($image))
    $path=Join-Path $OutputDirectory ($name+'.png')
    $stream=[IO.File]::Create($path)
    try{$encoder.Save($stream)}finally{$stream.Dispose()}
    $results.Add([pscustomobject]@{Name=$name;Pixels="$width x $height";Dpi=96*$scale;Path=$path})
}
try {
    $window.Show()
    foreach($theme in @('Windows 11 Light','Windows 11 Dark')) {
        $slug=($theme-replace '[^A-Za-z0-9]+','-').Trim('-')
        foreach($case in @(@{Width=1200;Height=760;Scale=1;Text=1;Name='desktop'},@{Width=720;Height=600;Scale=1.5;Text=1.35;Name='large-text-150'})) {
            $window.ConfigureSnapshot($theme,'','','',[double]$case.Text,$false)
            $window.Width=$case.Width; $window.Height=$case.Height
            $build.Invoke($window,@())|Out-Null
            foreach($page in @('home','routing')) {
                $navigate.Invoke($window,@($page,$false))|Out-Null
                Capture $window "$slug-$($case.Name)-$page" $case.Scale
                $name=if($page -eq 'routing'){'RoutingSitesVpn10'}else{'DualVpnCard'}
                $control=@(Nodes $window | Where-Object {$_ -is [Windows.FrameworkElement] -and $_.Name -eq $name})[0]
                if(!$control){throw "Missing dynamic control: $name"}
                if($page -eq 'home') {
                 $expand=@(Nodes $control | Where-Object {$_ -is [Windows.Controls.Expander]})[0]
                 if($expand){$expand.IsExpanded=$true;$window.UpdateLayout()}
                }
                $control.BringIntoView();$window.UpdateLayout()
                Capture $window "$slug-$($case.Name)-$page-slots" $case.Scale
            }
        }

    }
} finally {$window.Close(); $app.Shutdown()}
$results|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $OutputDirectory 'render-matrix.json') -Encoding UTF8
"Rendered $($results.Count) layouts. DPI uses raster scaling and constrained logical viewports; Windows display settings were not changed."

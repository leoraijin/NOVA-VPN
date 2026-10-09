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
$build=$type.GetMethod('BuildShell',$flags)
$navigate=$type.GetMethod('Navigate',$flags)
$window.Left=-32000; $window.Top=-32000; $window.ShowInTaskbar=$false
$window.WindowStartupLocation=[System.Windows.WindowStartupLocation]::Manual
$window.ShowActivated=$false
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$results=New-Object 'System.Collections.Generic.List[object]'
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
    foreach($theme in $assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)) {
        $slug=($theme-replace '[^A-Za-z0-9]+','-').Trim('-')
        foreach($case in @(@{Width=1200;Height=760;Scale=1;Text=1;Name='desktop'},@{Width=960;Height=720;Scale=1.25;Text=1;Name='balanced-125'},@{Width=720;Height=600;Scale=1.5;Text=1.35;Name='large-text-150'},@{Width=720;Height=520;Scale=2;Text=1;Name='compact-200'})) {
            $state.Visual.LoadThemeSettings($theme); $state.Visual.TextScale=$case.Text
            $window.Width=$case.Width; $window.Height=$case.Height
            $build.Invoke($window,@())|Out-Null
            foreach($page in @('home','servers','routing','settings','diagnostics','learning')) {
                $navigate.Invoke($window,@($page,$false))|Out-Null
                Capture $window "$slug-$($case.Name)-$page" $case.Scale
            }
        }
        $state.Visual.LoadThemeSettings($theme)
        $dialog=[Activator]::CreateInstance($assembly.GetType('NovaVpn.AppearanceWindow'),@($state.Visual))
        $dialog.Left=-32000; $dialog.Top=-32000; $dialog.ShowActivated=$false; $dialog.ShowInTaskbar=$false
        $dialog.WindowStartupLocation=[System.Windows.WindowStartupLocation]::Manual
        try {
          $dialog.Show(); Capture $dialog "$slug-personalization" 1
          $dialog.ScrollForSnapshot(900); Capture $dialog "$slug-personalization-motion" 1
          if($theme -like 'iOS*') {
            $dialog.ScrollForSnapshot(1100); Capture $dialog "$slug-personalization-glass" 1
            $dialog.ScrollForSnapshot(1450); Capture $dialog "$slug-personalization-effects" 1
          }
        } finally {$dialog.Close()}
    }
} finally {$window.Close(); $app.Shutdown()}
$results|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $OutputDirectory 'render-matrix.json') -Encoding UTF8
"Rendered $($results.Count) layouts. DPI uses raster scaling and constrained logical viewports; Windows display settings were not changed."

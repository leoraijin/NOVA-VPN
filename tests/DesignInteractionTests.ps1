param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app = New-Object System.Windows.Application
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Assert($condition,$message) { if(-not $condition){throw $message} }
function Descendants($node) {
    if($node -is [System.Windows.DependencyObject]) {
        $node
        foreach($child in [System.Windows.LogicalTreeHelper]::GetChildren($node)) { if($child -is [System.Windows.DependencyObject]) { Descendants $child } }
    }
}
$runtime=@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
public static class DesignFrameProbe {
  public static double[] Measure(int milliseconds) {
    var samples = new List<double>(); var clock = Stopwatch.StartNew();
    double previous = -1; TimeSpan previousRender = TimeSpan.MinValue;
    EventHandler handler = delegate(object sender, EventArgs args) {
      var e = (RenderingEventArgs)args;
      if(e.RenderingTime == previousRender) return;
      previousRender = e.RenderingTime; double now = clock.Elapsed.TotalMilliseconds;
      if(now < 250) return;
      if(previous >= 0) samples.Add(now-previous); previous = now;
    };
    var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) };
    timer.Tick += delegate { timer.Stop(); frame.Continue=false; };
    CompositionTarget.Rendering += handler;
    try { timer.Start(); Dispatcher.PushFrame(frame); }
    finally { timer.Stop(); CompositionTarget.Rendering -= handler; }
    return samples.ToArray();
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct Mode {
    [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string device;
    public short spec,driver,size,extra; public int fields,x,y,orientation,fixedOutput;
    public short color,duplex,resolution,tt,collate;
    [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string form;
    public short pixels; public int bits,width,height,flags,frequency,icm,intent,media,dither,r1,r2,pw,ph;
  }
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettings(string device,int mode,ref Mode settings);
  public static int RefreshRate() { var mode=new Mode(); mode.size=(short)Marshal.SizeOf(typeof(Mode)); return EnumDisplaySettings(null,-1,ref mode)?mode.frequency:0; }
}
'@
Add-Type -TypeDefinition $runtime -ReferencedAssemblies @([System.Windows.Window].Assembly.Location,[System.Windows.Media.CompositionTarget].Assembly.Location,[System.Windows.Threading.Dispatcher].Assembly.Location,'System.dll')
$visual=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences'))
$visual.FollowSystemMotion=$false
$dialog=[Activator]::CreateInstance($assembly.GetType('NovaVpn.AppearanceWindow'),@($visual))
$dialog.Left=-32000; $dialog.Top=-32000; $dialog.ShowActivated=$false; $dialog.ShowInTaskbar=$false
$dialog.Show(); $dialog.UpdateLayout()
$appearance=$dialog.GetType()
$body=$appearance.GetField('body',$flags).GetValue($dialog)
$sliders=@(Descendants $body | Where-Object {$_ -is [System.Windows.Controls.Slider]})
  Assert ($sliders.Count -eq 6) 'Expected the five existing visual sliders plus the independent soft-contrast slider'
$properties=@('TextScale','CornerRadius','SurfaceOpacity','GlowIntensity','AnimationSpeed','SoftContrast')
$divisors=@(100,1,100,100,100,100)
$preview=$appearance.GetField('previewCard',$flags).GetValue($dialog)
$previewTimer=$appearance.GetField('previewTimer',$flags).GetValue($dialog)
Assert ($previewTimer.Interval.TotalMilliseconds -ge 33) 'Preview refreshes must be throttled below display-frame cadence'
$notify=$appearance.GetMethod('NotifyVisualChanged',$flags)
$notify.Invoke($dialog,@()) | Out-Null
$notify.Invoke($dialog,@()) | Out-Null
Assert ($previewTimer.IsEnabled) 'Several rapid preference changes should share one scheduled preview refresh'
$appearance.GetMethod('FlushPreview',$flags).Invoke($dialog,@()) | Out-Null
Assert (-not $previewTimer.IsEnabled) 'Flushing the preview should cancel its pending timer'
$timings=New-Object 'System.Collections.Generic.List[double]'
try {
  for($i=0;$i -lt $sliders.Count;$i++) {
    foreach($fraction in @(0,.5,1)) {
      $sliders[$i].Value=$sliders[$i].Minimum+($sliders[$i].Maximum-$sliders[$i].Minimum)*$fraction
      $watch=[Diagnostics.Stopwatch]::StartNew()
      $appearance.GetMethod('FlushPreview',$flags).Invoke($dialog,@()) | Out-Null
      $dialog.UpdateLayout(); $watch.Stop(); $timings.Add($watch.Elapsed.TotalMilliseconds)
      $expected=$sliders[$i].Value/$divisors[$i]
      Assert ([Math]::Abs($dialog.Result.($properties[$i])-$expected) -lt .001) "Slider must change $($properties[$i])"
      Assert ([object]::ReferenceEquals($preview,$appearance.GetField('previewCard',$flags).GetValue($dialog))) 'Slider must retain the preview tree'
    }
  }
  Assert ($visual.TextScale -eq 1 -and $visual.CornerRadius -eq 18) 'Preview must not mutate caller preferences'
  $draft=$dialog.Result
  $choiceGroups=@(Descendants $body | Where-Object {
    $_ -is [System.Windows.Controls.WrapPanel] -and $_.Children.Count -eq 3 -and
    $_.Children[0] -is [System.Windows.Controls.Button] -and $null -ne $_.Children[0].Tag
  })
  Assert ($choiceGroups.Count -eq 4) 'Expected density, server view, background and frame-rate selectors'
  $choiceProperties=@('Density','ServerViewMode','BackgroundMode','AnimationFrameRate')
  for($groupIndex=0;$groupIndex -lt 4;$groupIndex++) {
    foreach($choice in $choiceGroups[$groupIndex].Children) {
      $choice.RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent))
      $appearance.GetMethod('FlushPreview',$flags).Invoke($dialog,@()) | Out-Null
      $expectedChoice=$choice.Tag
      if($groupIndex -eq 3) {$expectedChoice=if($choice.Tag -eq 'Auto'){0}else{[int]$choice.Tag}}
      Assert ($draft.($choiceProperties[$groupIndex]) -eq $expectedChoice) 'Choice click must update the corresponding preference'
    }
  }
  $toggles=@(Descendants $body | Where-Object {
    $_ -is [System.Windows.Controls.Button] -and $_.Content -is [System.Windows.Controls.Border] -and
    $_.Content.Child -is [System.Windows.Shapes.Ellipse]
  })
  Assert ($toggles.Count -eq 10) 'Expected five accessibility/layout toggles and five card toggles'
  $toggleProperties=@('ReduceMotion','FollowSystemMotion','HighContrast','ShowLiveGraph','ShowConnectionMap')
  for($toggleIndex=0;$toggleIndex -lt 5;$toggleIndex++) {
    $before=$draft.($toggleProperties[$toggleIndex])
    $toggles[$toggleIndex].RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent))
    Assert ($draft.($toggleProperties[$toggleIndex]) -eq (-not $before)) 'Toggle click must change preference'
    $toggles[$toggleIndex].RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent))
    Assert ($draft.($toggleProperties[$toggleIndex]) -eq $before) 'Second toggle click must restore preference'
  }
  $accentField=$appearance.GetField('accentHexInput',$flags)
  Assert ($null -eq $accentField -and $draft.AccentHex -eq '') 'Stock profile customization must not expose a free-form HEX color editor'
  $draft.ShowConnectionMap=$false; $draft.ShowLiveGraph=$false
  $draft.HiddenHomeCards.Add('Privacy'); $draft.HomeCardOrder.Reverse()
  $appearance.GetMethod('UpdatePreviewCard',$flags).Invoke($dialog,@()) | Out-Null
  Assert ($appearance.GetField('previewRoute',$flags).GetValue($dialog).Visibility -eq 'Collapsed') 'Map toggle must affect preview'
  Assert ($appearance.GetField('previewGraph',$flags).GetValue($dialog).Visibility -eq 'Collapsed') 'Graph toggle must affect preview'
  $metrics=$appearance.GetField('previewMetrics',$flags).GetValue($dialog)
  Assert ($metrics.Children[0].Tag -eq 'Privacy' -and $metrics.Children[0].Visibility -eq 'Collapsed') 'Card order and hidden cards must affect preview'
  $assembly.GetType('NovaVpn.StateStore').GetMethod('NormalizeForSerialization') | Out-Null
} finally { $dialog.Close() }

$connection=[Activator]::CreateInstance($assembly.GetType('NovaVpn.ConnectionControl'))
$motionWindow=New-Object System.Windows.Window
$motionWindow.Title='NOVA - animation measurement'; $motionWindow.Width=300; $motionWindow.Height=290
$motionWindow.ShowActivated=$false; $motionWindow.ShowInTaskbar=$false
$motionWindow.WindowStartupLocation=[System.Windows.WindowStartupLocation]::Manual
$motionWindow.Left=[System.Windows.SystemParameters]::WorkArea.Left+60
$motionWindow.Top=[System.Windows.SystemParameters]::WorkArea.Top+60
$motionWindow.Topmost=$true
$motionRoot=New-Object System.Windows.Controls.Grid
$backdropMethod=$assembly.GetType('NovaVpn.VisualDesign').GetMethod('Backdrop')
$motionRoot.Children.Add($backdropMethod.Invoke($null,@($visual)))|Out-Null
$motionRoot.Children.Add($connection)|Out-Null
$motionWindow.Content=$motionRoot
$statusType=$assembly.GetType('NovaVpn.CoreStatus')
$connecting=[Enum]::Parse($statusType,'Connecting')
$results=@()
try {
  $motionWindow.Show()
  foreach($theme in $assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)) {
    $visual.LoadThemeSettings($theme); $visual.FollowSystemMotion=$false; $visual.AnimationFrameRate=120
    $motionRoot.Children.RemoveAt(0)
    $motionRoot.Children.Insert(0,$backdropMethod.Invoke($null,@($visual)))
    $button=$connection.ActionButton
    $connection.Apply($visual,$connecting,$true); $motionWindow.UpdateLayout()
    Assert ($connection.IsAnimating) 'Connecting control must animate when visible'
    Assert ([object]::ReferenceEquals($button,$connection.ActionButton)) 'Theme application must retain action button'
    $intervals=@([DesignFrameProbe]::Measure(1500))
    Assert ($intervals.Count -gt 5) 'Visible connection must produce rendering callbacks'
    $sorted=@($intervals | Sort-Object)
    $measuredFps=1000/($intervals|Measure-Object -Average).Average
    $p95=$sorted[[int][Math]::Floor(($sorted.Count-1)*.95)]
    # Functional assertions do not prove the requested animation cadence.
    # Keep the performance acceptance result explicit and separate.
    $results += [pscustomobject]@{Theme=$theme;RequestedFps=120;MeasuredCallbacksPerSecond=[Math]::Round($measuredFps,1);P95IntervalMs=[Math]::Round($p95,2);CadenceTargetMet=($measuredFps -ge 114 -and $p95 -le 12.5);ButtonWidth=$button.Width;ButtonHeight=$button.Height}
    $visual.ReduceMotion=$true; $connection.Apply($visual,$connecting,$true)
    Assert (-not $connection.IsAnimating) 'Reduced motion must stop animation'
    $visual.ReduceMotion=$false; $connection.Apply($visual,$connecting,$true)
    $motionWindow.Hide(); Assert (-not $connection.IsAnimating) 'Hidden window must stop animation'
    $motionWindow.Show(); Assert ($connection.IsAnimating) 'Restored window must resume animation'
    $connection.Apply($visual,[Enum]::Parse($statusType,'Connected'),$true)
    Assert (-not $connection.IsAnimating) 'Connected idle control must stop decorative animation clocks'
  }
} finally { $motionWindow.Close(); $app.Shutdown() }
[IO.Directory]::CreateDirectory($WorkPath) | Out-Null
$report=[pscustomobject]@{PrimaryDisplayHz=[DesignFrameProbe]::RefreshRate();RenderTier=([System.Windows.Media.RenderCapability]::Tier -shr 16);PreviewUpdateMaxMs=[Math]::Round(($timings|Measure-Object -Maximum).Maximum,2);SliderChecks=$timings.Count;Motion=$results;Note='Rendering callbacks measure UI/compositor cadence, not physical frame presentation.'}
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $WorkPath 'design-interaction-report.json') -Encoding UTF8
$report|ConvertTo-Json -Depth 6

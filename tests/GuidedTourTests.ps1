param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$WorkPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Web.Extensions
$app=New-Object Windows.Application
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow');$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
$window.Left=-32000;$window.Top=-32000;$window.ShowInTaskbar=$false;$window.ShowActivated=$false
$window.WindowStartupLocation=[Windows.WindowStartupLocation]::Manual
$state=$type.GetField('state',$flags).GetValue($window)
$steps=$type.GetField('tourSteps',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
$serializer=New-Object Web.Script.Serialization.JavaScriptSerializer
[IO.Directory]::CreateDirectory($WorkPath)|Out-Null
function Field($name){$type.GetField($name,$flags).GetValue($window)}
function Call($name,$arguments=@()){$type.GetMethod($name,$flags).Invoke($window,$arguments)|Out-Null}
function Idle { $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle);$window.UpdateLayout() }
function Assert($condition,$message){if(-not $condition){throw $message}}
function Key($key){
 $event=New-Object Windows.Input.KeyEventArgs([Windows.Input.Keyboard]::PrimaryDevice,[Windows.PresentationSource]::FromVisual($window),0,$key)
 $event.RoutedEvent=[Windows.Input.Keyboard]::PreviewKeyDownEvent;$window.RaiseEvent($event);Idle
}
function Capture($name){
 $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$window.ActualWidth,[int]$window.ActualHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($window);$encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create((Join-Path $WorkPath ($name+'.png')))
 try{$encoder.Save($stream)}finally{$stream.Dispose()}
}
$checked=0
try {
 $window.Show();Idle
 foreach($theme in $assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)){
  foreach($size in @(@(1200,760),@(960,720),@(720,600))){
   $state.Visual.LoadThemeSettings($theme);$window.Width=$size[0];$window.Height=$size[1];Idle
   Call 'BuildShell';Call 'Navigate' @('home',$false);Idle
   $before=$serializer.Serialize($state)
   Call 'StartGuidedTour';Idle
   Assert ((Field 'tourActive')) 'Tour did not start'
   for($i=0;$i -lt $steps.Length;$i++){
    $type.GetField('tourIndex',$flags).SetValue($window,$i);Call 'ShowTourStep';Idle
    Assert ((Field 'currentPage') -eq $steps[$i].Page) 'Tour must navigate to the real page'
    $anchors=Field 'tourAnchors'
    Assert ($anchors.ContainsKey($steps[$i].Anchor)) ('Missing target '+$steps[$i].Anchor)
    $overlay=Field 'tourOverlay';Assert ($null -ne $overlay) 'Overlay missing'
    $card=$overlay.Children[$overlay.Children.Count-1]
    $left=[Windows.Controls.Canvas]::GetLeft($card);$top=[Windows.Controls.Canvas]::GetTop($card)
    Assert ($left -ge 0 -and $top -ge 0 -and $left+$card.ActualWidth -le $window.ActualWidth+1 -and $top+$card.ActualHeight -le $window.ActualHeight+1) 'Tour card must fit viewport'
    Assert (-not (Field 'tourPageRoot').IsHitTestVisible) 'Tour must prevent accidental underlying actions'
    Assert ([Windows.Input.KeyboardNavigation]::GetTabNavigation((Field 'tourPageRoot')) -eq [Windows.Input.KeyboardNavigationMode]::None) 'Underlying tab navigation must be blocked'
    Assert ($serializer.Serialize($state) -eq $before) 'Tour changed user configuration'
    if($size[0] -eq 1200 -and $i -in @(0,4,5,9,10)){Capture (($theme -replace '[^A-Za-z0-9]','-')+'-step-'+($i+1))}
    $checked++
   }
   Call 'TourNext';Idle
   Assert (-not (Field 'tourActive')) 'Last Next should finish'
   Assert ((Field 'currentPage') -eq 'home') 'Finish should return to original page'
   Assert ((Field 'tourPageRoot').IsHitTestVisible) 'Finish must restore input'
   Call 'StartGuidedTour';Call 'EndGuidedTour';Idle
   Assert (-not (Field 'tourActive')) 'Skip/restart must work'
   Call 'StartGuidedTour';Idle;Key ([Windows.Input.Key]::Right)
   Assert ((Field 'tourIndex') -eq 1) 'Right key must advance'
   Key ([Windows.Input.Key]::Left);Assert ((Field 'tourIndex') -eq 0) 'Left key must go back'
   Key ([Windows.Input.Key]::Escape);Assert (-not (Field 'tourActive')) 'Escape must end the tour'
  }
 }
}finally{$window.Close();$app.Shutdown()}
"PASS: $checked steps across five themes and three widths; real targets/pages, viewport fit, read-only state, finish, skip and restart. No live VPN or user settings changed."

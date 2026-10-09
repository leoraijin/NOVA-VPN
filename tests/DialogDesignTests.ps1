param([Parameter(Mandatory=$true)][string]$AssemblyPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object System.Windows.Application
$app.ShutdownMode=[System.Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
function Nodes($node) {
  if($node -is [System.Windows.DependencyObject]) {
    $node
    foreach($child in [System.Windows.LogicalTreeHelper]::GetChildren($node)) {
      if($child -is [System.Windows.DependencyObject]) {Nodes $child}
    }
  }
}
try {
  $themes=$assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames').GetValue($null)
  for($i=0;$i -lt $themes.Count;$i++) {
    $visual=[Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences'))
    $visual.LoadThemeSettings($themes[$i]); $visual.TextScale=1.35; $visual.AccentHex='#FFF4DB'
    $windows=@(
      [Activator]::CreateInstance($assembly.GetType('NovaVpn.ImportWindow'),@($visual)),
      [Activator]::CreateInstance($assembly.GetType('NovaVpn.TextPromptWindow'),@('Test prompt','Example descriptive caption for a saved profile','Example value',$visual))
    )
    for($j=0;$j -lt $windows.Count;$j++) {
      $window=$windows[$j]
      try {
        $window.Left=-32000; $window.Top=-32000; $window.ShowActivated=$false; $window.ShowInTaskbar=$false
        $window.Show();$window.UpdateLayout()
        $palette=$assembly.GetType('NovaVpn.ThemePalette').GetMethod('Create').Invoke($null,@($visual))
        $buttons=@(Nodes $window.Content | Where-Object {$_ -is [System.Windows.Controls.Button] -and $_.Background -is [System.Windows.Media.SolidColorBrush] -and $_.Background.Color -eq $palette.Accent})
        if($buttons.Count -ne 1){throw 'Expected one primary button with stock theme accent'}
        if($buttons[0].Foreground.Color -ne $palette.AccentForeground){throw 'Stock accent must use its computed readable foreground'}
        if($buttons[0].FontSize -lt 16){throw 'Dialog must respect text scale'}
        if($null -eq $buttons[0].Template){throw 'Dialog must use designed button template'}
        $target=[System.Windows.Media.Imaging.RenderTargetBitmap]::new([int]$window.ActualWidth,[int]$window.ActualHeight,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
        $target.Render($window)
        $encoder=New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))
        $file=[IO.File]::Create((Join-Path $OutputDirectory ('theme-'+$i+'-dialog-'+$j+'.png')))
        try {$encoder.Save($file)}finally{$file.Dispose()}
      } finally {$window.Close()}
    }
  }
  Write-Output 'PASS: ten dialogs rendered, five stock profiles, large text and readable stock-accent foregrounds.'
} finally {$app.Shutdown()}

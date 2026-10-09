param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$InitialTheme = 'Windows 11 Light'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if ($null -eq [System.Windows.Application]::Current) {
    $application = New-Object System.Windows.Application
    $application.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
}

$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[System.Windows.Application]::ResourceAssembly = $assembly
$windowType = $assembly.GetType('NovaVpn.MainWindow')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$modeField = $windowType.GetField('responsiveMode', $flags)
$stateField = $windowType.GetField('state', $flags)
$buildShell = $windowType.GetMethod('BuildShell', $flags)
$modeResolver = $windowType.GetMethod('ResponsiveModeForWidth', [Reflection.BindingFlags]'Public,Static')
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$window = [Activator]::CreateInstance($windowType, @($true))
$stateField.GetValue($window).Visual.ThemeName = $InitialTheme
$buildShell.Invoke($window, @()) | Out-Null
$window.Width = 1200
$window.Height = 760
$window.ShowActivated = $false
$window.ShowInTaskbar = $false
$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
$window.Left = -32000
$window.Top = -32000
$window.Show()

foreach ($width in @(720, 900, 1200)) {
	$window.Width = $width
	$window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
	$window.UpdateLayout()
	$mode = [string]$modeField.GetValue($window)
	$expectedMode = [string]$modeResolver.Invoke($null, @([double]$width))
	if ($mode -ne $expectedMode) { throw "Resize to $width selected '$mode'; expected '$expectedMode'." }

    $pixelWidth = [int][Math]::Ceiling($window.ActualWidth)
    $pixelHeight = [int][Math]::Ceiling($window.ActualHeight)
    if ($pixelWidth -lt 1 -or $pixelHeight -lt 1) { throw "Window did not arrange at width $width." }
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($pixelWidth, $pixelHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($window)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $path = Join-Path $OutputDirectory ("home-{0}-{1}.png" -f $width, $mode)
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
	[pscustomobject]@{ Width = $width; Mode = $mode; Rendered = "$pixelWidth x $pixelHeight"; Screenshot = $path }
}

	$themeNames = $assembly.GetType('NovaVpn.ThemePalette').GetField('ThemeNames', [Reflection.BindingFlags]'Public,Static').GetValue($null)
foreach ($theme in $themeNames) {
	$visual = $stateField.GetValue($window).Visual
	$visual.LoadThemeSettings($theme)
	$buildShell.Invoke($window, @()) | Out-Null
	$navigate = $windowType.GetMethod('Navigate', $flags)
	$navigate.Invoke($window, @('home', $false)) | Out-Null
	$window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
	$window.UpdateLayout()
	$pixelWidth = [int][Math]::Ceiling($window.ActualWidth)
	$pixelHeight = [int][Math]::Ceiling($window.ActualHeight)
	$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($pixelWidth, $pixelHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
	$bitmap.Render($window)
	$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
	$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
	$themeSlug = $theme -replace '[^A-Za-z0-9]+', '-'
	$path = Join-Path $OutputDirectory ("home-1200-{0}.png" -f $themeSlug.Trim('-'))
	$stream = [IO.File]::Open($path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
	try { $encoder.Save($stream) } finally { $stream.Dispose() }
	[pscustomobject]@{ Width = 1200; Mode = 'Wide'; Theme = $theme; Rendered = "$pixelWidth x $pixelHeight"; Screenshot = $path }
	foreach ($pageName in @('servers', 'routing', 'diagnostics', 'settings', 'learning')) {
		$navigate.Invoke($window, @($pageName, $false)) | Out-Null
		$window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
		$window.UpdateLayout()
		$pixelWidth = [int][Math]::Ceiling($window.ActualWidth)
		$pixelHeight = [int][Math]::Ceiling($window.ActualHeight)
		$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($pixelWidth, $pixelHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
		$bitmap.Render($window)
		$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
		$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
		$path = Join-Path $OutputDirectory ("page-{0}-{1}.png" -f $pageName, $themeSlug.Trim('-'))
		$stream = [IO.File]::Open($path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
		try { $encoder.Save($stream) } finally { $stream.Dispose() }
		[pscustomobject]@{ Width = [int]$window.ActualWidth; Page = $pageName; Theme = $theme; Screenshot = $path }
	}
}

$appearanceType = $assembly.GetType('NovaVpn.AppearanceWindow')
$stateField.GetValue($window).Visual.LoadThemeSettings($InitialTheme)
$appearance = [Activator]::CreateInstance($appearanceType, @($stateField.GetValue($window).Visual))
$appearance.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
$appearance.Left = -32000
$appearance.Top = -32000
$appearance.Show()
$appearance.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
$appearance.UpdateLayout()
$appearanceBitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap([int][Math]::Ceiling($appearance.ActualWidth), [int][Math]::Ceiling($appearance.ActualHeight), 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$appearanceBitmap.Render($appearance)
$appearanceEncoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$appearanceEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($appearanceBitmap))
$appearancePath = Join-Path $OutputDirectory 'appearance-Windows-11-Light.png'
$appearanceStream = [IO.File]::Open($appearancePath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try { $appearanceEncoder.Save($appearanceStream) } finally { $appearanceStream.Dispose() }
[pscustomobject]@{ Page = 'appearance'; Theme = 'Windows 11 Light'; Screenshot = $appearancePath }
$appearance.Close()

$stateField.GetValue($window).Visual.LoadThemeSettings($InitialTheme)
$stateField.GetValue($window).Visual.TextScale = 1.35
$stateField.GetValue($window).Visual.HighContrast = $true
$window.Width = 720
$window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
$window.UpdateLayout()
$mode = [string]$modeField.GetValue($window)
if ($mode -ne 'Compact') { throw "Large-text preview did not switch to Compact layout: $mode." }
$pixelWidth = [int][Math]::Ceiling($window.ActualWidth)
$pixelHeight = [int][Math]::Ceiling($window.ActualHeight)
$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($pixelWidth, $pixelHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($window)
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$path = Join-Path $OutputDirectory 'home-720-LargeText-HighContrast.png'
$stream = [IO.File]::Open($path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
[pscustomobject]@{ Width = 720; Mode = $mode; Theme = $InitialTheme; TextScale = 1.35; HighContrast = $true; Rendered = "$pixelWidth x $pixelHeight"; Screenshot = $path }

$window.Close()
if ($application) { $application.Shutdown() }

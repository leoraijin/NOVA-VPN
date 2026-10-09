param([Parameter(Mandatory = $true)][string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$profileType = $assembly.GetType('NovaVpn.VpnProfile', $true)
$service = $assembly.GetType('NovaVpn.ServerRegionMapService', $true)
$paletteType = $assembly.GetType('NovaVpn.ThemePalette', $true)

function New-Profile([string]$name) {
    $profile = [Activator]::CreateInstance($profileType)
    $profileType.GetField('Name').SetValue($profile, $name)
    return $profile
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERT FAILED: $message" }
}

$resolve = $service.GetMethod('Resolve')
$render = $service.GetMethod('BuildCanvas')
$colors = [System.Windows.Media.Color]::FromRgb(210,170,120)
$selected = [System.Windows.Media.Color]::FromRgb(185,80,20)
$outline = [System.Windows.Media.Color]::FromRgb(70,50,35)
$marker = [System.Windows.Media.Color]::FromRgb(230,105,34)

$frankfurt = $resolve.Invoke($null, @((New-Profile 'DE Germany - Frankfurt')))
Assert-True ($frankfurt.IsoCode -eq 'DE') 'country prefix DE must resolve to Germany'
Assert-True ($frankfurt.HasCountry) 'recognized country must expose a region'
Assert-True ($frankfurt.HasCityCoordinates) 'Frankfurt profile name must resolve a real city coordinate'
Assert-True (-not [string]::IsNullOrWhiteSpace($frankfurt.CityName)) 'city label must be available for the map caption'
$frankfurtCanvas = $render.Invoke($null, @($frankfurt, [double]250, [double]132, $colors, $selected, $outline, $marker))
Assert-True ($frankfurtCanvas.Tag -eq 'offline-geographic-map') 'map must render from the bundled offline dataset'
$frankfurtShapes = @($frankfurtCanvas.Children | Where-Object { $_ -is [System.Windows.Shapes.Path] })
Assert-True ($frankfurtShapes.Count -gt 2) 'regional map must include real country outlines'
Assert-True (@($frankfurtShapes | Where-Object { $_.Tag -eq 'DE' }).Count -eq 1) 'the selected country must be highlighted on the map'
Assert-True (@($frankfurtCanvas.Children | Where-Object { $_.Tag -eq 'server-city-marker' }).Count -eq 1) 'city marker must use the recognized server city coordinate'

$belgium = $resolve.Invoke($null, @((New-Profile 'BE Belgium - Brussels')))
Assert-True ($belgium.IsoCode -eq 'BE' -and $belgium.HasCityCoordinates) 'a second country and city must resolve independently'

$europe = $resolve.Invoke($null, @((New-Profile 'EU Europe')))
Assert-True ($europe.IsEuropeRegion) 'EU profiles must be represented as the European region'
$europeCanvas = $render.Invoke($null, @($europe, [double]250, [double]132, $colors, $selected, $outline, $marker))
$europeHighlights = @($europeCanvas.Children | Where-Object { $_ -is [System.Windows.Shapes.Path] -and $_.Stroke.Color -eq $marker })
Assert-True ($europeHighlights.Count -gt 20) 'the Europe region must highlight actual European country shapes'

$unknown = $resolve.Invoke($null, @((New-Profile 'Custom profile')))
Assert-True (-not $unknown.HasCountry -and -not $unknown.IsEuropeRegion) 'unknown profile location must not be guessed'
$worldCanvas = $render.Invoke($null, @($unknown, [double]250, [double]132, $colors, $selected, $outline, $marker))
Assert-True (@($worldCanvas.Children | Where-Object { $_ -is [System.Windows.Shapes.Path] }).Count -gt 100) 'unknown location must still show the real world map without a guessed marker'

$themedRender = $service.GetMethod('BuildThemedCanvas')
$themes = $paletteType.GetField('ThemeNames').GetValue($null)
foreach ($themeName in $themes) {
	# Use the real stock palette for each appearance rather than recoloring one theme's bitmap.
    $visual = [Activator]::CreateInstance($assembly.GetType('NovaVpn.VisualPreferences', $true))
    $visual.GetType().GetField('ThemeName').SetValue($visual, $themeName)
    $palette = $paletteType.GetMethod('Create').Invoke($null, @($visual))
    $canvas = $themedRender.Invoke($null, @($frankfurt, [double]250, [double]132, $palette))
    $countryShape = @($canvas.Children | Where-Object { $_ -is [System.Windows.Shapes.Path] -and $_.Tag -eq 'DE' })[0]
    Assert-True ($countryShape.Fill.Color.R -eq $palette.Accent.R -and $countryShape.Fill.Color.G -eq $palette.Accent.G -and $countryShape.Fill.Color.B -eq $palette.Accent.B) "selected country must use the stock accent in $themeName"
    $cityMarker = @($canvas.Children | Where-Object { $_.Tag -eq 'server-city-marker' })[0]
    Assert-True ($cityMarker.Fill.Color -eq $palette.AccentLight) "server marker must use the theme accent highlight in $themeName"
}

Write-Output 'Server region map tests passed: geographic rendering, unknown locations, and all five stock theme palettes.'

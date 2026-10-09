param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if ($null -eq [System.Windows.Application]::Current) { $testApplication = New-Object System.Windows.Application }
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[System.Windows.Application]::ResourceAssembly = $assembly
$type = $assembly.GetType('NovaVpn.MainWindow')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$window = [Activator]::CreateInstance($type, @($true))
function Field($name) { $type.GetField($name, $flags).GetValue($window) }
function Invoke-Window($name) { $type.GetMethod($name, $flags).Invoke($window, @()) | Out-Null }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Find-Sliders($element) {
    $found = @()
    if ($element -is [System.Windows.Controls.Slider]) { $found += $element }
    if ($element -is [System.Windows.Controls.Panel]) { foreach ($child in $element.Children) { $found += Find-Sliders $child } }
    elseif ($element -is [System.Windows.Controls.Border] -and $element.Child) { $found += Find-Sliders $element.Child }
    elseif ($element -is [System.Windows.Controls.ContentControl] -and $element.Content -is [System.Windows.UIElement]) { $found += Find-Sliders $element.Content }
    elseif ($element -is [System.Windows.Controls.Decorator] -and $element.Child) { $found += Find-Sliders $element.Child }
    return $found
}
try {
    $modeResolver = $type.GetMethod('ResponsiveModeForWidth', [Reflection.BindingFlags]'Public,Static')
    Assert ($modeResolver.Invoke($null, @([double]720)) -eq 'Compact') 'small window should use the compact layout'
    Assert ($modeResolver.Invoke($null, @([double]900)) -eq 'Balanced') 'medium window should use the balanced layout'
    Assert ($modeResolver.Invoke($null, @([double]1200)) -eq 'Wide') 'large window should use the wide layout'

    $preferencesType = $assembly.GetType('NovaVpn.VisualPreferences')
    Assert ($assembly.GetName().Version.ToString() -eq '2.3.15.0') 'built assembly version should match the release'
    $preferences = [Activator]::CreateInstance($preferencesType)
    Assert ($preferences.ThemeName -eq 'Windows 11 Light') 'fresh visual preferences should use Windows 11 Light'
    $paletteType = $assembly.GetType('NovaVpn.ThemePalette')
    $themeNames = $paletteType.GetField('ThemeNames', [Reflection.BindingFlags]'Public,Static').GetValue($null)
    $glassName = $paletteType.GetField('IosLiquidGlass', [Reflection.BindingFlags]'Public,Static').GetRawConstantValue()
    Assert ($themeNames.Count -eq 5 -and ($themeNames -contains $glassName) -and ($themeNames -contains 'Amber Glass') -and ($themeNames -contains 'One UI 8.0') -and ($themeNames -contains 'Windows 11 Light') -and ($themeNames -contains 'Windows 11 Dark')) 'five named appearance profiles should remain selectable'
    foreach ($themeName in $themeNames) {
        $profilePreferences = [Activator]::CreateInstance($preferencesType)
        $profilePreferences.ThemeName = $themeName
        $profilePalette = $paletteType.GetMethod('Create').Invoke($null, @($profilePreferences))
        Assert ($profilePalette.Name -eq $themeName) "profile palette should resolve $themeName"
        Assert (($themeName -eq 'Windows 11 Dark') -eq (-not $profilePalette.IsLight)) "profile light/dark classification should match $themeName"
    }
    $preferences.ThemeName = 'NOVA Fluent'
    $preferences.AccentHex = '#7252A8'
    $preferences.CornerRadius = 27
    $preferences.AnimationSpeed = 1.35
    $themeOverrideType = $assembly.GetType('NovaVpn.VisualThemeOverrides')
    $darkOverride = [Activator]::CreateInstance($themeOverrideType)
    $darkOverride.AccentHex = '#35734B'
    $darkOverride.CornerRadius = 20
    $preferences.ThemeOverrides['Windows 11 Dark'] = $darkOverride
    $stateStoreType = $assembly.GetType('NovaVpn.StateStore')
    $appStateType = $assembly.GetType('NovaVpn.AppState')
    $migrationState = [Activator]::CreateInstance($appStateType)
    $migrationState.Visual = $preferences
    $stateStoreType.GetMethod('NormalizeForSerialization').Invoke($null, @($migrationState)) | Out-Null
    Assert ($migrationState.Visual.ThemeName -eq 'Windows 11 Light' -and $migrationState.Visual.AccentHex -eq '') 'legacy Fluent settings should migrate and clear unsupported color overrides'
    Assert ($migrationState.Visual.CornerRadius -eq 27 -and $migrationState.Visual.AnimationSpeed -eq 1.35) 'normalization should preserve valid personalized geometry and animation speed'
    Assert ($migrationState.Visual.ThemeOverrides['Windows 11 Dark'].AccentHex -eq '') 'normalization should clear color overrides in inactive profiles too'
    Assert ($migrationState.Theme -eq 'Windows 11 Light') 'legacy state theme field should follow the normalized profile'
    $fluent = $paletteType.GetMethod('Create').Invoke($null, @($migrationState.Visual))
    Assert ($fluent.IsLight -and $fluent.Bg.R -gt 200 -and $fluent.Text.R -lt 80) 'Windows 11 Light must use light surfaces and readable dark text'
    $appearanceType = $assembly.GetType('NovaVpn.AppearanceWindow')
    $appearance = [Activator]::CreateInstance($appearanceType, @($migrationState.Visual))
    Assert ($appearance.Width -le [System.Windows.SystemParameters]::WorkArea.Width) 'personalization window must fit the available screen width'
    Assert ($appearance.Height -le [System.Windows.SystemParameters]::WorkArea.Height) 'personalization window must fit the available screen height'
    Assert ($appearance.MinWidth -le $appearance.Width -and $appearance.MinHeight -le $appearance.Height) 'personalization minimums must remain reachable'
	$selectorMethod = $appearanceType.GetMethod('ThemeSelectionRow', [Reflection.BindingFlags]'Instance,NonPublic')
	$selector = $selectorMethod.Invoke($appearance, @())
	Assert ($selector.Children.Count -eq 2 -and $selector.Children[0].Children.Count -eq 5) 'personalization should expose five profile cards and a profile reset action'
    $switchTheme = $appearanceType.GetMethod('SwitchTheme', [Reflection.BindingFlags]'Instance,NonPublic')
    $switchTheme.Invoke($appearance, @('Windows 11 Dark'))
    Assert ($appearance.Result.ThemeName -eq 'Windows 11 Dark' -and $appearance.Result.AccentHex -eq '') 'switching profile should retain its stock colors'
    $switchTheme.Invoke($appearance, @('Windows 11 Light'))
    Assert ($appearance.Result.AccentHex -eq '' -and $appearance.Result.CornerRadius -eq 27) 'switching back should preserve geometry without restoring custom colors'
    Assert ($migrationState.Visual.ThemeName -eq 'Windows 11 Light' -and $migrationState.Visual.AccentHex -eq '') 'editing the appearance draft should not mutate live application settings'
    $bodyField = $appearanceType.GetField('body', [Reflection.BindingFlags]'Instance,NonPublic')
    $bodyBeforeSlider = $bodyField.GetValue($appearance)
    $previewBeforeSlider = $appearanceType.GetField('previewCard', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($appearance)
    $firstSlider = @(Find-Sliders $bodyBeforeSlider)[0]
    Assert ($null -ne $firstSlider) 'personalization sliders should be created'
    $firstSlider.Value = 101
    Assert ([object]::ReferenceEquals($bodyBeforeSlider, $bodyField.GetValue($appearance))) 'moving a slider should not rebuild the personalization form'
    Assert ([object]::ReferenceEquals($previewBeforeSlider, $appearanceType.GetField('previewCard', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($appearance))) 'moving a slider should update the existing preview card'
    Assert ($appearance.Result.TextScale -eq 1.01) 'slider changes should update the draft immediately'
    Assert ($migrationState.Visual.TextScale -eq 1.0) 'slider preview must not change persisted application preferences'
    $appearance.Close()

    $state = Field 'state'
    $touchPrimary = $type.GetMethod('PrimaryButton', $flags).Invoke($window, @('Сенсорная кнопка', [double]140))
    $touchSmall = $type.GetMethod('SmallButton', $flags).Invoke($window, @('Действие'))
    $touchIcon = $type.GetMethod('VectorIconButton', $flags).Invoke($window, @('settings', 'Настройки', $null, $false))
    $touchToggle = $type.GetMethod('ToggleButton', $flags).Invoke($window, @($false))
    $touchCaption = $type.GetMethod('WindowButton', $flags).Invoke($window, @('×'))
    Assert ($touchPrimary.Height -ge 48 -and $touchSmall.Height -ge 48) 'main actions must have at least a 48-DIP touch height'
    Assert ($touchIcon.Width -ge 48 -and $touchIcon.Height -ge 48) 'icon actions must have at least a 48-DIP touch target'
    Assert ($touchToggle.Width -ge 64 -and $touchToggle.Height -ge 48) 'switch controls must expose a large finger hit area'
    Assert ($touchCaption.Width -ge 48 -and $touchCaption.Height -ge 48) 'window caption actions must have touch-sized targets'
    $touchNav = $type.GetMethod('Nav', $flags).Invoke($window, @('touch-test', 'home', 'Главная'))
    Assert ($touchNav.Height -ge 52) 'navigation destinations must have touch-sized targets'
    $touchScrollFlags = [Reflection.BindingFlags]'Static,NonPublic'
    $touchScroll = $type.GetMethod('TouchScrollViewer', $touchScrollFlags).Invoke($null, @([System.Windows.Controls.ScrollViewer]::new()))
    Assert ([System.Windows.Controls.ScrollViewer]::GetPanningMode($touchScroll) -eq [System.Windows.Controls.PanningMode]::VerticalOnly) 'page scroll viewers must support vertical touch panning'
    $state.ShowAnimations = $true
    $state.Visual.FollowSystemMotion = $false
    $type.GetField('snapshotMode', $flags).SetValue($window, $false)
    Assert ($type.GetProperty('MotionAllowed', $flags).GetValue($window)) 'Test must exercise enabled animations'
    $core = Field 'core'
    foreach ($statusName in @('Connecting','Connected','Error','Disconnected')) {
        $status = [Enum]::Parse($assembly.GetType('NovaVpn.CoreStatus'), $statusName)
        $core.GetType().GetProperty('Status').SetValue($core, $status)
        Invoke-Window 'RefreshCurrentPage'
        $page = (Field 'contentHost').Children[0]
        $source = [System.Windows.DependencyPropertyHelper]::GetValueSource($page, [System.Windows.UIElement]::OpacityProperty)
        Assert (-not $source.IsAnimated -and $page.Opacity -eq 1) "Page fades during $statusName"
    }
    $page = (Field 'contentHost').Children[0]
    $button = Field 'connectButton'
    foreach ($latency in @(20,35,48)) {
        $core.Verification.DnsOk = $true
        $core.Verification.InternetOk = $true
        $core.Verification.InternetLatencyMs = $latency
        Invoke-Window 'UpdateVerificationDisplay'
        Assert ([object]::ReferenceEquals($page, (Field 'contentHost').Children[0])) 'Verification rebuilt the page'
        Assert ([object]::ReferenceEquals($button, (Field 'connectButton'))) 'Verification replaced the connection button'
        Assert ((Field 'protectionIndicators').Children[2].Child.Text.Contains([string]$latency)) 'Latency was not updated'
    }

    $modeField = $type.GetField('responsiveMode', $flags)
    $buildHome = $type.GetMethod('BuildHomePage', $flags)
    foreach ($case in @(@('Compact',1),@('Balanced',2),@('Wide',3))) {
        $modeField.SetValue($window, $case[0])
        $responsivePage = $buildHome.Invoke($window, @())
        Assert ($responsivePage -is [System.Windows.Controls.ScrollViewer]) "home should build in $($case[0]) layout"
        $hero = @($responsivePage.Content.Children | Where-Object { [System.Windows.Controls.Grid]::GetRow($_) -eq 2 })[0]
        Assert ($hero.Child.ColumnDefinitions.Count -eq $case[1]) "home hero should use $($case[1]) columns in $($case[0]) layout"
        if ($case[0] -eq 'Compact') {
            $summary = $hero.Child.Children[1]
            Assert ($summary -is [System.Windows.Controls.Grid] -and $summary.ColumnDefinitions.Count -eq 2) 'compact connection button and status must share a row'
        }
    }
    $state.Visual.HighContrast = $false
    $insightsMethod=$type.GetMethod('BuildHomeInsights',$flags)
    $state.Visual.HiddenHomeCards.Clear()
    foreach($key in @('Speed','Protocol','Latency','Session','Privacy')) {$state.Visual.HiddenHomeCards.Add($key)}
    $state.Visual.ShowLiveGraph=$false
    $insights=$insightsMethod.Invoke($window,@($null))
    Assert ($insights.Children.Count -eq 0) 'Hiding every home card must not reintroduce the privacy card'
    $state.Visual.ShowLiveGraph=$true
    $insights=$insightsMethod.Invoke($window,@($null))
    Assert ($insights.Children.Count -eq 1) 'Live graph may remain visible with all metric cards hidden'
    $state.Visual.HiddenHomeCards.Clear()
    $surface = $type.GetMethod('SoftSurface', $flags).Invoke($window, @($true))
    Assert ($surface.IsFrozen) 'decorative gradient should be frozen to avoid mutable rendering resources'
    $state.Visual.HighContrast = $true
    $surface = $type.GetMethod('SoftSurface', $flags).Invoke($window, @($true))
    Assert ($surface -is [System.Windows.Media.SolidColorBrush]) 'high contrast must not use decorative gradients'
    $state.Visual.ThemeName = 'Windows 11 Light'
    $state.Visual.HighContrast = $false
    Invoke-Window 'BuildShell'
    $state.Visual.ReduceMotion = $true
    Assert (-not $type.GetProperty('MotionAllowed', $flags).GetValue($window)) 'reduced motion should disable application transitions'
    $connection = Field 'connectionControl'
    $connection.Apply($state.Visual, $core.Status, $true)
    Assert (-not $connection.IsAnimating) 'reduced motion should stop connection animation clocks'
    $state.Visual.ReduceMotion = $false
    $buttonBefore = $connection.ActionButton
    foreach ($statusName in @('Connecting','Connected','Error','Disconnected')) {
        $core.GetType().GetProperty('Status').SetValue($core, [Enum]::Parse($assembly.GetType('NovaVpn.CoreStatus'), $statusName))
        Invoke-Window 'UpdateHomeConnectionStatus'
        Assert ([object]::ReferenceEquals($connection, (Field 'connectionControl'))) 'status changes should retain the connection component'
        Assert ([object]::ReferenceEquals($buttonBefore, (Field 'connectButton'))) 'status changes should retain the action button'
    }
    $frameAnimation = [System.Windows.Media.Animation.DoubleAnimation]::new(0.0, 1.0, [TimeSpan]::FromMilliseconds(100))
    $scaleTransform = [System.Windows.Media.ScaleTransform]::new()
    $frameHelper = $type.GetMethod('BeginMotionAnimation', [Reflection.BindingFlags]'Instance,NonPublic')
    $state.Visual.AnimationFrameRate = 120
    $frameHelper.Invoke($window, @($scaleTransform, [System.Windows.Media.ScaleTransform]::ScaleXProperty, $frameAnimation)) | Out-Null
    Assert ([System.Windows.Media.Animation.Timeline]::GetDesiredFrameRate($frameAnimation) -eq 120) 'all configured visual animations should request up to 120 FPS'
    $frameAnimation60 = [System.Windows.Media.Animation.DoubleAnimation]::new(0.0, 1.0, [TimeSpan]::FromMilliseconds(100))
    $state.Visual.AnimationFrameRate = 60
    $frameHelper.Invoke($window, @($scaleTransform, [System.Windows.Media.ScaleTransform]::ScaleYProperty, $frameAnimation60)) | Out-Null
    Assert ([System.Windows.Media.Animation.Timeline]::GetDesiredFrameRate($frameAnimation60) -eq 60) '60 FPS preference should configure animation sampling'
    $frameAnimationAuto = [System.Windows.Media.Animation.DoubleAnimation]::new(0.0, 1.0, [TimeSpan]::FromMilliseconds(100))
    $state.Visual.AnimationFrameRate = 0
    $frameHelper.Invoke($window, @($scaleTransform, [System.Windows.Media.ScaleTransform]::ScaleYProperty, $frameAnimationAuto)) | Out-Null
    Assert ($null -eq [System.Windows.Media.Animation.Timeline]::GetDesiredFrameRate($frameAnimationAuto)) 'Auto FPS should leave animation sampling to WPF'
    $selector = New-Object System.Windows.Controls.ComboBox
    $selector.SetResourceReference([System.Windows.FrameworkElement]::StyleProperty, 'NovaSelectorStyle')
    foreach($scale in @(0.85,1.35)) {
        $state.Visual.TextScale=$scale
        $state.Visual.CornerRadius=if($scale -lt 1){8}else{32}
        Invoke-Window 'BuildShell'
        (Field 'contentHost').Children.Add($selector) | Out-Null
        $selector.Style=[System.Windows.Application]::Current.Resources['NovaSelectorStyle']
        $selector.ApplyTemplate() | Out-Null
        $window.UpdateLayout()
        Assert ([Math]::Abs($selector.FontSize-12*$scale) -lt .001) 'Selector font must follow text-scale preference'
        $radius=[System.Windows.Application]::Current.Resources['NovaControlRadius']
        Assert ([Math]::Abs($radius.TopLeft-[Math]::Min(7.0,$state.Visual.CornerRadius*.35)) -lt .001) ('Selector must retain lightly rounded rectangular shape while respecting corner preference: actual '+$radius.TopLeft+' corner '+$state.Visual.CornerRadius)
        (Field 'contentHost').Children.Remove($selector)
    }
    Write-Output 'PASS: touch targets and touch scrolling, five theme profiles, saved per-theme customizations, isolated/coalesced preview updates, responsive layouts, motion accessibility, configurable frame rate, stable connection visuals and selector personalization validated.'
} finally {
    $type.GetField('snapshotMode', $flags).SetValue($window, $true)
    $window.Close()
}

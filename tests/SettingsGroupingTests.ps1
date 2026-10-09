param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$app=New-Object System.Windows.Application
$app.ShutdownMode=[System.Windows.ShutdownMode]::OnExplicitShutdown
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly=$assembly
$type=$assembly.GetType('NovaVpn.MainWindow');$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type,@($true))
try {
    $state=$type.GetField('state',$flags).GetValue($window)
    $state.Visual.LoadThemeSettings('One UI 8.0')
    $type.GetMethod('ApplyVisualPalette',$flags).Invoke($window,@())|Out-Null
    $panel=New-Object Windows.Controls.StackPanel
    $title=New-Object Windows.Controls.TextBlock;$title.Text='Group A';$panel.Children.Add($title)|Out-Null
    $cards=@();$buttons=@()
    for($i=0;$i -lt 3;$i++) {
        $button=New-Object Windows.Controls.Button;$button.Content='Setting '+$i
        $card=New-Object Windows.Controls.Border;$card.Child=$button
        $cards+=$card;$buttons+=$button;$panel.Children.Add($card)|Out-Null
    }
    $next=New-Object Windows.Controls.TextBlock;$next.Text='Group B';$panel.Children.Add($next)|Out-Null
    $type.GetMethod('GroupSettingsCards',$flags).Invoke($window,@($panel.PSObject.BaseObject))|Out-Null
    if($panel.Children.Count -ne 3 -or -not [object]::ReferenceEquals($panel.Children[0],$title) -or -not [object]::ReferenceEquals($panel.Children[2],$next)){throw 'Grouping changed section order'}
    $rows=$panel.Children[1].Child
    for($i=0;$i -lt 3;$i++) {
        if(-not [object]::ReferenceEquals($rows.Children[$i],$cards[$i]) -or -not [object]::ReferenceEquals($rows.Children[$i].Child,$buttons[$i])){throw 'Grouping recreated or reordered controls, risking lost handlers'}
        if($cards[$i].BorderThickness.Bottom -ne $(if($i -lt 2){1}else{0})){throw 'Incorrect group divider'}
    }
    if($cards[0].CornerRadius.TopLeft -ne 16 -or $cards[1].CornerRadius.TopLeft -ne 0 -or $cards[2].CornerRadius.BottomLeft -ne 16){throw 'Group geometry is inconsistent'}
    Write-Output 'PASS: One UI settings grouping preserves original control instances, order and section boundaries; contiguous dividers and outer corners validated.'
}finally{$window.Close()}

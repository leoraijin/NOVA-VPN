param([Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Runtime.Serialization
$app = New-Object Windows.Application
$app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
[Windows.Application]::ResourceAssembly = $assembly
$windowType = $assembly.GetType('NovaVpn.MainWindow', $true)
$entryType = $assembly.GetType('NovaVpn.ReleaseNoteEntry', $true)
$owner = [Activator]::CreateInstance($windowType, [object[]]@($true))
$owner.Left = -32000
$owner.Top = -32000
$owner.ShowInTaskbar = $false
$owner.Show()
try {
    $entryCtor = $entryType.GetConstructor([type[]]@([Version], [string[]]))
    $entry = $entryCtor.Invoke([object[]]@([Version]'2.3.13', [string[]]@('Test release summary.')))
    $listType = [System.Collections.Generic.List``1].MakeGenericType(@($entryType))
    $entries = [Activator]::CreateInstance($listType)
    $listType.GetMethod('Add').Invoke($entries, @($entry))
    $method = $windowType.GetMethod('ShowReleaseNotesPrompt', [Reflection.BindingFlags]'Instance,NonPublic')

    foreach ($case in @(@{Index=0; Expected=$false}, @{Index=1; Expected=$true})) {
        $targetIndex = $case.Index
        $timer = New-Object Windows.Threading.DispatcherTimer
        $timer.Interval = [TimeSpan]::FromMilliseconds(100)
        $timer.add_Tick({
            $timer.Stop()
            $dialog = @($app.Windows | Where-Object { $_.Owner -eq $owner })[0]
            $actions = $dialog.Content.Children[2].Children[1]
            $actions.Children[$targetIndex].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        })
        $timer.Start()
        $openedSettings = $method.Invoke($owner, [object[]]@($entries, [Version]'2.3.13'))
        if ($openedSettings -ne $case.Expected) { throw 'Release summary dialog returned the wrong action.' }
    }

    Write-Output 'PASS: release summary renders; both dialog actions work. No VPN or network operation occurred.'
} finally {
    $owner.Close()
    $app.Shutdown()
}

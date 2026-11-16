param(
    [Parameter(Mandatory)][string]$BaselineInstaller,
    [Parameter(Mandatory)][string]$CandidateInstaller
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This integration test requires a disposable GitHub-hosted Windows runner.'
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Use Windows PowerShell with -STA.'
}
Add-Type -AssemblyName System.Windows.Forms
$target = Join-Path $env:ProgramW6432 'NOVA VPN'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NOVA VPN'
$profile = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'NovaVPN'
if ((Test-Path $target) -or (Test-Path $key) -or (Test-Path $profile)) {
    throw 'Expected a clean runner with no NOVA VPN state.'
}

function Install-Package([string]$Path, [string]$UpdatePath) {
    $assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Path).Path)
    $version = $assembly.GetName().Version.ToString(3)
    $formType = $assembly.GetType('NovaInstaller', $true)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $constructor = $formType.GetConstructor($flags, $null, [type[]]@([string], [int], [bool]), $null)
    $form = $constructor.Invoke([object[]]@($UpdatePath, 0, $false))
    try {
        $options = [Activator]::CreateInstance($assembly.GetType('InstallOptions', $true), $true)
        $options.Target = $target
        $options.DesktopShortcut = $false
        $worker = $formType.GetField('worker', $flags).GetValue($form)
        $result = @{ Done = $false; Error = $null }
        $callback = [ComponentModel.RunWorkerCompletedEventHandler]{
            param($sender, $args)
            $result.Error = $args.Error
            $result.Done = $true
        }
        [Threading.SynchronizationContext]::SetSynchronizationContext(
            (New-Object Windows.Forms.WindowsFormsSynchronizationContext))
        $worker.add_RunWorkerCompleted($callback)
        $worker.RunWorkerAsync($options)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (!$result.Done -and $timer.Elapsed.TotalSeconds -lt 120) {
            [Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 25
        }
        if (!$result.Done) { throw "Installer $version timed out." }
        if ($result.Error) { throw $result.Error }
    } finally {
        $form.Dispose()
    }

    $registered = Get-ItemProperty -LiteralPath $key
    if ($registered.DisplayVersion -ne $version -or $registered.InstallLocation.TrimEnd('\') -ne $target) {
        throw "Installer $version did not register the expected version and path."
    }
    $app = Get-Item -LiteralPath (Join-Path $target 'NOVA VPN.exe')
    if (([Version]$app.VersionInfo.FileVersion).ToString(3) -ne $version) {
        throw "The installed executable does not match installer $version."
    }
    foreach ($file in @('core\sing-box.exe', 'core\reality-client.exe', 'core\wintun.dll', 'NOVA VPN Uninstaller.exe', 'install-manifest.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $target $file) -PathType Leaf)) { throw "Missing installed file $file." }
    }
    Write-Host "Verified installer $version against the installed files and machine registration."
}

Install-Package $BaselineInstaller ''
New-Item -ItemType Directory -Path $profile | Out-Null
$sentinel = Join-Path $profile 'installer-upgrade-sentinel.txt'
Set-Content -LiteralPath $sentinel -Value 'preserve user profile' -Encoding ASCII
$before = (Get-FileHash -LiteralPath $sentinel).Hash
Install-Package $CandidateInstaller $target
if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $before) { throw 'Upgrade modified user profile data.' }


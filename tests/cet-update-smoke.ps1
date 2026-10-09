param(
    [Parameter(Mandatory)][string]$CurrentSetupPath,
    [Parameter(Mandatory)][string]$CurrentAppPath,
    [Parameter(Mandatory)][string]$FutureSetupPath,
    [Parameter(Mandatory)][string]$FutureAppPath,
    [string]$ProductionWindowsAssemblyPath = (Join-Path $PSScriptRoot '../src/ResoDrive.Windows/bin/Release/net10.0-windows10.0.17763.0/ResoDrive.Windows.dll'),
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'CET update acceptance requires a disposable GitHub-hosted Windows runner.'
}
. (Join-Path $PSScriptRoot 'installer-ui.ps1')
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ResoDriveCetObserver {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint flags, UIntPtr length);
    public static uint ShadowStackFlags(SafeProcessHandle process) {
        uint flags;
        if (!GetProcessMitigationPolicy(process, 15, out flags, (UIntPtr)4)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return flags;
    }
}
'@
$currentSetup = (Resolve-Path -LiteralPath $CurrentSetupPath).Path
$currentApp = (Resolve-Path -LiteralPath $CurrentAppPath).Path
$futureSetup = (Resolve-Path -LiteralPath $FutureSetupPath).Path
$futureApp = (Resolve-Path -LiteralPath $FutureAppPath).Path
$windowsAssembly = (Resolve-Path -LiteralPath $ProductionWindowsAssemblyPath).Path
$currentVersion = ((Get-Item -LiteralPath $currentApp).VersionInfo.ProductVersion -split '\+', 2)[0]
$futureVersion = ((Get-Item -LiteralPath $futureApp).VersionInfo.ProductVersion -split '\+', 2)[0]
if ([version]$futureVersion -le [version]$currentVersion) { throw 'The upgrade fixture must have a later version.' }
$installDirectory = Join-Path $env:ProgramFiles 'ResoDrive'
$installedApp = Join-Path $installDirectory 'resodrive.exe'
if ((Test-Path -LiteralPath $installedApp) -or (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'rdrive/resodrive.exe'))) {
    throw 'An application installation already exists.'
}
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-cet-update-smoke'
$data = Join-Path $evidence 'data'
$updates = Join-Path $data 'updates'
New-Item -ItemType Directory -Path $updates -Force | Out-Null
$driverOutput = Join-Path $evidence 'launcher'
& $DotnetPath build (Join-Path $PSScriptRoot 'CetUpdateLauncher/CetUpdateLauncher.csproj') -c Release --output $driverOutput "-p:ProductionWindowsAssemblyPath=$windowsAssembly"
if ($LASTEXITCODE -ne 0) { throw 'The test-only production-launch driver did not build.' }
$driver = Join-Path $driverOutput 'CetUpdateLauncher.dll'

function Get-CetPreference {
    $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $installation = $key.OpenSubKey('SOFTWARE\ResoDrive\Installation')
        try {
            if ($null -eq $installation -or $installation.GetValueKind('DisableCet') -ne [Microsoft.Win32.RegistryValueKind]::String) {
                throw 'The installed CET preference is absent or not a string.'
            }
            return [string]$installation.GetValue('DisableCet')
        } finally { if ($installation) { $installation.Dispose() } }
    } finally { $key.Dispose() }
}

function Get-IfeoSnapshot {
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $root = $machine.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\resodrive.exe')
        try {
            if ($null -eq $root) { return 'null' }
            function Read-PolicyKey($Key) {
                $values = [ordered]@{}
                foreach ($name in @($Key.GetValueNames() | Sort-Object)) {
                    if ($name -in @('UseFilter', 'FilterFullPath')) { continue }
                    $value = $Key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    if ($name -in @('MitigationOptions', 'MitigationOptionsMask') -and
                        $value -is [byte[]] -and @($value | Where-Object { $_ -ne 0 }).Count -eq 0) { continue }
                    $values[$name] = [ordered]@{ Kind = [string]$Key.GetValueKind($name); Value = $value }
                }
                $children = [ordered]@{}
                foreach ($name in @($Key.GetSubKeyNames() | Sort-Object)) {
                    $child = $Key.OpenSubKey($name)
                    try {
                        $content = Read-PolicyKey $child
                        if ($null -ne $content) { $children[$name] = $content }
                    } finally { $child.Dispose() }
                }
                # Windows may retain empty filtering metadata after Remove. It has
                # no mitigation effect; retain paths whenever real policy remains.
                if ($values.Count -eq 0 -and $children.Count -eq 0) { return $null }
                foreach ($metadata in @('FilterFullPath', 'UseFilter')) {
                    if ($Key.GetValueNames() -contains $metadata) {
                        $values[$metadata] = [ordered]@{ Kind = [string]$Key.GetValueKind($metadata); Value = $Key.GetValue($metadata) }
                    }
                }
                return [ordered]@{ Values = $values; Children = $children }
            }
            return (ConvertTo-Json -InputObject (Read-PolicyKey $root) -Depth 30 -Compress)
        } finally { if ($root) { $root.Dispose() } }
    } finally { $machine.Dispose() }
}

function Assert-CetMarked([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $sections = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optionalBytes = [BitConverter]::ToUInt16($bytes, $pe + 20)
    $optional = $pe + 24
    if ([BitConverter]::ToUInt16($bytes, $optional) -ne 0x20B) { throw 'Expected an x64 application PE.' }
    $debugRva = [BitConverter]::ToUInt32($bytes, $optional + 112 + 6 * 8)
    $debugBytes = [BitConverter]::ToUInt32($bytes, $optional + 112 + 6 * 8 + 4)
    $debugOffset = 0
    for ($index = 0; $index -lt $sections; $index++) {
        $section = $optional + $optionalBytes + $index * 40
        $rva = [BitConverter]::ToUInt32($bytes, $section + 12)
        $size = [Math]::Max([BitConverter]::ToUInt32($bytes, $section + 8), [BitConverter]::ToUInt32($bytes, $section + 16))
        if ($debugRva -ge $rva -and $debugRva -lt $rva + $size) {
            $debugOffset = [BitConverter]::ToUInt32($bytes, $section + 20) + $debugRva - $rva
            break
        }
    }
    for ($offset = $debugOffset; $debugOffset -gt 0 -and $offset -lt $debugOffset + $debugBytes; $offset += 28) {
        if ([BitConverter]::ToUInt32($bytes, $offset + 12) -eq 20) {
            $payload = [BitConverter]::ToUInt32($bytes, $offset + 24)
            if (([BitConverter]::ToUInt32($bytes, $payload) -band 1) -eq 1) { return }
        }
    }
    throw "The application is not CET marked: $Path"
}

function Start-ProductionChild([string]$Executable, [string]$WorkingDirectory, [string]$Receipt, [string[]]$ChildArguments) {
    if (Test-Path -LiteralPath $Receipt) { Remove-Item -LiteralPath $Receipt }
    $start = [Diagnostics.ProcessStartInfo]::new($DotnetPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @($driver, $installedApp, $Executable, $WorkingDirectory, $Receipt) + $ChildArguments) {
        $start.ArgumentList.Add($argument)
    }
    return [Diagnostics.Process]::Start($start)
}

function Wait-LaunchReceipt([string]$Path, $Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($Process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'The production launcher did not record a child.' }
        Start-Sleep -Milliseconds 50
    }
    $receipt = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $receipt.DisabledForRegisteredInstallation -or $receipt.ShadowStacksEnabled) {
        throw 'The registered compatibility choice did not disable the child shadow stacks.'
    }
    return $receipt
}

$originalPolicy = Get-IfeoSnapshot
$previousData = $env:RDRIVE_DATA_DIR
$previousHandoff = $env:RDRIVE_UPDATE_HANDOFF_DIR
$env:RDRIVE_DATA_DIR = $data
$env:RDRIVE_UPDATE_HANDOFF_DIR = $updates
$parent = $helper = $null
try {
    Assert-CetMarked $currentApp
    Assert-CetMarked $futureApp
    $invalid = Start-Process -FilePath $currentSetup -ArgumentList @('/quiet', '/norestart', 'ResoDriveDisableCet=2', '/log', ('"' + (Join-Path $evidence 'invalid.log') + '"')) -WindowStyle Hidden -Wait -PassThru
    try {
        if ($invalid.ExitCode -eq 0 -or (Test-Path -LiteralPath $installedApp)) { throw 'Setup accepted an invalid CET flag.' }
    } finally { $invalid.Dispose() }
    $setup = Start-Process -FilePath $currentSetup -ArgumentList @('/norestart', 'ResoDriveDisableCet=1', ('ResoDriveDataRoot="' + $data + '"'), '/log', ('"' + (Join-Path $evidence 'fresh.log') + '"')) -WindowStyle Hidden -PassThru
    try {
        Wait-VisibleSetup $setup 'Install' (Join-Path $evidence 'fresh-ui.json')
        if ($setup.ExitCode -ne 0) { throw "Fresh compatibility installation failed: $($setup.ExitCode)." }
    } finally { $setup.Dispose() }
    if ((Get-CetPreference) -ne '1' -or (Get-FileHash -LiteralPath $installedApp).Hash -ne (Get-FileHash -LiteralPath $currentApp).Hash) {
        throw 'Fresh install did not preserve the choice and identical CET-marked application.'
    }
    $repair = Start-Process -FilePath $currentSetup -ArgumentList @('/repair', '/norestart', ('ResoDriveDataRoot="' + $data + '"'), '/log', ('"' + (Join-Path $evidence 'repair.log') + '"')) -WindowStyle Hidden -PassThru
    try {
        Wait-VisibleSetup $repair 'Repair' (Join-Path $evidence 'repair-ui.json')
        if ($repair.ExitCode -ne 0) { throw "Compatibility repair failed: $($repair.ExitCode)." }
    } finally { $repair.Dispose() }
    if ((Get-CetPreference) -ne '1' -or (Get-FileHash -LiteralPath $installedApp).Hash -ne (Get-FileHash -LiteralPath $currentApp).Hash) {
        throw 'Repair changed the choice or application bytes.'
    }
    foreach ($choice in @('0', '1')) {
        $toggle = Start-Process -FilePath $currentSetup -ArgumentList @('/repair', '/norestart', "ResoDriveDisableCet=$choice", ('ResoDriveDataRoot="' + $data + '"'), '/log', ('"' + (Join-Path $evidence "repair-$choice.log") + '"')) -WindowStyle Hidden -PassThru
        try {
            Wait-VisibleSetup $toggle 'Repair' (Join-Path $evidence "repair-$choice-ui.json")
            if ($toggle.ExitCode -ne 0) { throw "Compatibility repair with choice $choice failed: $($toggle.ExitCode)." }
        } finally { $toggle.Dispose() }
        if ((Get-CetPreference) -ne $choice) { throw "Repair did not store explicit CET choice $choice." }
        if ($choice -eq '0' -and (Get-IfeoSnapshot) -cne $originalPolicy) { throw 'Explicit CET re-enable did not restore the original mitigation policy.' }
        if ((Get-FileHash -LiteralPath $installedApp).Hash -ne (Get-FileHash -LiteralPath $currentApp).Hash) {
            throw 'Changing the compatibility choice altered application bytes.'
        }
    }
    $settings = Join-Path $data 'settings.json'
    [IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
    $cache = Join-Path $data 'cache-marker'
    [IO.File]::WriteAllText($cache, 'disposable unsent changes')
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    $cacheHash = (Get-FileHash -LiteralPath $cache).Hash
    $parentReceiptPath = Join-Path $evidence 'parent-launch.json'
    # Normal Windows launch proves the persisted override, independently of the
    # production copied-helper launcher exercised immediately afterward.
    $parent = Start-Process -FilePath $installedApp -WindowStyle Hidden -PassThru
    $parentPolicy = [ResoDriveCetObserver]::ShadowStackFlags($parent.SafeHandle)
    if (($parentPolicy -band 1) -ne 0) { throw 'Normal installed-app launch did not honor the persisted CET override.' }
    $parentReceipt = [ordered]@{ ChildProcessId = $parent.Id; EffectiveShadowStackFlags = $parentPolicy; NormalWindowsLaunch = $true }
    $parentReceipt | ConvertTo-Json | Set-Content -LiteralPath $parentReceiptPath
    Start-Sleep -Seconds 4
    if ($parent.HasExited) { throw 'The installed parent did not stay running.' }
    $helperPath = Join-Path $updates 'resodrive-update-helper.exe'
    Copy-Item -LiteralPath $installedApp -Destination $helperPath
    $staged = Join-Path $updates "resodrive-win-x64-$futureVersion-setup.exe"
    Copy-Item -LiteralPath $futureSetup -Destination $staged
    $outcome = Join-Path $updates 'application-update-result.json'
    $helperReceiptPath = Join-Path $evidence 'helper-launch.json'
    $helper = Start-ProductionChild $helperPath $updates $helperReceiptPath @('--complete-update', $futureVersion, $staged,
        $installedApp, $installedApp, $outcome, (Get-FileHash -LiteralPath $staged).Hash, [string]$parentReceipt.ChildProcessId)
    $helperReceipt = Wait-LaunchReceipt $helperReceiptPath $helper
    $prepare = Start-ProductionChild $installedApp $installDirectory (Join-Path $evidence 'prepare-launch.json') @('--prepare-install', $installDirectory, '2', $data)
    try {
        if (-not $prepare.WaitForExit(60000) -or $prepare.ExitCode -ne 0) { throw 'The installed parent did not close safely.' }
    } finally { $prepare.Dispose() }
    if (-not $parent.WaitForExit(15000)) { throw 'The old parent remains.' }
    Wait-VisibleSetup $helper '' (Join-Path $evidence 'update-ui.json') -Passive
    if ($helper.ExitCode -ne 0) { throw "Actual compatibility updater failed: $($helper.ExitCode)." }
    if ((Get-CetPreference) -ne '1' -or (Get-FileHash -LiteralPath $installedApp).Hash -ne (Get-FileHash -LiteralPath $futureApp).Hash) {
        throw 'The future update changed the choice or installed unexpected application bytes.'
    }
    Assert-CetMarked $installedApp
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or (Get-FileHash -LiteralPath $cache).Hash -ne $cacheHash) {
        throw 'The future update changed disposable settings or cache.'
    }
    $showReceiptPath = Join-Path $evidence 'show-launch.json'
    $show = Start-ProductionChild $installedApp $installDirectory $showReceiptPath @('--show')
    try {
        $showReceipt = Wait-LaunchReceipt $showReceiptPath $show
        if (-not $show.WaitForExit(120000) -or $show.ExitCode -ne 0) { throw 'The reopened app did not acknowledge its ready window.' }
    } finally { $show.Dispose() }
    $installed = @(Get-ChildItem 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall' | Get-ItemProperty |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'ResoDrive' -and $_.DisplayVersion -eq $futureVersion })
    if ($installed.Count -ne 1) { throw 'Expected one visible current MSI entry.' }
    $remove = Start-Process msiexec.exe -ArgumentList @('/x', $installed[0].PSChildName, '/qn', '/norestart', ('RDRIVE_DATA_ROOT="' + $data + '\."'), '/l*v', ('"' + (Join-Path $evidence 'remove.log') + '"')) -WindowStyle Hidden -PassThru -Wait
    try { if ($remove.ExitCode -ne 0) { throw "Compatibility uninstall failed: $($remove.ExitCode)." } } finally { $remove.Dispose() }
    if ((Get-IfeoSnapshot) -cne $originalPolicy) { throw 'Uninstall did not restore the original executable mitigation policy.' }
    [ordered]@{
        CurrentVersion = $currentVersion; FutureVersion = $futureVersion; InvalidFlagRejected = $true; RepairRetainedChoice = $true
        RepairToggledChoice = $true; NormalWindowsLaunchHonoredChoice = $true
        CurrentExeSha256 = (Get-FileHash -LiteralPath $currentApp).Hash; FutureExeSha256 = (Get-FileHash -LiteralPath $futureApp).Hash
        CopiedHelperSha256 = $helperReceipt.ExecutableSha256; ParentShadowStackFlags = $parentReceipt.EffectiveShadowStackFlags
        CopiedHelperShadowStackFlags = $helperReceipt.EffectiveShadowStackFlags; FutureShadowStackFlags = $showReceipt.EffectiveShadowStackFlags
        UpdaterExitCode = $helper.ExitCode; ReadyWindowAcknowledged = $true; SettingsAndCachePreserved = $true
        SavedChoiceSurvivedUpgrade = $true; SameCetMarkedBuild = $true; OriginalPolicyRestored = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
} finally {
    foreach ($file in @(Get-ChildItem -LiteralPath $updates -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.log' -or $_.Name -in @('application-update-result.json', 'application-update-result.json.tmp', 'installer-preparation.json') })) {
        try { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidence $file.Name) -Force }
        catch { Write-Warning "Could not preserve updater diagnostic $($file.Name)." }
    }
    $env:RDRIVE_DATA_DIR = $previousData
    $env:RDRIVE_UPDATE_HANDOFF_DIR = $previousHandoff
    if ($parent) { $parent.Dispose() }
    if ($helper) { $helper.Dispose() }
}

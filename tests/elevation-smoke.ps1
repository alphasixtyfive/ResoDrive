param(
    [Parameter(Mandatory)][string]$PreviousAppPath,
    [Parameter(Mandatory)][string]$NewAppPath,
    [ValidateSet('Host', 'OrphanedUi')][string]$Scenario = 'Host'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test from a normal, unelevated PowerShell session. The helper will request UAC elevation.'
}
$previous = (Resolve-Path -LiteralPath $PreviousAppPath).Path
$helper = (Resolve-Path -LiteralPath $NewAppPath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-elevation-smoke-' + [Guid]::NewGuid().ToString('N'))
$binaries = Join-Path $testRoot 'previous'
$data = Join-Path $testRoot 'data'
New-Item -ItemType Directory -Path $binaries, (Join-Path $data 'cache') -Force | Out-Null
$oldApp = Join-Path $binaries 'resodrive.exe'
Copy-Item -LiteralPath $previous -Destination $oldApp
$settings = Join-Path $data 'settings.json'
$marker = Join-Path $data 'cache\preserve.txt'
[IO.File]::WriteAllText($marker, 'Isolated cache marker. No production data is used.')
if ($Scenario -eq 'Host') {
    [IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
}

function Assert-NoOtherIsolatedProcess([int]$uiProcessId) {
    foreach ($candidate in [Diagnostics.Process]::GetProcessesByName('resodrive')) {
        try {
            if ($candidate.Id -eq $uiProcessId -or $candidate.HasExited) { continue }
            $candidatePath = $candidate.MainModule.FileName
            if ([IO.Path]::GetFullPath($candidatePath).Equals($oldApp, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Another isolated ResoDrive process is running (PID $($candidate.Id)); the UI-only case is not established."
            }
        } finally { $candidate.Dispose() }
    }
}
$resultPath = Join-Path $testRoot 'elevated-result.json'
$scriptPath = Join-Path $testRoot 'elevated-helper.ps1'
@'
param([string]$Helper, [string]$Installation, [string]$Data, [string]$Result,
      [string]$Scenario, [int]$OldProcessId)
$ErrorActionPreference = 'Stop'
$env:RDRIVE_DATA_DIR = $Data
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$hostAbsent = $null
if ($Scenario -eq 'OrphanedUi') {
    $oldApp = Join-Path $Installation 'resodrive.exe'
    $isolated = @(Get-CimInstance Win32_Process -Filter "Name='resodrive.exe'" |
        Where-Object { $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals($oldApp, [StringComparison]::OrdinalIgnoreCase) })
    $hostAbsent = $isolated.Count -eq 1 -and $isolated[0].ProcessId -eq $OldProcessId
    if (-not $hostAbsent) { throw 'The isolated old UI is not the only ResoDrive process immediately before helper launch.' }
}
$process = Start-Process -FilePath $Helper -ArgumentList @('--prepare-install', ('"' + $Installation + '"'), '3', ('"' + $Data + '"')) -PassThru -WindowStyle Hidden
try {
    if (-not $process.WaitForExit(90000)) { throw 'Elevated preparation timed out.' }
    [ordered]@{
        Elevated = ([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        User = $id.User.Value
        Owner = $id.Owner.Value
        ExitCode = $process.ExitCode
        HostAbsentBeforeHelper = $hostAbsent
    } | ConvertTo-Json | Set-Content -LiteralPath $Result
} finally { $process.Dispose() }
'@ | Set-Content -LiteralPath $scriptPath

$start = [Diagnostics.ProcessStartInfo]::new($oldApp)
if ($Scenario -eq 'Host') { $start.ArgumentList.Add('--host') }
$start.UseShellExecute = $false
$start.CreateNoWindow = $Scenario -eq 'Host'
$start.Environment['RDRIVE_DATA_DIR'] = $data
$oldProcess = [Diagnostics.Process]::Start($start)
try {
    if ($Scenario -eq 'OrphanedUi') {
        # With no settings yet, the real 0.3.14 UI waits at its first-run dialog
        # before it can start a host. Create settings only after that dialog opens.
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($oldProcess.HasExited) { throw 'The isolated legacy UI exited before showing its first-run dialog.' }
            $oldProcess.Refresh()
            if ($oldProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($oldProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The isolated legacy UI did not show a window.' }
        Assert-NoOtherIsolatedProcess $oldProcess.Id
        [IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
        Start-Sleep -Seconds 1
        if ($oldProcess.HasExited) { throw 'The isolated legacy UI did not stay running.' }
        Assert-NoOtherIsolatedProcess $oldProcess.Id
    } else {
        Start-Sleep -Seconds 3
        if ($oldProcess.HasExited) { throw 'The isolated legacy host did not stay running.' }
    }
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    $markerHash = (Get-FileHash -LiteralPath $marker).Hash
    $powershell = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-File', ('"' + $scriptPath + '"'), '-Helper', ('"' + $helper + '"'),
        '-Installation', ('"' + $binaries + '"'), '-Data', ('"' + $data + '"'), '-Result', ('"' + $resultPath + '"'),
        '-Scenario', $Scenario, '-OldProcessId', $oldProcess.Id)
    $elevated = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    try {
        if (-not $elevated.WaitForExit(120000)) { throw 'Elevation test timed out. Inspect the preparation window.' }
    } finally { $elevated.Dispose() }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.Elevated -or $result.ExitCode -ne 0 -or
        ($Scenario -eq 'OrphanedUi' -and -not $result.HostAbsentBeforeHelper)) {
        throw "Elevated preparation failed. See $testRoot"
    }
    if (-not $oldProcess.WaitForExit(10000)) { throw "Preparation returned success but left the old $Scenario process running." }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or (Get-FileHash -LiteralPath $marker).Hash -ne $markerHash) {
        throw 'Preparation changed settings or cached data.'
    }
    $report = [ordered]@{
        Succeeded = $true
        Scenario = $Scenario
        PreviousVersion = (Get-Item -LiteralPath $previous).VersionInfo.ProductVersion
        NewVersion = (Get-Item -LiteralPath $helper).VersionInfo.ProductVersion
        HelperSha256 = (Get-FileHash -LiteralPath $helper).Hash
        ElevatedHelper = $result
        LegacyProcessExited = $true
        LegacyHostExited = $Scenario -eq 'Host'
        LegacyUiExited = $Scenario -eq 'OrphanedUi'
        NoHostBeforeElevation = $Scenario -eq 'OrphanedUi'
        SettingsAndCachePreserved = $true
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json')
    Write-Output "Elevation smoke passed. Evidence: $testRoot\result.json"
} finally {
    if (-not $oldProcess.HasExited) { $oldProcess.Kill(); $oldProcess.WaitForExit(10000) | Out-Null }
    $oldProcess.Dispose()
}

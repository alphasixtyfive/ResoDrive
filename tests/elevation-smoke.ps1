param(
    [Parameter(Mandatory)][string]$PreviousAppPath,
    [Parameter(Mandatory)][string]$NewAppPath
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
[IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
$marker = Join-Path $data 'cache\preserve.txt'
[IO.File]::WriteAllText($marker, 'Isolated cache marker. No production data is used.')
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
$markerHash = (Get-FileHash -LiteralPath $marker).Hash
$resultPath = Join-Path $testRoot 'elevated-result.json'
$scriptPath = Join-Path $testRoot 'elevated-helper.ps1'
@'
param([string]$Helper, [string]$Installation, [string]$Data, [string]$Result)
$ErrorActionPreference = 'Stop'
$env:RDRIVE_DATA_DIR = $Data
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$process = Start-Process -FilePath $Helper -ArgumentList @('--prepare-install', ('"' + $Installation + '"'), '3') -PassThru -WindowStyle Hidden
try {
    if (-not $process.WaitForExit(90000)) { throw 'Elevated preparation timed out.' }
    [ordered]@{
        Elevated = ([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        User = $id.User.Value
        Owner = $id.Owner.Value
        ExitCode = $process.ExitCode
    } | ConvertTo-Json | Set-Content -LiteralPath $Result
} finally { $process.Dispose() }
'@ | Set-Content -LiteralPath $scriptPath

$start = [Diagnostics.ProcessStartInfo]::new($oldApp, '--host')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.Environment['RDRIVE_DATA_DIR'] = $data
$oldHost = [Diagnostics.Process]::Start($start)
try {
    Start-Sleep -Seconds 3
    if ($oldHost.HasExited) { throw 'The isolated legacy host did not stay running.' }
    $powershell = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-File', ('"' + $scriptPath + '"'), '-Helper', ('"' + $helper + '"'),
        '-Installation', ('"' + $binaries + '"'), '-Data', ('"' + $data + '"'), '-Result', ('"' + $resultPath + '"'))
    $elevated = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    try {
        if (-not $elevated.WaitForExit(120000)) { throw 'Elevation test timed out. Inspect the preparation window.' }
    } finally { $elevated.Dispose() }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.Elevated -or $result.ExitCode -ne 0) { throw "Elevated preparation failed. See $testRoot" }
    if (-not $oldHost.WaitForExit(10000)) { throw 'Preparation returned success but left the old host running.' }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or (Get-FileHash -LiteralPath $marker).Hash -ne $markerHash) {
        throw 'Preparation changed settings or cached data.'
    }
    $report = [ordered]@{
        Succeeded = $true
        PreviousVersion = (Get-Item -LiteralPath $previous).VersionInfo.ProductVersion
        NewVersion = (Get-Item -LiteralPath $helper).VersionInfo.ProductVersion
        HelperSha256 = (Get-FileHash -LiteralPath $helper).Hash
        ElevatedHelper = $result
        LegacyHostExited = $true
        SettingsAndCachePreserved = $true
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json')
    Write-Output "Elevation smoke passed. Evidence: $testRoot\result.json"
} finally {
    if (-not $oldHost.HasExited) { $oldHost.Kill(); $oldHost.WaitForExit(10000) | Out-Null }
    $oldHost.Dispose()
}

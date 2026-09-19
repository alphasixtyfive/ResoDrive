param([Parameter(Mandatory)][string]$SetupPath, [string]$PreviousVersion = '0.3.6')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This test installs machine-wide software. Never run it on a developer's PC.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer smoke tests require a disposable GitHub-hosted Windows runner.'
}
if ($PreviousVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid previous version.' }
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$app = Join-Path $env:ProgramFiles 'rdrive\resodrive.exe'
if (Test-Path -LiteralPath $app) { throw 'Refusing to overwrite a pre-existing installation.' }
$testRoot = Join-Path $env:RUNNER_TEMP 'resodrive-installer-smoke'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$oldDataRoot = $env:RDRIVE_DATA_DIR
$env:RDRIVE_DATA_DIR = Join-Path $testRoot 'user-data'
New-Item -ItemType Directory -Path $env:RDRIVE_DATA_DIR -Force | Out-Null
$settings = Join-Path $env:RDRIVE_DATA_DIR 'settings.json'
[IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
$cache = Join-Path $env:RDRIVE_DATA_DIR 'cache'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$marker = Join-Path $cache 'preserve-on-uninstall.txt'
[IO.File]::WriteAllText($marker, 'Pending local data must survive installer operations.')
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
$markerHash = (Get-FileHash -LiteralPath $marker).Hash

function Invoke-Installer([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(180000)) { throw "Installer timed out: $Arguments" }
        if ($process.ExitCode -notin @(0,3010)) { throw "Installer failed ($($process.ExitCode)): $Arguments" }
    } finally { $process.Dispose() }
}
function Assert-DataPreserved {
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or
        (Get-FileHash -LiteralPath $marker).Hash -ne $markerHash) { throw 'Installation changed user settings or cached data.' }
}
function Start-TestApplication {
    if (-not (Test-Path -LiteralPath $app)) { throw 'The installed app is missing.' }
    $process = Start-Process -FilePath $app -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) { throw 'The installed application exited unexpectedly.' }
    return $process
}
function Assert-Stopped($Process) {
    try {
        if (-not $Process.WaitForExit(15000)) { throw 'Installer left the old application running.' }
        $remaining = @(Get-Process resodrive -ErrorAction SilentlyContinue | Where-Object Path -EQ $app)
        if ($remaining.Count -gt 0) { throw 'Installer left a background host running.' }
    } finally { $Process.Dispose() }
}

try {
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\fresh.log`""
    $running = Start-TestApplication
    Invoke-Installer $setup "/repair /quiet /norestart /log `"$testRoot\repair.log`""
    Assert-Stopped $running
    Assert-DataPreserved
    $running = Start-TestApplication
    Invoke-Installer $setup "/uninstall /quiet /norestart /log `"$testRoot\uninstall.log`""
    Assert-Stopped $running
    if (Test-Path -LiteralPath $app) { throw 'Uninstall left the application executable.' }
    Assert-DataPreserved

    # Upgrade the preceding public MSI while its application is running.
    $name = "resodrive-win-x64-$PreviousVersion.msi"
    $previous = Join-Path $testRoot $name
    $url = "https://github.com/alphasixtyfive/resodrive/releases/download/v$PreviousVersion/$name"
    Invoke-WebRequest -Uri $url -OutFile $previous
    $checksum = (Invoke-WebRequest -Uri "$url.sha256").Content
    if ($checksum -is [byte[]]) { $checksum = [Text.Encoding]::ASCII.GetString($checksum) }
    $match = [regex]::Match($checksum.Trim(), '\A([0-9a-fA-F]{64})\s+\*?(.+)\z')
    if (-not $match.Success -or $match.Groups[2].Value -cne $name -or
        (Get-FileHash -LiteralPath $previous).Hash -ine $match.Groups[1].Value) { throw 'Previous installer checksum is invalid.' }
    Invoke-Installer 'msiexec.exe' "/i `"$previous`" /quiet /norestart /l*v `"$testRoot\previous.log`""
    $running = Start-TestApplication
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\upgrade.log`""
    Assert-Stopped $running
    $properties = [xml](Get-Content (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw)
    $expectedVersion = $properties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
    if ((Get-Item -LiteralPath $app).VersionInfo.ProductVersion -notlike "$expectedVersion*") { throw 'Upgrade did not install the expected version.' }
    Assert-DataPreserved
    $running = Start-TestApplication
    Invoke-Installer $setup "/uninstall /quiet /norestart /log `"$testRoot\upgrade-uninstall.log`""
    Assert-Stopped $running
    if (Test-Path -LiteralPath $app) { throw 'Upgraded application was not removed.' }
    Assert-DataPreserved
    Write-Output 'Installer smoke passed: fresh install, launch, running-app repair/removal, previous-version upgrade, and user-data preservation.'
} finally {
    $env:RDRIVE_DATA_DIR = $oldDataRoot
}

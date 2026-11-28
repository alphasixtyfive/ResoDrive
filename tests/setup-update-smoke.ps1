param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$AppPath,
    [string]$BaselineVersion = '0.3.30',
    [switch]$UsePreviousUpdater
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Setup updater acceptance requires a disposable GitHub-hosted Windows runner.'
}
. (Join-Path $PSScriptRoot 'installer-ui.ps1')
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$version = ((Get-Item $candidate).VersionInfo.ProductVersion -split '\+', 2)[0]
if ($UsePreviousUpdater -and -not $PSBoundParameters.ContainsKey('BaselineVersion')) {
    $releaseJson = gh release list --exclude-drafts --exclude-pre-releases --limit 100 --json tagName
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the latest public updater baseline.' }
    $previousVersions = @($releaseJson | ConvertFrom-Json | ForEach-Object {
        if ($_.tagName -match '^v(\d+\.\d+\.\d+)$') { [version]$Matches[1] }
    } | Where-Object { $_ -lt [version]$version } | Sort-Object -Descending)
    if ($previousVersions.Count -eq 0) { throw 'No earlier public updater baseline is available.' }
    $BaselineVersion = $previousVersions[0].ToString()
}
$parsedBaseline = $null
if ($BaselineVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
    -not [version]::TryParse($BaselineVersion, [ref]$parsedBaseline) -or $parsedBaseline -ge [version]$version) {
    throw 'The public baseline must be a canonical x.y.z version earlier than the candidate.'
}
$oldInstall = Join-Path $env:ProgramFiles 'rdrive'
$newInstall = Join-Path $env:ProgramFiles 'ResoDrive'
if ((Test-Path (Join-Path $oldInstall 'resodrive.exe')) -or (Test-Path (Join-Path $newInstall 'resodrive.exe'))) {
    throw 'An application installation already exists.'
}
$mode = if ($UsePreviousUpdater) { 'previous-updater' } else { 'current-helper' }
$evidence = Join-Path $env:RUNNER_TEMP "resodrive-setup-update-smoke/$mode-$BaselineVersion"
$data = Join-Path $evidence 'data'
$updates = Join-Path $data 'updates'
New-Item -ItemType Directory -Path $updates -Force | Out-Null
$previousRoot = $env:RDRIVE_DATA_DIR
$env:RDRIVE_DATA_DIR = $data
$parent = $helper = $null
try {
    gh release download "v$BaselineVersion" --dir $evidence --pattern "resodrive-win-x64-$BaselineVersion.msi*"
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the public baseline.' }
    $baseline = Join-Path $evidence "resodrive-win-x64-$BaselineVersion.msi"
    $expected = (Get-Content "$baseline.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
    if ((Get-FileHash $baseline).Hash -ine $expected) { throw 'Baseline hash mismatch.' }
    $install = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $baseline + '"'), '/qn', '/norestart') -PassThru -Wait
    try { if ($install.ExitCode -ne 0) { throw 'Baseline installation failed.' } } finally { $install.Dispose() }
    $settings = Join-Path $data 'settings.json'
    [IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
    $settingsHash = (Get-FileHash $settings).Hash
    $cache = Join-Path $data 'cache-marker'
    [IO.File]::WriteAllText($cache, 'disposable unsent changes')
    $cacheHash = (Get-FileHash $cache).Hash
    $baselineApps = @(@((Join-Path $oldInstall 'resodrive.exe'), (Join-Path $newInstall 'resodrive.exe')) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($baselineApps.Count -ne 1) { throw 'Expected one installed public baseline executable.' }
    $oldApp = $baselineApps[0]
    $baselineProductVersion = (Get-Item -LiteralPath $oldApp).VersionInfo.ProductVersion
    if (($baselineProductVersion -split '\+', 2)[0] -ne $BaselineVersion) {
        throw 'The installed executable does not match the public baseline version.'
    }
    $oldDirectory = Split-Path $oldApp -Parent
    $parent = Start-Process $oldApp -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 4
    if ($parent.HasExited) { throw 'The baseline application did not stay running.' }
    $helperPath = Join-Path $updates 'resodrive-update-helper.exe'
    $helperSource = if ($UsePreviousUpdater) { $oldApp } else { $candidate }
    Copy-Item -LiteralPath $helperSource -Destination $helperPath
    $helperHash = (Get-FileHash -LiteralPath $helperPath).Hash
    $staged = Join-Path $updates "resodrive-win-x64-$version-setup.exe"
    Copy-Item -LiteralPath $setup -Destination $staged
    $outcome = Join-Path $updates 'application-update-result.json'
    $helper = Start-Process $helperPath -ArgumentList @('--complete-update', $version, ('"' + $staged + '"'),
        ('"' + $oldApp + '"'), ('"' + $oldApp + '"'), ('"' + $outcome + '"'), (Get-FileHash $staged).Hash, $parent.Id) -WindowStyle Hidden -PassThru
    $prepare = Start-Process $candidate -ArgumentList @('--prepare-install', ('"' + $oldDirectory + '"'), '2', ('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
    try { if ($prepare.ExitCode -ne 0) { throw 'The old app did not close safely.' } } finally { $prepare.Dispose() }
    if (-not $parent.WaitForExit(15000)) { throw 'The old window remains.' }
    Wait-VisibleSetup $helper '' (Join-Path $evidence 'setup-ui.json') -Passive
    # The reopened UI consumes its finalized receipt. The actual current helper
    # returns zero only after both installer success and a real ready-window ACK;
    # requiring the transient receipt afterward races that normal UI behavior.
    if ($helper.ExitCode -ne 0) { throw "Setup updater failed with code $($helper.ExitCode). See the retained installer logs." }
    $newApp = Join-Path $newInstall 'resodrive.exe'
    if ((Get-FileHash $newApp).Hash -ne (Get-FileHash $candidate).Hash) { throw 'Installed executable mismatch.' }
    if ((Get-FileHash $settings).Hash -ne $settingsHash -or (Get-FileHash $cache).Hash -ne $cacheHash) {
        throw 'Custom-root settings or cache changed.'
    }
    # A second activation must reach the already reopened window. A process
    # that starts a fresh window instead stays running and fails this check.
    $show = Start-Process $newApp -ArgumentList '--show' -WindowStyle Hidden -PassThru
    try {
        if (-not $show.WaitForExit(120000) -or $show.ExitCode -ne 0) {
            throw 'The actual reopened app did not acknowledge its ready window.'
        }
    } finally { $show.Dispose() }
    [ordered]@{ Version = $version; BaselineVersion = $BaselineVersion; BrandedSetup = $true;
        BaselineExeProductVersion = $baselineProductVersion; BaselineMsiSha256 = (Get-FileHash -LiteralPath $baseline).Hash;
        ActualCurrentUpdater = -not [bool]$UsePreviousUpdater; ActualPreviousPublicUpdater = [bool]$UsePreviousUpdater;
        UpdaterExeSha256 = $helperHash; CustomRootPreserved = $true; CachePreserved = $true;
        UpdaterExitCode = $helper.ExitCode; ReadyWindowAcknowledged = $true;
        SetupSha256 = (Get-FileHash $setup).Hash; InstalledExeSha256 = (Get-FileHash $newApp).Hash } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
    $installed = Get-ChildItem 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall' | Get-ItemProperty |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'ResoDrive' -and $_.DisplayVersion -eq $version }
    if (@($installed).Count -ne 1) { throw 'Expected one visible current MSI entry.' }
    $remove = Start-Process msiexec.exe -ArgumentList @('/x', $installed.PSChildName, '/qn', '/norestart', ('RDRIVE_DATA_ROOT="' + $data + '\."')) -Wait -PassThru
    try { if ($remove.ExitCode -ne 0) { throw 'Updated app removal failed.' } } finally { $remove.Dispose() }
    Write-Output "Setup updater acceptance passed: public $BaselineVersion, $mode, one branded progress window, reopened app and preserved settings/cache."
} finally {
    # Keep the installer and handoff diagnostics available to the workflow even
    # when Setup fails. Never collect account configuration or credential files.
    if (Test-Path -LiteralPath $updates) {
        $diagnostics = @(Get-ChildItem -LiteralPath $updates -File -Filter '*.log')
        foreach ($name in @('application-update-result.json', 'application-update-result.json.tmp', 'installer-preparation.json')) {
            $path = Join-Path $updates $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $diagnostics += Get-Item -LiteralPath $path
            }
        }
        foreach ($file in $diagnostics) {
            $name = if ($file.Name -eq 'application-update-result.json.tmp') { 'application-update-result-staged.json' } else { $file.Name }
            try { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidence $name) -Force }
            catch { Write-Warning "Could not preserve updater diagnostic $name." }
        }
    }
    $env:RDRIVE_DATA_DIR = $previousRoot
    if ($parent) { $parent.Dispose() }
    if ($helper) { $helper.Dispose() }
}

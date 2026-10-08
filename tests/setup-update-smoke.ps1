param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Setup updater acceptance requires a disposable GitHub-hosted Windows runner.'
}
. (Join-Path $PSScriptRoot 'installer-ui.ps1')
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$version = ((Get-Item $candidate).VersionInfo.ProductVersion -split '\+', 2)[0]
$oldInstall = Join-Path $env:ProgramFiles 'rdrive'
$newInstall = Join-Path $env:ProgramFiles 'ResoDrive'
if ((Test-Path (Join-Path $oldInstall 'resodrive.exe')) -or (Test-Path (Join-Path $newInstall 'resodrive.exe'))) {
    throw 'An application installation already exists.'
}
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-setup-update-smoke'
$data = Join-Path $evidence 'data'
$updates = Join-Path $data 'updates'
New-Item -ItemType Directory -Path $updates -Force | Out-Null
$previousRoot = $env:RDRIVE_DATA_DIR
$env:RDRIVE_DATA_DIR = $data
$parent = $helper = $null
try {
    gh release download v0.3.30 --dir $evidence --pattern 'resodrive-win-x64-0.3.30.msi*'
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the public baseline.' }
    $baseline = Join-Path $evidence 'resodrive-win-x64-0.3.30.msi'
    $expected = (Get-Content "$baseline.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
    if ((Get-FileHash $baseline).Hash -ine $expected) { throw 'Baseline hash mismatch.' }
    $install = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $baseline + '"'), '/qn', '/norestart') -PassThru -Wait
    try { if ($install.ExitCode -ne 0) { throw 'Baseline installation failed.' } } finally { $install.Dispose() }
    $settings = Join-Path $data 'settings.json'
    [IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
    $settingsHash = (Get-FileHash $settings).Hash
    $oldApp = Join-Path $oldInstall 'resodrive.exe'
    $parent = Start-Process $oldApp -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 4
    if ($parent.HasExited) { throw 'The baseline application did not stay running.' }
    $helperPath = Join-Path $updates 'resodrive-update-helper.exe'
    Copy-Item -LiteralPath $candidate -Destination $helperPath
    $staged = Join-Path $updates "resodrive-win-x64-$version-setup.exe"
    Copy-Item -LiteralPath $setup -Destination $staged
    $outcome = Join-Path $updates 'application-update-result.json'
    $helper = Start-Process $helperPath -ArgumentList @('--complete-update', $version, ('"' + $staged + '"'),
        ('"' + $oldApp + '"'), ('"' + $oldApp + '"'), ('"' + $outcome + '"'), (Get-FileHash $staged).Hash, $parent.Id) -WindowStyle Hidden -PassThru
    $prepare = Start-Process $candidate -ArgumentList @('--prepare-install', ('"' + $oldInstall + '"'), '2', ('"' + $data + '"')) -WindowStyle Hidden -PassThru -Wait
    try { if ($prepare.ExitCode -ne 0) { throw 'The old app did not close safely.' } } finally { $prepare.Dispose() }
    if (-not $parent.WaitForExit(15000)) { throw 'The old window remains.' }
    Wait-VisibleSetup $helper '' (Join-Path $evidence 'setup-ui.json') -Passive
    if ($helper.ExitCode -ne 0) { throw "Setup updater failed: $(Get-Content $outcome -Raw)" }
    $receipt = Get-Content $outcome -Raw | ConvertFrom-Json
    if (-not $receipt.Finalized -or -not $receipt.RelaunchAcknowledged -or $receipt.Status -ne 'succeeded') { throw 'Updater did not confirm the actual reopened app.' }
    $newApp = Join-Path $newInstall 'resodrive.exe'
    if ((Get-FileHash $newApp).Hash -ne (Get-FileHash $candidate).Hash) { throw 'Installed executable mismatch.' }
    if ((Get-FileHash $settings).Hash -ne $settingsHash) { throw 'Custom-root settings changed.' }
    [ordered]@{ Version = $version; BrandedSetup = $true; ActualCurrentUpdater = $true; CustomRootPreserved = $true;
        SetupSha256 = (Get-FileHash $setup).Hash; InstalledExeSha256 = (Get-FileHash $newApp).Hash } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
    $installed = Get-ChildItem 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall' | Get-ItemProperty |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'ResoDrive' -and $_.DisplayVersion -eq $version }
    if (@($installed).Count -ne 1) { throw 'Expected one visible current MSI entry.' }
    $remove = Start-Process msiexec.exe -ArgumentList @('/x', $installed.PSChildName, '/qn', '/norestart', ('RDRIVE_DATA_ROOT="' + $data + '\."')) -Wait -PassThru
    try { if ($remove.ExitCode -ne 0) { throw 'Updated app removal failed.' } } finally { $remove.Dispose() }
    Write-Output 'Setup updater acceptance passed: one branded progress window, current helper, reopened app and custom-root preservation.'
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

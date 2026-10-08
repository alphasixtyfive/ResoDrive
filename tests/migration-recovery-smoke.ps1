param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Migration recovery acceptance requires a disposable GitHub-hosted Windows runner.'
}
. (Join-Path $PSScriptRoot 'installer-ui.ps1')
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$local = [Environment]::GetFolderPath('LocalApplicationData')
$oldData = Join-Path $local 'rdrive'
$newData = Join-Path $local 'ResoDrive'
$install = Join-Path $env:ProgramFiles 'ResoDrive'
$installedApp = Join-Path $install 'resodrive.exe'
if ((Test-Path $installedApp) -or (Test-Path $oldData) -or -not (Test-Path $newData)) {
    throw 'Recovery requires the preserved, uninstalled default-root fixture from directory-migration-smoke.'
}
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-migration-recovery-smoke'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$previousRoot = $env:RDRIVE_DATA_DIR
$previousBridge = $env:RESODRIVE_LEGACY_HANDOFF
$parent = $updated = $null
try {
    $hashes = @{}
    foreach ($name in @('settings.json', 'cache\preserve.txt', 'managed-sync\preserve.txt', 'config-pass.dpapi')) {
        $hashes[$name] = (Get-FileHash (Join-Path $newData $name)).Hash
    }
    # Only relocate the disposable fixture, after checking both exact sibling paths.
    if ([IO.Path]::GetFullPath($newData) -ine (Join-Path $local 'ResoDrive') -or
        [IO.Path]::GetFullPath($oldData) -ine (Join-Path $local 'rdrive')) { throw 'Unexpected fixture paths.' }
    [IO.Directory]::Move($newData, $oldData)
    $env:RDRIVE_DATA_DIR = $oldData
    gh release download v0.3.35 --dir $evidence --pattern 'resodrive-win-x64-0.3.35.msi*'
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the withdrawn recovery baseline.' }
    $baseline = Join-Path $evidence 'resodrive-win-x64-0.3.35.msi'
    $expected = (Get-Content "$baseline.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
    if ((Get-FileHash $baseline).Hash -ine $expected) { throw 'Recovery baseline hash mismatch.' }
    $baselineInstall = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $baseline + '"'), '/qn', '/norestart',
        ('RDRIVE_DATA_ROOT="' + $oldData + '\."'), '/l*v', ('"' + $evidence + '\baseline.log"')) -Wait -PassThru
    try { if ($baselineInstall.ExitCode -ne 0) { throw 'Recovery baseline installation failed.' } } finally { $baselineInstall.Dispose() }
    # Reproduce .35's post-handoff state: current program path, old data root,
    # and a running app/host. The baseline's existing bridge flag defers its move.
    $env:RESODRIVE_LEGACY_HANDOFF = '1'
    $completionPath = Join-Path $local 'ResoDriveMigration\completion.json'
    [IO.File]::WriteAllText($completionPath, '{"Succeeded":false,"Message":"Prior migration did not finish."}')
    $parent = Start-Process $installedApp -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    if ($parent.HasExited -or -not (Test-Path $oldData) -or (Test-Path $newData)) { throw 'The partial-migration baseline was not reproduced.' }
    $env:RESODRIVE_LEGACY_HANDOFF = $null
    $update = Start-Process $setup -ArgumentList @('/passive', '/norestart', '/log', ('"' + $evidence + '\setup.log"'),
        ('ResoDriveDataRoot="' + $oldData + '\."')) -PassThru
    try {
        Wait-VisibleSetup $update '' (Join-Path $evidence 'setup-ui.json') -Passive
        if ($update.ExitCode -notin @(0, 3010)) { throw "Recovery Setup failed: $($update.ExitCode)" }
    } finally { $update.Dispose() }
    if (-not $parent.WaitForExit(15000)) { throw 'The .35 app was not safely stopped.' }
    if ((Get-FileHash $installedApp).Hash -ne (Get-FileHash $candidate).Hash) { throw 'Recovery executable mismatch.' }
    $updated = Start-Process $installedApp -ArgumentList '--show' -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $completed = $false
    while (-not $updated.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        try { $completed = -not (Test-Path $oldData) -and (Get-Content $completionPath -Raw | ConvertFrom-Json).Succeeded }
        catch { $completed = $false } # Receipt publication can race this read.
        if ($completed) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $completed -or $updated.HasExited) { throw 'The corrected app did not finish the move and replace the previous failure receipt.' }
    $show = Start-Process $installedApp -ArgumentList '--show' -WindowStyle Hidden -PassThru
    try {
        if (-not $show.WaitForExit(120000) -or $show.ExitCode -ne 0 -or $updated.HasExited) {
            throw 'The recovered app did not acknowledge its actual ready window.'
        }
    } finally { $show.Dispose() }
    foreach ($name in $hashes.Keys) {
        if ((Get-FileHash (Join-Path $newData $name)).Hash -ine $hashes[$name]) { throw "Recovery changed preserved file: $name" }
    }
    [ordered]@{ PriorVersion = '0.3.35'; InstalledVersion = (Get-Item $installedApp).VersionInfo.ProductVersion;
        PendingDefaultDataMoved = $true; ReadyWindowAcknowledged = $true; PreviousFailureReplaced = $true;
        SettingsCacheAndCredentialBytesPreserved = $true;
        SetupSha256 = (Get-FileHash $setup).Hash; InstalledExeSha256 = (Get-FileHash $installedApp).Hash } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
    $remove = Start-Process msiexec.exe -ArgumentList @('/x', ('"' + (Join-Path (Split-Path $setup) ((Split-Path $setup -Leaf) -replace '-setup.exe$', '.msi')) + '"'),
        '/qn', '/norestart', ('RDRIVE_DATA_ROOT="' + $newData + '\."')) -Wait -PassThru
    try { if ($remove.ExitCode -ne 0) { throw 'Recovery fixture removal failed.' } } finally { $remove.Dispose() }
    Write-Output 'Partial-migration recovery passed against the actual .35 program with preserved default-root data.'
} finally {
    $env:RDRIVE_DATA_DIR = $previousRoot
    $env:RESODRIVE_LEGACY_HANDOFF = $previousBridge
    if ($parent) { $parent.Dispose() }
    if ($updated) { $updated.Dispose() }
}

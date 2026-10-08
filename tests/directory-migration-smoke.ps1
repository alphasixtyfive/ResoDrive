param([Parameter(Mandatory)][string]$MsiPath, [Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Directory migration acceptance requires a disposable GitHub-hosted Windows runner.'
}
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$version = ((Get-Item -LiteralPath $candidate).VersionInfo.ProductVersion -split '\+', 2)[0]
$oldInstall = Join-Path $env:ProgramFiles 'rdrive'
$newInstall = Join-Path $env:ProgramFiles 'ResoDrive'
$oldApp = Join-Path $oldInstall 'resodrive.exe'
$newApp = Join-Path $newInstall 'resodrive.exe'
$local = [Environment]::GetFolderPath('LocalApplicationData')
$oldData = Join-Path $local 'rdrive'
$newData = Join-Path $local 'ResoDrive'
foreach ($path in @($oldInstall, $newInstall, $oldData, $newData)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite existing migration fixture: $path" }
}
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-directory-migration-smoke'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
$previousRoot = $env:RDRIVE_DATA_DIR
$env:RDRIVE_DATA_DIR = $oldData
$userName = 'rdmigrate' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$testUser = $null
$credential = $null
$helper = $null

function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 250
    }
    throw $Message
}
function Invoke-Msi([string]$Arguments, [switch]$Failure) {
    $process = Start-Process $msiexec -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(180000)) { throw 'Migration MSI timed out.' }
        if ($Failure) {
            if ($process.ExitCode -in @(0, 3010)) { throw 'The conflicting deployment profile did not block installation.' }
        } elseif ($process.ExitCode -notin @(0, 3010)) { throw "Migration MSI failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
function Stop-Application([string]$Directory, [string]$Data) {
    $process = Start-Process $candidate -ArgumentList @('--prepare-install', ('"' + $Directory + '"'), '2', ('"' + $Data + '"')) -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(90000) -or $process.ExitCode -ne 0) { throw 'Migration fixture could not stop safely.' }
    } finally { $process.Dispose() }
}
function Task-Hash([string]$Sid) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Sid))).Substring(0, 12)
}
function Read-ProtectedFixture([string]$Root) {
    $encrypted = [Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $Root 'config-pass.dpapi')))
    $plain = [Security.Cryptography.ProtectedData]::Unprotect($encrypted,
        [Text.Encoding]::UTF8.GetBytes('rdrive/rclone/config-password/v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ([Text.Encoding]::UTF8.GetString($plain) -cne 'disposable migration fixture') { throw 'Protected credentials no longer decrypt for their owner.' }
}

try {
    $priorName = 'resodrive-win-x64-0.3.30.msi'
    $priorMsi = Join-Path $evidence $priorName
    gh release download v0.3.30 --dir $evidence --pattern "$priorName*"
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the actual prior public MSI.' }
    $hash = (Get-Content -LiteralPath "$priorMsi.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
    if ($hash -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash $priorMsi).Hash -ine $hash) { throw 'Prior MSI checksum mismatch.' }
    Invoke-Msi "/i `"$priorMsi`" /qn /norestart /l*v `"$evidence\baseline.log`""
    $oldHash = (Get-FileHash -LiteralPath $oldApp).Hash

    # Use the real default layout and owner-protected credentials, with no real account.
    New-Item -ItemType Directory -Path (Join-Path $oldData 'cache'), (Join-Path $oldData 'managed-sync') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $oldData 'settings.json'), '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
    [IO.File]::WriteAllText((Join-Path $oldData 'cache\preserve.txt'), 'Cached files survive one-update migration.')
    [IO.File]::WriteAllText((Join-Path $oldData 'managed-sync\preserve.txt'), 'Managed copies survive one-update migration.')
    $encrypted = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('disposable migration fixture'),
        [Text.Encoding]::UTF8.GetBytes('rdrive/rclone/config-password/v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllText((Join-Path $oldData 'config-pass.dpapi'), [Convert]::ToBase64String($encrypted))
    $preserved = @{}
    foreach ($name in @('settings.json', 'cache\preserve.txt', 'managed-sync\preserve.txt', 'config-pass.dpapi')) {
        $preserved[$name] = (Get-FileHash (Join-Path $oldData $name)).Hash
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $startupName = 'ResoDrive Startup - ' + (Task-Hash $sid)
    $action = New-ScheduledTaskAction -Execute $oldApp -Argument '--background'
    $principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $startupName -Action $action -Principal $principal -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $sid) `
        -Description 'Starts ResoDrive for this user at sign-in. Managed by ResoDrive.' | Out-Null
    Disable-ScheduledTask -TaskName $startupName | Out-Null

    # A real deferred-action failure must restore the old MSI and owned startup task.
    New-Item -ItemType Directory -Path $newInstall -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $oldInstall 'profiles.json'), '{"profiles":[]}')
    [IO.File]::WriteAllText((Join-Path $newInstall 'profiles.json'), '{"profiles":[],"conflict":true}')
    Invoke-Msi "/i `"$msi`" /qn /norestart /l*v `"$evidence\rollback.log`"" -Failure
    if ((Get-FileHash $oldApp).Hash -ine $oldHash -or (Test-Path $newApp)) { throw 'Native MSI rollback did not restore the old executable.' }
    $task = Get-ScheduledTask -TaskName $startupName
    if ($task.State -ne 'Disabled' -or $task.Actions[0].Execute -ine $oldApp) { throw 'Rollback did not restore the disabled startup task.' }
    Remove-Item -LiteralPath (Join-Path $newInstall 'profiles.json')

    # Initialize a signed-out account's old default root under that account's identity.
    $password = ConvertTo-SecureString ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'aA1!') -AsPlainText -Force
    $testUser = New-LocalUser -Name $userName -Password $password -AccountNeverExpires -PasswordNeverExpires
    $credential = [pscredential]::new("$env:COMPUTERNAME\$userName", $password)
    $fixtureScript = Join-Path $evidence 'user-fixture.ps1'
    @'
param([switch]$Verify)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $(if ($Verify) {'ResoDrive'} else {'rdrive'})
if ($Verify) {
    if (Test-Path (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'rdrive')) { throw 'Old user root remains.' }
    if ([IO.File]::ReadAllText((Join-Path $root 'cache\preserve.txt')) -cne 'second account') { throw 'Second-user cache changed.' }
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $root 'config-pass.dpapi'))), [Text.Encoding]::UTF8.GetBytes('rdrive/rclone/config-password/v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ([Text.Encoding]::UTF8.GetString($bytes) -cne 'second account') { throw 'Second-user credential changed.' }
} else {
    New-Item -ItemType Directory -Path (Join-Path $root 'cache') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'settings.json'), '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
    [IO.File]::WriteAllText((Join-Path $root 'cache\preserve.txt'), 'second account')
    $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('second account'), [Text.Encoding]::UTF8.GetBytes('rdrive/rclone/config-password/v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllText((Join-Path $root 'config-pass.dpapi'), [Convert]::ToBase64String($bytes))
}
'@ | Set-Content -LiteralPath $fixtureScript
    & icacls.exe $evidence /grant "${userName}:(OI)(CI)RX" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant fixture-script access.' }
    $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $initialize = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $fixtureScript + '"')) `
        -Credential $credential -LoadUserProfile -WorkingDirectory $evidence -WindowStyle Hidden -PassThru
    try { if (-not $initialize.WaitForExit(30000) -or $initialize.ExitCode -ne 0) { throw 'Second-account initialization failed.' } } finally { $initialize.Dispose() }

    # Run the actual public updater helper, including its compiled old relaunch path.
    $updates = Join-Path $oldData 'updates'
    New-Item -ItemType Directory -Path $updates -Force | Out-Null
    $helperPath = Join-Path $updates 'resodrive-update-helper.exe'
    Copy-Item $oldApp $helperPath
    $stagedMsi = Join-Path $updates "resodrive-win-x64-$version.msi"
    Copy-Item $msi $stagedMsi
    $env:RDRIVE_UPDATE_HANDOFF_DIR = $updates
    $parent = Start-Process $oldApp -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    $parent.Refresh()
    if ($parent.HasExited) { throw 'The actual old application did not start.' }
    $outcome = Join-Path $updates 'application-update-result.json'
    $helper = Start-Process $helperPath -ArgumentList @('--complete-update', $version, ('"' + $stagedMsi + '"'),
        ('"' + $oldApp + '"'), ('"' + $oldApp + '"'), ('"' + $outcome + '"'), (Get-FileHash $stagedMsi).Hash, $parent.Id) -PassThru -WindowStyle Hidden
    Stop-Application $oldInstall $oldData
    if (-not $parent.WaitForExit(15000)) { throw 'The old UI did not close.' }
    $parent.Dispose()
    if (-not $helper.WaitForExit(240000) -or $helper.ExitCode -ne 0) { throw 'The actual prior-version updater handoff failed.' }
    Wait-Until { Test-Path (Join-Path $local 'ResoDriveMigration\completion.json') } 'No migration completion receipt.'
    $receipt = Get-Content (Join-Path $local 'ResoDriveMigration\completion.json') -Raw | ConvertFrom-Json
    if (-not $receipt.Succeeded -or (Test-Path $oldData)) { throw "User migration failed: $($receipt.Message)" }
    if ((Get-FileHash $newApp).Hash -ine (Get-FileHash $candidate).Hash) { throw 'Installed executable differs from the exact candidate.' }
    foreach ($name in $preserved.Keys) {
        if ((Get-FileHash (Join-Path $newData $name)).Hash -ine $preserved[$name]) { throw "Migration changed preserved file: $name" }
    }
    Read-ProtectedFixture $newData
    $task = Get-ScheduledTask -TaskName $startupName
    if ($task.State -ne 'Disabled' -or $task.Actions[0].Execute -ine $newApp) { throw 'Startup migration changed the disabled preference.' }
    $dataTask = 'ResoDrive Data Migration - ' + (Task-Hash $testUser.SID.Value)
    $task = Get-ScheduledTask -TaskName $dataTask
    if ($task.Principal.UserId -ine $testUser.SID.Value -or $task.Principal.RunLevel -ne 'Limited') { throw 'Second-account task has incorrect privilege or identity.' }

    # Stop the first user's own host before the signed-out-account fixture runs;
    # the separate cross-account gate checks that foreign hosts are never killed.
    Stop-Application $newInstall $newData
    $migrate = Start-Process $newApp -ArgumentList '--migrate-user-data' -Credential $credential -LoadUserProfile `
        -Environment @{ RDRIVE_DATA_DIR = ''; RDRIVE_UPDATE_HANDOFF_DIR = '' } -WorkingDirectory $newInstall -PassThru -WindowStyle Hidden
    try { if (-not $migrate.WaitForExit(90000) -or $migrate.ExitCode -ne 0) { throw 'Second-account migration failed.' } } finally { $migrate.Dispose() }
    $verify = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $fixtureScript + '"'), '-Verify') `
        -Credential $credential -LoadUserProfile -WorkingDirectory $evidence -WindowStyle Hidden -PassThru
    try { if (-not $verify.WaitForExit(30000) -or $verify.ExitCode -ne 0) { throw 'Second-account preservation failed.' } } finally { $verify.Dispose() }
    Start-ScheduledTask -TaskName 'ResoDrive Installation Migration Cleanup'
    Wait-Until { -not (Test-Path $oldInstall) -and -not (Get-ScheduledTask -TaskName 'ResoDrive Installation Migration Cleanup' -ErrorAction SilentlyContinue) } 'Old installation cleanup did not finish.'
    if (Get-ScheduledTask -TaskName $dataTask -ErrorAction SilentlyContinue) { throw 'Completed user migration task remains.' }
    [ordered]@{ PriorVersion = '0.3.30'; InstalledVersion = (Get-Item $newApp).VersionInfo.ProductVersion;
        CandidateMsiSha256 = (Get-FileHash $msi).Hash; InstalledExeSha256 = (Get-FileHash $newApp).Hash;
        NativeRollback = $true; ActualOldUpdater = $true; OwnerCredentialsPreserved = $true;
        DefaultDataMoved = $true; DisabledStartupPreserved = $true; SignedOutUserMigrated = $true;
        LegacyInstallationRemoved = $true } | ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
    Invoke-Msi "/x `"$msi`" /qn /norestart /l*v `"$evidence\uninstall.log`""
    Write-Output 'Directory migration acceptance passed: rollback, actual old updater, protected data, disabled startup and signed-out account.'
} finally {
    $env:RDRIVE_DATA_DIR = $previousRoot
    $env:RDRIVE_UPDATE_HANDOFF_DIR = $null
    if ($helper) { $helper.Dispose() }
    if ($testUser) {
        $existing = Get-LocalUser -Name $userName -ErrorAction SilentlyContinue
        if ($existing -and $existing.SID.Value -eq $testUser.SID.Value) { Remove-LocalUser -Name $userName }
    }
    foreach ($path in @((Join-Path $env:ProgramData 'ResoDriveMigration\failure.json'), (Join-Path $local 'ResoDriveMigration\completion.json'))) {
        if (Test-Path $path) { Copy-Item $path (Join-Path $evidence ([IO.Path]::GetFileName($path))) -Force }
    }
}

param(
    [Parameter(Mandatory)][string]$NewAppPath,
    [string]$PreviousVersion = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This test creates a Windows account. Keep it on a disposable hosted runner.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Cross-account smoke requires a disposable GitHub-hosted Windows runner.'
}
$runnerIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($runnerIdentity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Cross-account smoke requires the hosted runner administrator account.'
}
$newApp = (Resolve-Path -LiteralPath $NewAppPath).Path
if ([IO.Path]::GetFileName($newApp) -ine 'resodrive.exe') {
    throw 'The new preparation helper must be the built ResoDrive executable.'
}

if ([string]::IsNullOrWhiteSpace($PreviousVersion)) {
    $project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw)
    $targetVersion = [version]$project.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
    $releaseJson = gh release list --repo alphasixtyfive/ResoDrive --exclude-drafts --exclude-pre-releases --limit 100 --json tagName
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the previous public release.' }
    $versions = @($releaseJson | ConvertFrom-Json | ForEach-Object {
        if ($_.tagName -match '^v(\d+\.\d+\.\d+)$') { [version]$Matches[1] }
    } | Where-Object { $_ -lt $targetVersion } | Sort-Object -Descending)
    if ($versions.Count -eq 0) { throw 'No earlier public release is available.' }
    $PreviousVersion = $versions[0].ToString()
}
if ($PreviousVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid previous version.' }

$runId = [Guid]::NewGuid().ToString('N')
$userName = 'rdsmoke' + $runId.Substring(0, 8)
$rootParent = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ResoDriveCrossAccountSmoke'
$testRoot = Join-Path $rootParent $runId
$binaryRoot = Join-Path $testRoot 'previous'
$dataRoot = Join-Path $testRoot 'data'
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-cross-account-smoke'
$accountCreated = $false
$hostProcess = $null
$securePassword = $null

function Assert-NoReparseAncestor([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and
            [IO.File]::GetAttributes($current).HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a redirected cross-account test path: $current"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent.Equals($current, [StringComparison]::OrdinalIgnoreCase)) { break }
    }
}

function Invoke-Preparation([string]$App, [string]$Installation, [string]$Data) {
    $arguments = @('--prepare-install', ('"' + $Installation + '"'), '2', ('"' + $Data + '"'))
    $process = Start-Process -FilePath $App -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(90000)) {
            throw 'Preparation timed out.'
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}

try {
    Assert-NoReparseAncestor $rootParent
    New-Item -ItemType Directory -Path $testRoot, $binaryRoot, (Join-Path $dataRoot 'cache'), $evidence -Force | Out-Null
    $zipName = "resodrive-win-x64-$PreviousVersion.zip"
    $zipPath = Join-Path $testRoot $zipName
    $url = "https://github.com/alphasixtyfive/ResoDrive/releases/download/v$PreviousVersion/$zipName"
    Invoke-WebRequest -Uri $url -OutFile $zipPath
    $checksum = (Invoke-WebRequest -Uri "$url.sha256").Content
    if ($checksum -is [byte[]]) { $checksum = [Text.Encoding]::ASCII.GetString($checksum) }
    $match = [regex]::Match($checksum.Trim(), '\A([0-9a-fA-F]{64})\s+\*?(.+)\z')
    if (-not $match.Success -or $match.Groups[2].Value -cne $zipName -or
        (Get-FileHash -LiteralPath $zipPath).Hash -ine $match.Groups[1].Value) {
        throw 'The previous public portable package failed SHA-256 verification.'
    }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $binaryRoot
    $oldApp = Join-Path $binaryRoot 'resodrive.exe'
    if (-not (Test-Path -LiteralPath $oldApp -PathType Leaf)) {
        throw 'The previous public portable package has no ResoDrive executable.'
    }

    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'aA1!'
    $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    $testUser = New-LocalUser -Name $userName -Password $securePassword -AccountNeverExpires -PasswordNeverExpires
    $accountCreated = $true
    $credential = [pscredential]::new("$env:COMPUTERNAME\$userName", $securePassword)
    $grant = "$env:COMPUTERNAME\${userName}:(OI)(CI)M"
    & icacls.exe $testRoot /grant $grant /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the disposable user access to isolated test data.' }

    $settings = Join-Path $dataRoot 'settings.json'
    $marker = Join-Path $dataRoot 'cache\preserve.txt'
    [IO.File]::WriteAllText($settings, '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":false,"startWithWindows":false},"mounts":[]}')
    [IO.File]::WriteAllText($marker, 'Cross-account preparation must preserve this local cache marker.')
    $environment = @{ RDRIVE_DATA_DIR = $dataRoot }
    $hostProcess = Start-Process -FilePath $oldApp -ArgumentList '--host' -Credential $credential `
        -LoadUserProfile -Environment $environment -WorkingDirectory $binaryRoot `
        -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $hostProcess.Refresh()
    if ($hostProcess.HasExited) { throw 'The disposable prior-version host exited before the cross-account check.' }
    if ($hostProcess.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) {
        throw 'The alternate-credential host was launched in a different Windows session; this runner cannot test the intended path.'
    }
    $hostCim = Get-CimInstance Win32_Process -Filter "ProcessId=$($hostProcess.Id)"
    $hostOwner = Invoke-CimMethod -InputObject $hostCim -MethodName GetOwnerSid
    if ($hostOwner.ReturnValue -ne 0 -or $hostOwner.Sid -ne $testUser.SID.Value -or
        $hostOwner.Sid -eq $runnerIdentity.User.Value) {
        throw 'The host did not run under the disposable standard-user account.'
    }

    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    $markerHash = (Get-FileHash -LiteralPath $marker).Hash
    $blockedExit = Invoke-Preparation $newApp $binaryRoot $dataRoot
    $blockedResult = Get-Content -LiteralPath (Join-Path $dataRoot 'updates\installer-preparation.json') -Raw | ConvertFrom-Json
    if ($blockedExit -eq 0 -or $blockedResult.Succeeded -or
        $blockedResult.Message -notmatch 'different Windows account' -or $hostProcess.HasExited) {
        throw 'Preparation did not leave the active other-account host untouched.'
    }
    Copy-Item -LiteralPath (Join-Path $dataRoot 'updates\installer-preparation.json') `
        -Destination (Join-Path $evidence 'blocked-installer-preparation.json')

    $stop = Start-Process -FilePath $oldApp -ArgumentList '--prepare-update' -Credential $credential `
        -LoadUserProfile -Environment $environment -WorkingDirectory $binaryRoot `
        -WindowStyle Hidden -PassThru
    try {
        if (-not $stop.WaitForExit(45000) -or $stop.ExitCode -ne 0) {
            throw 'The disposable user could not stop its host safely.'
        }
    } finally { $stop.Dispose() }
    if (-not $hostProcess.WaitForExit(10000)) { throw 'The prior-version host remained after its own shutdown.' }

    $readyExit = Invoke-Preparation $newApp $binaryRoot $dataRoot
    $readyResult = Get-Content -LiteralPath (Join-Path $dataRoot 'updates\installer-preparation.json') -Raw | ConvertFrom-Json
    if ($readyExit -ne 0 -or -not $readyResult.Succeeded) {
        throw 'Preparation did not succeed after the other-account host exited.'
    }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or
        (Get-FileHash -LiteralPath $marker).Hash -ne $markerHash) {
        throw 'Cross-account preparation changed isolated settings or cache.'
    }

    [ordered]@{
        PreviousVersion = $PreviousVersion
        PriorPackageSha256 = (Get-FileHash -LiteralPath $zipPath).Hash
        NewHelperSha256 = (Get-FileHash -LiteralPath $newApp).Hash
        DifferentAccountSid = $testUser.SID.Value
        RunnerSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        ActiveHostBlocked = $true
        HostExitedAfterOwnShutdown = $true
        PreparationSucceededAfterExit = $true
        SettingsAndCachePreserved = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
    Write-Output "Cross-account preparation smoke passed. Evidence: $evidence\result.json"
} finally {
    try {
        if ($hostProcess) {
            try {
                if (-not $hostProcess.HasExited) {
                    $hostProcess.Kill()
                    $hostProcess.WaitForExit(10000) | Out-Null
                }
            } finally { $hostProcess.Dispose() }
        }
        if (Test-Path -LiteralPath (Join-Path $dataRoot 'updates\installer-preparation.json')) {
            Copy-Item -LiteralPath (Join-Path $dataRoot 'updates\installer-preparation.json') `
                -Destination (Join-Path $evidence 'installer-preparation.json') -Force
        }
        if (Test-Path -LiteralPath (Join-Path $dataRoot 'logs\resodrive-ui.log')) {
            Copy-Item -LiteralPath (Join-Path $dataRoot 'logs\resodrive-ui.log') `
                -Destination (Join-Path $evidence 'resodrive-ui.log') -Force
        }
    } finally {
        try {
            if ($accountCreated) {
                $createdAccount = Get-LocalUser -Name $userName -ErrorAction SilentlyContinue
                if ($createdAccount -and $createdAccount.SID.Value -ne $testUser.SID.Value) {
                    throw 'Refusing to remove a different local account.'
                }
                if ($createdAccount) { Remove-LocalUser -Name $userName -ErrorAction Stop }
            }
        } finally {
            if ($securePassword) { $securePassword.Dispose() }
            if (Test-Path -LiteralPath $testRoot) {
                Assert-NoReparseAncestor $rootParent
                Assert-NoReparseAncestor $testRoot
                $resolvedParent = (Resolve-Path -LiteralPath $rootParent).ProviderPath
                $resolvedRoot = (Resolve-Path -LiteralPath $testRoot).ProviderPath
                if (-not $resolvedRoot.StartsWith($resolvedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [IO.Path]::GetFileName($resolvedRoot).Equals($runId, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing to remove a path outside the disposable cross-account test root.'
                }
                Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
            }
        }
    }
}

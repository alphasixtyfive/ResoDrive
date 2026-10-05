param(
    [Parameter(Mandatory)][string]$SetupPath,
    [string]$PreviousVersion = '',
    [string]$SameVersionBaselineMsiPath = '',
    [string]$SameVersionBaselineSetupPath = '',
    [string]$CandidateMsiPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This test installs machine-wide software. Never run it on a developer's PC.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer smoke tests require a disposable GitHub-hosted Windows runner.'
}
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw)
$expectedVersion = $project.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
$targetVersion = [version]$expectedVersion
if ([string]::IsNullOrWhiteSpace($PreviousVersion)) {
    $releaseJson = gh release list --repo alphasixtyfive/ResoDrive --exclude-drafts --exclude-pre-releases --limit 100 --json tagName
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the previous public installer.' }
    $previousVersions = @($releaseJson | ConvertFrom-Json | ForEach-Object {
        if ($_.tagName -match '^v(\d+\.\d+\.\d+)$') { [version]$Matches[1] }
    } | Where-Object { $_ -lt $targetVersion } | Sort-Object -Descending)
    if ($previousVersions.Count -eq 0) { throw 'No earlier public installer is available.' }
    $PreviousVersion = $previousVersions[0].ToString()
}
if ($PreviousVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid previous version.' }
Write-Output "Testing upgrade from public ResoDrive $PreviousVersion."
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
$managedCopy = Join-Path $env:RDRIVE_DATA_DIR 'managed-sync\00000000000000000000000000000001\preserve.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $managedCopy) -Force | Out-Null
[IO.File]::WriteAllText($managedCopy, 'Managed copies survive upgrades and uninstall; only an accepted remote wipe removes them.')
$managedCopyHash = (Get-FileHash -LiteralPath $managedCopy).Hash

function Invoke-Installer([string]$Executable, [string]$Arguments, [switch]$ExpectFailure) {
    # Windows Installer's service does not inherit process-local RDRIVE_DATA_DIR.
    # Pass the isolated root through the supported bundle/MSI property instead.
    if ([IO.Path]::GetFileName($Executable) -ieq 'msiexec.exe') {
        $Arguments += " RDRIVE_DATA_ROOT=`"$([IO.Path]::TrimEndingDirectorySeparator($env:RDRIVE_DATA_DIR))\.`""
    } else {
        $Arguments += " ResoDriveDataRoot=`"$env:RDRIVE_DATA_DIR`""
    }
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(180000)) { throw "Installer timed out: $Arguments" }
        if ($ExpectFailure) {
            if ($process.ExitCode -in @(0,3010)) { throw "Installer unexpectedly accepted an unsupported operation: $Arguments" }
        } elseif ($process.ExitCode -notin @(0,3010)) {
            throw "Installer failed ($($process.ExitCode)): $Arguments"
        }
    } finally { $process.Dispose() }
}
function Assert-DataPreserved {
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or
        (Get-FileHash -LiteralPath $marker).Hash -ne $markerHash -or
        (Get-FileHash -LiteralPath $managedCopy).Hash -ne $managedCopyHash) { throw 'Installation changed user settings, cache or managed copies.' }
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

function Get-MsiProperty([string]$Path, [string]$Name) {
    if ($Name -notin @('ProductCode', 'ProductVersion', 'UpgradeCode')) { throw 'Unsupported MSI property.' }
    $installer = $database = $view = $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
        $query = 'SELECT `Value` FROM `Property` WHERE `Property` = ' + "'$Name'"
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($query))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { throw "MSI property '$Name' is missing." }
        return [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    } finally {
        if ($null -ne $view) { $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null }
        foreach ($item in @($record, $view, $database, $installer)) {
            if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
        }
    }
}

function Get-RelatedMsiProducts([string]$UpgradeCode) {
    $installer = $products = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $products = $installer.GetType().InvokeMember('RelatedProducts', 'GetProperty', $null, $installer, @($UpgradeCode))
        $count = [int]$products.GetType().InvokeMember('Count', 'GetProperty', $null, $products, $null)
        for ($index = 0; $index -lt $count; $index++) {
            [string]$products.GetType().InvokeMember('Item', 'GetProperty', $null, $products, @($index))
        }
    } finally {
        foreach ($item in @($products, $installer)) {
            if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
        }
    }
}

function Assert-OneRelatedMsiProduct([string]$ProductCode) {
    $products = @(Get-RelatedMsiProducts $sameVersionUpgradeCode)
    if ($products.Count -ne 1 -or $products[0] -ine $ProductCode) {
        throw "Expected only product $ProductCode, found: $($products -join ', ')."
    }
}

function Get-RelatedBundleEntries {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        $views = if ($hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser) {
            @([Microsoft.Win32.RegistryView]::Default)
        } else {
            @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)
        }
        foreach ($registryView in $views) {
            $base = $uninstall = $null
            try {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $registryView)
                $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
                if ($null -eq $uninstall) { continue }
                foreach ($name in $uninstall.GetSubKeyNames()) {
                    $entry = $uninstall.OpenSubKey($name)
                    try {
                        if ($null -ne $entry -and @($entry.GetValue('BundleUpgradeCode', @())) -icontains $bundleUpgradeCode) {
                            "$hive/$registryView/$name"
                        }
                    } finally { if ($null -ne $entry) { $entry.Dispose() } }
                }
            } finally {
                if ($null -ne $uninstall) { $uninstall.Dispose() }
                if ($null -ne $base) { $base.Dispose() }
            }
        }
    }
}

function Assert-BundleCount([int]$Expected) {
    $entries = @(Get-RelatedBundleEntries)
    if ($entries.Count -ne $Expected) { throw "Expected $Expected related bundle entries, found: $($entries -join ', ')." }
}

function Assert-OneRemovableAppEntry {
    Assert-OneRelatedMsiProduct $candidateProductCode
    Assert-BundleCount 0
    $entry = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$candidateProductCode"
    $hidden = $entry.PSObject.Properties['SystemComponent']
    if (($null -ne $hidden -and $hidden.Value -eq 1) -or $entry.DisplayVersion -ne $expectedVersion) {
        throw 'The current MSI is not a visible, correctly versioned Windows app entry.'
    }
}

function Assert-VerifiedAsset([string]$Path) {
    $checksum = [IO.File]::ReadAllText($Path + '.sha256').Trim()
    $match = [regex]::Match($checksum, '\A([0-9a-fA-F]{64})\s+\*?(.+)\z')
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not $match.Success -or $match.Groups[2].Value -cne [IO.Path]::GetFileName($Path) -or $hash -ine $match.Groups[1].Value) {
        throw "Asset checksum does not match '$Path'."
    }
    return $hash
}

function Assert-CandidateInstalled {
    if ((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash -ine $candidateAppHash -or
        (Get-Item -LiteralPath $app).VersionInfo.ProductVersion -cne $candidateProductVersion) {
        throw 'The installed executable does not match the exact candidate bundle payload.'
    }
}

$sameVersionPaths = @($SameVersionBaselineMsiPath, $SameVersionBaselineSetupPath, $CandidateMsiPath)
$sameVersionRecovery = @($sameVersionPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
$bundleUpgradeCode = '{5B94F457-820F-4B41-B609-071179764B08}'
$msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
$candidateMsi = Join-Path ([IO.Path]::GetDirectoryName($setup)) "resodrive-win-x64-$expectedVersion.msi"
$candidateProductCode = Get-MsiProperty $candidateMsi 'ProductCode'
$sameVersionUpgradeCode = Get-MsiProperty $candidateMsi 'UpgradeCode'
$sameVersionReceipt = $null
try {
    if ($sameVersionRecovery) {
        if (@($sameVersionPaths | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
            throw 'Same-version smoke requires both baseline installers and the candidate MSI.'
        }
        $baselineMsi = (Resolve-Path -LiteralPath $SameVersionBaselineMsiPath).Path
        $baselineSetup = (Resolve-Path -LiteralPath $SameVersionBaselineSetupPath).Path
        $candidateMsi = (Resolve-Path -LiteralPath $CandidateMsiPath).Path
        $sameVersionReceipt = [ordered]@{
            Version = $expectedVersion
            BaselineMsiSha256 = Assert-VerifiedAsset $baselineMsi
            BaselineSetupSha256 = Assert-VerifiedAsset $baselineSetup
            CandidateMsiSha256 = Assert-VerifiedAsset $candidateMsi
            CandidateSetupSha256 = Assert-VerifiedAsset $setup
            MsiReplacementPassed = $false
            BundleReplacementPassed = $false
            UserDataPreserved = $false
        }
        $sameVersionUpgradeCode = Get-MsiProperty $candidateMsi 'UpgradeCode'
        $baselineProductCode = Get-MsiProperty $baselineMsi 'ProductCode'
        $candidateProductCode = Get-MsiProperty $candidateMsi 'ProductCode'
        if ((Get-MsiProperty $baselineMsi 'ProductVersion') -ne $expectedVersion -or
            (Get-MsiProperty $candidateMsi 'ProductVersion') -ne $expectedVersion -or
            (Get-MsiProperty $baselineMsi 'UpgradeCode') -ine $sameVersionUpgradeCode -or
            $baselineProductCode -ieq $candidateProductCode) {
            throw 'The baselines and candidate must be distinct MSI products in the same version and product family.'
        }
        if (@(Get-RelatedMsiProducts $sameVersionUpgradeCode).Count -ne 0) { throw 'The test product family is already installed.' }
        Assert-BundleCount 0
        $sameVersionReceipt['BaselineProductCode'] = $baselineProductCode
        $sameVersionReceipt['CandidateProductCode'] = $candidateProductCode
        $sameVersionReceipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'same-version-result.json') -Encoding utf8
    }
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\fresh.log`""
    $candidateAppHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
    $candidateProductVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
    if (($candidateProductVersion -split '\+', 2)[0] -cne $expectedVersion) { throw 'Fresh installation has an unexpected product version.' }
    Assert-OneRemovableAppEntry
    Invoke-Installer $setup "/uninstall /quiet /norestart /log `"$testRoot\unsupported-setup-uninstall.log`"" -ExpectFailure
    Assert-CandidateInstalled
    Assert-OneRemovableAppEntry
    $running = Start-TestApplication
    Invoke-Installer $setup "/repair /quiet /norestart /log `"$testRoot\repair.log`""
    Assert-Stopped $running
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    $running = Start-TestApplication
    Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\uninstall.log`""
    Assert-Stopped $running
    if (Test-Path -LiteralPath $app) { throw 'Uninstall left the application executable.' }
    Assert-BundleCount 0
    Assert-DataPreserved

    # Upgrade the preceding public MSI while its application is running.
    $name = "resodrive-win-x64-$PreviousVersion.msi"
    $previous = Join-Path $testRoot $name
    $url = "https://github.com/alphasixtyfive/ResoDrive/releases/download/v$PreviousVersion/$name"
    Invoke-WebRequest -Uri $url -OutFile $previous
    $checksum = (Invoke-WebRequest -Uri "$url.sha256").Content
    if ($checksum -is [byte[]]) { $checksum = [Text.Encoding]::ASCII.GetString($checksum) }
    $match = [regex]::Match($checksum.Trim(), '\A([0-9a-fA-F]{64})\s+\*?(.+)\z')
    if (-not $match.Success -or $match.Groups[2].Value -cne $name -or
        (Get-FileHash -LiteralPath $previous).Hash -ine $match.Groups[1].Value) { throw 'Previous installer checksum is invalid.' }
    Invoke-Installer $msiexec "/i `"$previous`" /quiet /norestart /l*v `"$testRoot\previous.log`""
    $running = Start-TestApplication
    Invoke-Installer $msiexec "/i `"$candidateMsi`" /quiet /norestart /l*v `"$testRoot\upgrade.log`""
    Assert-Stopped $running
    Assert-CandidateInstalled
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    $running = Start-TestApplication
    Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\upgrade-uninstall.log`""
    Assert-Stopped $running
    if (Test-Path -LiteralPath $app) { throw 'Upgraded application was not removed.' }
    Assert-DataPreserved

    # A legacy Setup owns a hidden MSI. Reject MSI-only migration before any
    # preparation or replacement, then let native Burn remove its own old entry.
    $previousSetupName = "resodrive-win-x64-$PreviousVersion-setup.exe"
    $previousSetup = Join-Path $testRoot $previousSetupName
    $previousSetupUrl = "https://github.com/alphasixtyfive/ResoDrive/releases/download/v$PreviousVersion/$previousSetupName"
    Invoke-WebRequest -Uri $previousSetupUrl -OutFile $previousSetup
    Invoke-WebRequest -Uri "$previousSetupUrl.sha256" -OutFile ($previousSetup + '.sha256')
    Assert-VerifiedAsset $previousSetup | Out-Null
    Invoke-Installer $previousSetup "/install /quiet /norestart /log `"$testRoot\legacy-setup.log`""
    Assert-BundleCount 1
    $legacyHash = (Get-FileHash -LiteralPath $app).Hash
    $running = Start-TestApplication
    $blockedLog = Join-Path $testRoot 'legacy-msi-blocked.log'
    Invoke-Installer $msiexec "/i `"$candidateMsi`" /quiet /norestart /l*v `"$blockedLog`"" -ExpectFailure
    if ((Get-FileHash -LiteralPath $app).Hash -ine $legacyHash -or $running.HasExited) {
        throw 'The rejected MSI migration changed or stopped the legacy installation.'
    }
    if (-not (Select-String -LiteralPath $blockedLog -Pattern 'Use ResoDrive-Setup\.exe to update this installation' -Quiet) -or
        (Select-String -LiteralPath $blockedLog -Pattern 'Doing action: PrepareInstalledResoDriveForUpgrade' -Quiet)) {
        throw 'MSI-only legacy migration did not stop at its actionable launch condition.'
    }
    Assert-BundleCount 1
    Assert-DataPreserved
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\legacy-setup-migration.log`""
    Assert-Stopped $running
    Assert-CandidateInstalled
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    $running = Start-TestApplication
    # /f ignores command-line properties, including this fixture's custom root.
    # Reinstall maintenance accepts the root and exercises the same native repair.
    Invoke-Installer $msiexec "/i `"$candidateMsi`" REINSTALL=ALL REINSTALLMODE=amus /quiet /norestart /l*v `"$testRoot\migrated-msi-repair.log`""
    Assert-Stopped $running
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\migrated-msi-uninstall.log`""
    if (Test-Path -LiteralPath $app) { throw 'The migrated MSI could not remove the application.' }
    Assert-BundleCount 0
    Assert-DataPreserved
    if ($sameVersionRecovery) {
        # Direct MSI recovery and Burn bundle recovery must each replace the old
        # registration, even though their displayed three-part version is unchanged.
        Assert-BundleCount 0
        Invoke-Installer $msiexec "/i `"$baselineMsi`" /quiet /norestart /l*v `"$testRoot\same-version-msi-baseline.log`""
        Assert-OneRelatedMsiProduct $baselineProductCode
        $baselineAppHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
        if ($baselineAppHash -ieq $candidateAppHash) { throw 'The baseline executable already matches the candidate; replacement was not exercised.' }
        $running = Start-TestApplication
        Invoke-Installer $msiexec "/i `"$candidateMsi`" /quiet /norestart /l*v `"$testRoot\same-version-msi-replace.log`""
        Assert-Stopped $running
        Assert-CandidateInstalled
        Assert-OneRelatedMsiProduct $candidateProductCode
        Assert-BundleCount 0
        Assert-DataPreserved
        Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\same-version-msi-uninstall.log`""
        if (Test-Path -LiteralPath $app) { throw 'Same-version MSI uninstall left the application.' }
        if (@(Get-RelatedMsiProducts $sameVersionUpgradeCode).Count -ne 0) { throw 'Same-version MSI recovery left a registered product.' }
        Assert-DataPreserved

        Invoke-Installer $baselineSetup "/install /quiet /norestart /log `"$testRoot\same-version-bundle-baseline.log`""
        Assert-OneRelatedMsiProduct $baselineProductCode
        Assert-BundleCount 1
        if ((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash -ine $baselineAppHash) { throw 'The baseline bundle and MSI contain different executables.' }
        $running = Start-TestApplication
        Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\same-version-bundle-replace.log`""
        Assert-Stopped $running
        Assert-CandidateInstalled
        Assert-OneRelatedMsiProduct $candidateProductCode
        Assert-BundleCount 0
        Assert-DataPreserved
        Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\same-version-bundle-uninstall.log`""
        if (Test-Path -LiteralPath $app) { throw 'Same-version bundle uninstall left the application.' }
        if (@(Get-RelatedMsiProducts $sameVersionUpgradeCode).Count -ne 0) { throw 'Same-version bundle recovery left a registered MSI product.' }
        Assert-BundleCount 0
        Assert-DataPreserved
        $sameVersionReceipt['BaselineApplicationSha256'] = $baselineAppHash
        $sameVersionReceipt['InstalledApplicationSha256'] = $candidateAppHash
        $sameVersionReceipt['InstalledProductVersion'] = $candidateProductVersion
        $sameVersionReceipt['MsiReplacementPassed'] = $true
        $sameVersionReceipt['BundleReplacementPassed'] = $true
        $sameVersionReceipt['UserDataPreserved'] = $true
        $sameVersionReceipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'same-version-result.json') -Encoding utf8
        Write-Output 'Same-version smoke passed: exact MSI/Setup replacement, one removable MSI entry and preserved data.'
    }
    [ordered]@{
        Passed = $true
        Version = $expectedVersion
        PreviousPublicVersion = $PreviousVersion
        OneRemovableMsiEntry = $true
        RetainedSetupBundleEntries = 0
        DirectMsiUpgradePassed = $true
        LegacyMsiUpgradeRejectedBeforePreparation = $true
        LegacySetupMigrationPassed = $true
        RunningAppRepairAndRemovalPassed = $true
        UserDataPreserved = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'installer-ownership-result.json') -Encoding utf8
    Write-Output 'Installer smoke passed: one removable app entry, fresh install, running-app repair/removal, direct MSI upgrade, blocked legacy MSI migration, native Setup migration and preserved user data.'
} finally {
    try {
        foreach ($diagnosticRoot in @($env:RDRIVE_DATA_DIR, (Join-Path $env:LOCALAPPDATA 'rdrive'))) {
            foreach ($relative in @('logs\resodrive-ui.log', 'updates\installer-preparation.json')) {
                $diagnostic = Join-Path $diagnosticRoot $relative
                if (Test-Path -LiteralPath $diagnostic) {
                    $prefix = if ($diagnosticRoot -eq $env:RDRIVE_DATA_DIR) { 'isolated-' } else { 'default-' }
                    Copy-Item -LiteralPath $diagnostic -Destination (Join-Path $testRoot ($prefix + [IO.Path]::GetFileName($diagnostic) + '.log'))
                }
            }
        }
    } finally { $env:RDRIVE_DATA_DIR = $oldDataRoot }
}

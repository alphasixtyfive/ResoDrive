param(
    [Parameter(Mandatory)][string]$SetupPath,
    [string]$PreviousVersion = '',
    [string]$LegacyUpdaterVersion = '0.3.30',
    [string]$LegacySetupVersion = '0.3.20',
    [string]$SameVersionBaselineMsiPath = '',
    [string]$SameVersionBaselineSetupPath = '',
    [string]$CandidateMsiPath = '',
    [switch]$CompatibilityMode
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
if ($PreviousVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$PreviousVersion -ge $targetVersion) { throw 'The previous public version must be older than the candidate.' }
if ($LegacyUpdaterVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$LegacyUpdaterVersion -ge $targetVersion) { throw 'The frozen legacy updater must be older than the candidate.' }
if ($LegacySetupVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$LegacySetupVersion -ge $targetVersion) { throw 'The hidden-MSI Setup fixture must be an older published version.' }
Write-Output "Testing upgrade from public ResoDrive $PreviousVersion."
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$app = Join-Path $env:ProgramFiles 'ResoDrive\resodrive.exe'
$legacyApp = Join-Path $env:ProgramFiles 'rdrive\resodrive.exe'
$launcher = Join-Path $env:ProgramFiles 'ResoDrive\resodrive-launcher.exe'
if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $legacyApp)) { throw 'Refusing to overwrite a pre-existing installation.' }
$testRoot = Join-Path $env:RUNNER_TEMP $(if ($CompatibilityMode) { 'resodrive-installer-smoke-compatibility' } else { 'resodrive-installer-smoke' })
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
# Disposable fixture only: use the real persisted filenames and unchanged DPAPI
# entropy, without a configured remote or an enabled mount.
$config = Join-Path $env:RDRIVE_DATA_DIR 'rclone.conf'
$secret = Join-Path $env:RDRIVE_DATA_DIR 'config-pass.dpapi'
[IO.File]::WriteAllText($config, '', [Text.Encoding]::ASCII)
$fixtureSecret = [Text.Encoding]::UTF8.GetBytes('disposable-installer-smoke-password')
$fixtureEntropy = [Text.Encoding]::UTF8.GetBytes('rdrive/rclone/config-password/v1')
$protectedSecret = [Security.Cryptography.ProtectedData]::Protect($fixtureSecret, $fixtureEntropy,
    [Security.Cryptography.DataProtectionScope]::CurrentUser)
[IO.File]::WriteAllText($secret, [Convert]::ToBase64String($protectedSecret), [Text.Encoding]::ASCII)
$configHash = (Get-FileHash -LiteralPath $config).Hash
$secretHash = (Get-FileHash -LiteralPath $secret).Hash

$werKeyPath = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
function Get-WerPolicySnapshot([string]$KeyPath) {
    $base = $key = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $key = $base.OpenSubKey($KeyPath)
        if ($null -eq $key) { return '<absent>' }
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            [ordered]@{
                Name = $_
                Kind = $key.GetValueKind($_).ToString()
                Value = $key.GetValue($_, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        })
        return ConvertTo-Json -InputObject $values -Depth 5 -Compress
    } finally {
        if ($null -ne $key) { $key.Dispose() }
        if ($null -ne $base) { $base.Dispose() }
    }
}
function Get-GlobalWerPolicySnapshot {
    $snapshot = Get-WerPolicySnapshot $werKeyPath
    # Creating/removing a child key can leave an empty parent; only global
    # policy values matter, not the existence of that empty container.
    if ($snapshot -ceq '<absent>') { return '[]' }
    return $snapshot
}
$initialGlobalWerPolicy = Get-GlobalWerPolicySnapshot
if ((Get-WerPolicySnapshot "$werKeyPath\resodrive.exe") -cne '<absent>') {
    throw 'The disposable runner has an existing ResoDrive WER policy; leave administrator settings unchanged.'
}
function Assert-WerPolicy {
    if ((Get-GlobalWerPolicySnapshot) -cne $initialGlobalWerPolicy) {
        throw 'Installation changed the global Windows Error Reporting policy.'
    }
    $base = $key = $owner = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $key = $base.OpenSubKey("$werKeyPath\resodrive.exe")
        $owner = $base.OpenSubKey('SOFTWARE\ResoDrive\Diagnostics')
        if ($null -eq $key -or $null -eq $owner -or $owner.GetValue('LocalDumpsOwned') -ne 1) {
            throw 'The installed candidate did not create its owned per-executable WER policy.'
        }
        if ($key.GetValueKind('DumpFolder') -ne [Microsoft.Win32.RegistryValueKind]::ExpandString -or
            $key.GetValue('DumpFolder', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne '%LOCALAPPDATA%\rdrive-diagnostics\dumps' -or
            $key.GetValueKind('DumpCount') -ne [Microsoft.Win32.RegistryValueKind]::DWord -or $key.GetValue('DumpCount') -ne 3 -or
            $key.GetValueKind('DumpType') -ne [Microsoft.Win32.RegistryValueKind]::DWord -or $key.GetValue('DumpType') -ne 2) {
            throw 'Installed WER policy does not contain user-expanded bounded full-dump settings.'
        }
    } finally {
        foreach ($item in @($owner, $key, $base)) { if ($null -ne $item) { $item.Dispose() } }
    }
}
function Assert-WerPolicyRemoved {
    $snapshot = Get-WerPolicySnapshot "$werKeyPath\resodrive.exe"
    if ($snapshot -cnotin @('<absent>', '[]')) {
        throw 'Uninstall left installer-owned per-executable Windows Error Reporting settings.'
    }
    if ((Get-GlobalWerPolicySnapshot) -cne $initialGlobalWerPolicy) {
        throw 'Uninstall changed the pre-existing global Windows Error Reporting policy.'
    }
}

function Invoke-Installer([string]$Executable, [string]$Arguments, [switch]$ExpectFailure, [switch]$LegacyUpdater, [switch]$PreserveSetupMode) {
    # Windows Installer's service does not inherit process-local RDRIVE_DATA_DIR.
    # Pass the isolated root through the supported bundle/MSI property instead.
    if ([IO.Path]::GetFileName($Executable) -ieq 'msiexec.exe') {
        $Arguments += " RDRIVE_DATA_ROOT=`"$([IO.Path]::TrimEndingDirectorySeparator($env:RDRIVE_DATA_DIR))\.`""
        if (-not $LegacyUpdater) { $Arguments += ' RDRIVE_MIGRATE_INSTALL=1' }
    } else {
        $Arguments += " ResoDriveDataRoot=`"$env:RDRIVE_DATA_DIR`""
        # Only the candidate supports this contract. Historical fixtures must
        # retain their exact published command-line behavior. Repair exercises
        # detection of the remembered mode without an explicit override.
        if ($Executable -ieq $setup -and -not $PreserveSetupMode -and
            $Arguments -notmatch '(?i)\bResoDriveCompatibilityMode=') {
            if ($CompatibilityMode) { $Arguments += ' ResoDriveCompatibilityMode=1' }
        }
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
        (Get-FileHash -LiteralPath $managedCopy).Hash -ne $managedCopyHash -or
        (Get-FileHash -LiteralPath $config).Hash -ne $configHash -or
        (Get-FileHash -LiteralPath $secret).Hash -ne $secretHash) { throw 'Installation changed user settings, cache, managed copies or disposable credential/config fixtures.' }
    $decoded = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([IO.File]::ReadAllText($secret)), $fixtureEntropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ([Text.Encoding]::UTF8.GetString($decoded) -cne 'disposable-installer-smoke-password') { throw 'The preserved DPAPI secret no longer decrypts.' }
}
function Start-TestApplication([string]$Executable = $app) {
    if (-not (Test-Path -LiteralPath $Executable)) { throw 'The installed app is missing.' }
    $process = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) { throw 'The installed application exited unexpectedly.' }
    return $process
}
function Assert-Stopped($Process, [string]$Executable = $app) {
    try {
        if (-not $Process.WaitForExit(15000)) { throw 'Installer left the old application running.' }
        $remaining = @(Get-Process resodrive -ErrorAction SilentlyContinue | Where-Object Path -EQ $Executable)
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

function Assert-OneRemovableAppEntry([string]$ProductCode = $candidateProductCode, [string]$Version = $expectedVersion) {
    Assert-OneRelatedMsiProduct $ProductCode
    Assert-BundleCount 0
    $entry = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$ProductCode"
    $hidden = $entry.PSObject.Properties['SystemComponent']
    if (($null -ne $hidden -and $hidden.Value -eq 1) -or $entry.DisplayVersion -ne $Version) {
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

function Get-VerifiedPublicMsi([string]$Version) {
    $name = "resodrive-win-x64-$Version.msi"
    $path = Join-Path $testRoot $name
    $url = "https://github.com/alphasixtyfive/ResoDrive/releases/download/v$Version/$name"
    Invoke-WebRequest -Uri $url -OutFile $path
    Invoke-WebRequest -Uri "$url.sha256" -OutFile ($path + '.sha256')
    Assert-VerifiedAsset $path | Out-Null
    if ((Get-MsiProperty $path 'ProductVersion') -cne $Version -or
        (Get-MsiProperty $path 'UpgradeCode') -ine $sameVersionUpgradeCode) {
        throw 'The published MSI does not match the requested version and product family.'
    }
    return $path
}

function Assert-CandidateInstalled([string]$Executable = $app) {
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $Executable) 'resodrive-launcher.exe') -PathType Leaf)) { throw 'The installed native launcher is missing.' }
    Assert-WerPolicy
    if ((Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash -ine $candidateAppHash -or
        (Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion -cne $candidateProductVersion) {
        throw 'The installed executable does not match the exact candidate bundle payload.'
    }
    Assert-NativeInstalledLocator $Executable $expectedVersion
    Assert-CompatibilityMode
}

function Assert-CompatibilityMode {
    $base = $key = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $key = $base.OpenSubKey('SOFTWARE\ResoDrive\Installation')
        $expectedMode = if ($CompatibilityMode) { 'cet-disabled' } else { 'standard' }
        if ($null -eq $key -or $key.GetValueKind('CompatibilityMode') -ne [Microsoft.Win32.RegistryValueKind]::String -or
            $key.GetValue('CompatibilityMode') -cne $expectedMode) {
            throw 'The installed CET compatibility choice was not preserved.'
        }
    } finally {
        if ($null -ne $key) { $key.Dispose() }
        if ($null -ne $base) { $base.Dispose() }
    }
}

function Assert-NativeInstalledLocator([string]$Executable, [string]$Version) {
    $oldPath = $env:RDRIVE_TEST_INSTALLED_MSI_PATH
    $oldVersion = $env:RDRIVE_TEST_INSTALLED_MSI_VERSION
    try {
        $env:RDRIVE_TEST_INSTALLED_MSI_PATH = $Executable
        $env:RDRIVE_TEST_INSTALLED_MSI_VERSION = $Version
        dotnet test (Join-Path $PSScriptRoot 'ResoDrive.Windows.Tests') -c Release --no-build --no-restore `
            --filter FullyQualifiedName~NativeWindowsInstallerCatalogFindsExactIsolatedInstalledApplication
        if ($LASTEXITCODE -ne 0) { throw 'Real Windows Installer registration lookup failed.' }
    } finally {
        $env:RDRIVE_TEST_INSTALLED_MSI_PATH = $oldPath
        $env:RDRIVE_TEST_INSTALLED_MSI_VERSION = $oldVersion
    }
}

function Assert-MigrationReceipt {
    $receipt = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\ResoDrive\Installation'
    if ([IO.Path]::TrimEndingDirectorySeparator($receipt.LegacyInstallLocation) -ine (Split-Path -Parent $legacyApp) -or
        [IO.Path]::TrimEndingDirectorySeparator($receipt.InstallLocation) -ine (Split-Path -Parent $app) -or
        $receipt.UpgradeCode.Trim('{}') -ine $sameVersionUpgradeCode.Trim('{}')) { throw 'The installed migration receipt does not prove the exact related-product relocation.' }
}

$startupFixtureCreated = $false
$startupFixtureName = $null
function Get-StartupFixtureFolder {
    $service = New-Object -ComObject Schedule.Service
    try { $service.Connect(); return $service.GetFolder('\') }
    finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null }
}
function Test-StartupFixtureExists($Folder) {
    $tasks = $Folder.GetTasks(1) # Include disabled tasks.
    try {
        for ($index = 1; $index -le $tasks.Count; $index++) {
            $task = $tasks.Item($index)
            try { if ($task.Name -ceq $startupFixtureName) { return $true } }
            finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null }
        }
        return $false
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($tasks) | Out-Null }
}
function New-DisabledStartupFixture {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sid)))
    $script:startupFixtureName = 'ResoDrive Startup - ' + $hash.Substring(0, 12)
    $folder = Get-StartupFixtureFolder
    try {
        if (Test-StartupFixtureExists $folder) {
            throw 'Refusing to replace a pre-existing startup task on the hosted runner.'
        }
        $command = [Security.SecurityElement]::Escape($legacyApp)
        $directory = [Security.SecurityElement]::Escape((Split-Path -Parent $legacyApp))
        $xml = @"
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts ResoDrive for this user at sign-in. Managed by ResoDrive.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$sid</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="CurrentUser"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><Enabled>false</Enabled></Settings>
  <Actions Context="CurrentUser"><Exec><Command>$command</Command><Arguments>--background</Arguments><WorkingDirectory>$directory</WorkingDirectory></Exec></Actions>
</Task>
"@
        # TASK_CREATE only; never replace an unrelated definition after the check.
        $registered = $folder.RegisterTask($startupFixtureName, $xml, 2, $null, $null, 3, $null)
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($registered) | Out-Null
        $script:startupFixtureCreated = $true
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null }
}
function Resolve-StartupFixtureSid([string]$UserId) {
    if ([string]::IsNullOrWhiteSpace($UserId)) { throw 'The startup task has no Windows user identity.' }
    try { return [Security.Principal.SecurityIdentifier]::new($UserId).Value }
    catch [ArgumentException] {
        return ([Security.Principal.NTAccount]::new($UserId)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
}
function Assert-MigratedStartupDefinition([xml]$Xml, [string]$ExpectedLauncher, [string]$ExpectedSid) {
    $ns = [Xml.XmlNamespaceManager]::new($Xml.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $actions = $Xml.SelectNodes('/t:Task/t:Actions/*', $ns)
    $principals = $Xml.SelectNodes('/t:Task/t:Principals/*', $ns)
    $triggers = $Xml.SelectNodes('/t:Task/t:Triggers/*', $ns)
    if ($actions.Count -ne 1 -or $actions[0].LocalName -cne 'Exec' -or
        $principals.Count -ne 1 -or $principals[0].LocalName -cne 'Principal' -or
        $triggers.Count -ne 1 -or $triggers[0].LocalName -cne 'LogonTrigger') {
        throw 'Migration changed the single startup action, principal or logon trigger.'
    }
    # Scheduler XML can store DOMAIN\user instead of a SID and omit the default
    # least-privilege RunLevel. Resolve identity semantically without broadening
    # ownership to another account or accepting an elevated task.
    $principalSid = Resolve-StartupFixtureSid $principals[0].SelectSingleNode('t:UserId', $ns).InnerText
    $triggerSid = Resolve-StartupFixtureSid $triggers[0].SelectSingleNode('t:UserId', $ns).InnerText
    $runLevel = $principals[0].SelectSingleNode('t:RunLevel', $ns)
    if ($principalSid -cne $ExpectedSid -or $triggerSid -cne $ExpectedSid -or
        ($null -ne $runLevel -and -not [string]::IsNullOrEmpty($runLevel.InnerText) -and $runLevel.InnerText -cne 'LeastPrivilege') -or
        $principals[0].SelectSingleNode('t:LogonType', $ns).InnerText -cne 'InteractiveToken' -or
        $actions[0].SelectSingleNode('t:Command', $ns).InnerText -ine $ExpectedLauncher -or
        $actions[0].SelectSingleNode('t:Arguments', $ns).InnerText -cne '--background' -or
        $Xml.SelectSingleNode('/t:Task/t:RegistrationInfo/t:Description', $ns).InnerText -cne 'Starts ResoDrive for this user at sign-in. Managed by ResoDrive.') {
        throw 'The receipt-backed startup migration did not preserve the exact user, privilege, activation and ownership contract.'
    }
}
function Assert-DisabledStartupFixtureMigrated {
    $folder = Get-StartupFixtureFolder
    $task = $null
    try {
        $task = $folder.GetTask($startupFixtureName)
        if ($task.Enabled) { throw 'Migration enabled a disabled startup task.' }
        Assert-MigratedStartupDefinition ([xml]$task.Xml) $launcher ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    } finally {
        foreach ($item in @($task, $folder)) { if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null } }
    }
}
function Remove-StartupFixture {
    if (-not $startupFixtureCreated) { return }
    $folder = Get-StartupFixtureFolder
    try { if (Test-StartupFixtureExists $folder) { $folder.DeleteTask($startupFixtureName, 0) } }
    finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null }
}

function Assert-UpdatedUiReady([string]$Executable, [DateTimeOffset]$StartedAfter) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $windows = @(Get-Process resodrive -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -ieq $Executable -and $_.MainWindowHandle -ne 0 -and
                $_.StartTime.ToUniversalTime() -ge $StartedAfter.UtcDateTime
        })
        if ($windows.Count -gt 1) { throw 'The updater reopened more than one UI at the expected installed path.' }
        if ($windows.Count -eq 1) {
            $identity = [ordered]@{
                processId = $windows[0].Id
                processCreatedAtUtc = $windows[0].StartTime.ToUniversalTime().ToString('O')
                mainWindowHandle = $windows[0].MainWindowHandle.ToInt64()
            }
            # This normal secondary launch targets the exact installed directory's
            # activation scope. Exit 0 requires its live, loaded, visible UI to ACK.
            $probe = Start-Process -FilePath $Executable -ArgumentList '--show' -WindowStyle Hidden -PassThru
            try {
                if (-not $probe.WaitForExit(15000) -or $probe.ExitCode -ne 0) { throw 'The updated UI did not acknowledge its exact-directory readiness probe.' }
            } finally { $probe.Dispose() }
            $windows[0].Refresh()
            if ($windows[0].HasExited -or $windows[0].MainWindowHandle -eq 0) { throw 'The updated UI exited or lost its visible window after readiness acknowledgement.' }
            return $identity
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The updater did not reopen a visible UI at the exact expected installed path.'
}

function Read-OptionalFinalUpdateOutcome([string]$Path, [string]$Version) {
    # The relaunched UI intentionally consumes this receipt. Open once with
    # delete-sharing so disappearance after this read cannot invalidate evidence.
    $stream = $null
    try {
        try { $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::Read -bor [IO.FileShare]::Delete) }
        catch [IO.FileNotFoundException] { return $null }
        catch [IO.DirectoryNotFoundException] { return $null }
        if ($stream.Length -gt 64 * 1024) { throw 'The retained update outcome exceeds its expected bound.' }
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 1024, $true)
        try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $result = $json | ConvertFrom-Json
        if ($result.version -cne $Version -or $result.status -cne 'succeeded' -or
            $result.finalized -isnot [bool] -or $result.finalized -ne $true -or
            $result.relaunchAcknowledged -isnot [bool] -or $result.relaunchAcknowledged -ne $true -or
            $result.installerExitCode -notin @(0, 1641, 3010)) { throw 'The retained updater outcome does not confirm successful installation and readiness.' }
        return $result
    } finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Read-IsolatedUpdaterEvidenceBytes([string]$Path, [int]$MaximumBytes) {
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        if ($stream.Length -gt $MaximumBytes) { throw 'Updater evidence exceeds its expected bound.' }
        $buffer = [byte[]]::new($MaximumBytes + 1)
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt $MaximumBytes) { throw 'Updater evidence grew beyond its expected bound.' }
        $result = [byte[]]::new($count)
        [Array]::Copy($buffer, $result, $count)
        return ,$result
    } finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Save-IsolatedUpdaterEvidence([string]$UpdatesDirectory, [string]$Destination, [string]$Version) {
    # Only exact updater receipts and the exact staged-MSI log from this disposable
    # fixture are eligible. Never enumerate or copy account configuration or dumps.
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid updater evidence version.' }
    foreach ($name in @('application-update-result.json', 'application-update-result.json.tmp')) {
        try {
            $bytes = Read-IsolatedUpdaterEvidenceBytes (Join-Path $UpdatesDirectory $name) (64 * 1024)
            $receipt = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
            $evidence = [ordered]@{ expectedTargetVersion = $Version; sourceReceipt = $name }
            foreach ($field in @('Version', 'Status', 'InstallerExitCode', 'RelaunchAcknowledged', 'Finalized', 'RecordedAtUtc', 'Message')) {
                $property = $receipt.PSObject.Properties[$field]
                if ($null -ne $property) {
                    $value = $property.Value
                    if ($value -is [string] -and $value.Length -gt 2048) { $value = $value.Substring(0, 2048) }
                    if ($null -eq $value -or $value -is [string] -or $value -is [bool] -or $value -is [ValueType]) {
                        $evidence[$field] = $value
                    }
                }
            }
            $evidence | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Destination "updater-$Version-$name.evidence.json") -Encoding utf8
        } catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        catch { Write-Host ("Updater receipt evidence unavailable ({0})." -f $_.Exception.GetType().Name) }
    }
    foreach ($logName in @("resodrive-win-x64-$Version.msi.log", "resodrive-win-x64-$Version-compatibility.msi.log")) {
        try {
            $bytes = Read-IsolatedUpdaterEvidenceBytes (Join-Path $UpdatesDirectory $logName) (32 * 1024 * 1024)
            [IO.File]::WriteAllBytes((Join-Path $Destination "updater-$logName"), $bytes)
        } catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        catch { Write-Host ("Updater MSI-log evidence unavailable ({0})." -f $_.Exception.GetType().Name) }
    }
}

function Invoke-CopiedUpdater([string]$Source, [string]$Msi, [string]$Version,
    [string]$ExpectedExecutable, [string]$ExpectedSha256, [switch]$LegacyProtocol) {
    $sourceVersion = (Get-Item -LiteralPath $Source).VersionInfo.ProductVersion
    $updates = Join-Path $env:RDRIVE_DATA_DIR 'updates'
    New-Item -ItemType Directory -Path $updates -Force | Out-Null
    $helper = Join-Path $updates 'resodrive-update-helper.exe'
    # Frozen legacy helpers require the original filename. The new compatibility
    # helper retains its explicit variant in the filename and checksum contract.
    $variantSuffix = if ($CompatibilityMode -and -not $LegacyProtocol) { '-compatibility' } else { '' }
    $stagedMsi = Join-Path $updates "resodrive-win-x64-$Version$variantSuffix.msi"
    $outcome = Join-Path $updates 'application-update-result.json'
    Copy-Item -LiteralPath $Source -Destination $helper -Force
    Copy-Item -LiteralPath $Msi -Destination $stagedMsi -Force
    foreach ($staleOutcome in @($outcome, ($outcome + '.tmp'))) {
        if (Test-Path -LiteralPath $staleOutcome) { Remove-Item -LiteralPath $staleOutcome }
    }
    $parent = Start-Process -FilePath (Join-Path ([Environment]::SystemDirectory) 'cmd.exe') -ArgumentList '/c exit 0' -WindowStyle Hidden -PassThru
    try { $parentId = $parent.Id; $parent.WaitForExit() } finally { $parent.Dispose() }
    $arguments = '--complete-update ' + $Version + ' "' + $stagedMsi + '" "' + $Source + '" "' + $Source + '" "' + $outcome + '" ' +
        (Get-FileHash -LiteralPath $stagedMsi -Algorithm SHA256).Hash + ' ' + $parentId
    $oldHandoff = $env:RDRIVE_UPDATE_HANDOFF_DIR
    $env:RDRIVE_UPDATE_HANDOFF_DIR = $updates
    $startedAtUtc = [DateTimeOffset]::UtcNow
    $helperExitCode = $null
    try {
        $process = Start-Process -FilePath $helper -ArgumentList $arguments -WindowStyle Hidden -PassThru
        try {
            if (-not $process.WaitForExit(240000)) { throw 'Copied updater timed out.' }
            $helperExitCode = $process.ExitCode
            if ($process.ExitCode -ne 0) {
                Save-IsolatedUpdaterEvidence $updates $testRoot $Version
                throw "Copied updater failed ($($process.ExitCode))."
            }
        } finally { $process.Dispose() }
    } finally { $env:RDRIVE_UPDATE_HANDOFF_DIR = $oldHandoff }
    Assert-NativeInstalledLocator $ExpectedExecutable $Version
    Assert-CompatibilityMode
    $actualHash = (Get-FileHash -LiteralPath $ExpectedExecutable -Algorithm SHA256).Hash
    if ($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $actualHash -ine $ExpectedSha256) { throw 'The copied updater did not install the exact expected managed binary.' }
    $ui = Assert-UpdatedUiReady $ExpectedExecutable $startedAtUtc
    $result = Read-OptionalFinalUpdateOutcome $outcome $Version
    $receiptState = 'consumed-by-active-updated-ui'
    if ($null -ne $result) { $receiptState = 'observed-and-validated-final-outcome' }
    # Frozen v0.3.30 CompleteAsync exits 0 only for successful MSI + RequestReady
    # acknowledgement; malformed --complete-update returns 2. The current helper
    # preserves that exit contract. The UI may already have deleted its receipt.
    [ordered]@{
        targetVersion = $Version
        sourceProductVersion = $sourceVersion
        helperExitCode = $helperExitCode
        helperExitContract = '0 requires succeeded MSI and RequestReady acknowledgement'
        expectedExecutable = $ExpectedExecutable
        installedExecutableSha256 = $actualHash
        realInstalledMsiRegistrationVerified = $true
        exactDirectoryReadinessProbeAcknowledged = $true
        updatedUi = $ui
        outcomeReceiptState = $receiptState
        finalOutcome = $result
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot "update-handoff-$Version-$([IO.Path]::GetFileName((Split-Path -Parent $ExpectedExecutable))).json") -Encoding utf8
}

function New-FutureMigrationFixture([int]$VersionOffset = 1) {
    if ($VersionOffset -lt 1 -or $VersionOffset -gt 3) { throw 'Invalid isolated future fixture version offset.' }
    $nextVersion = '{0}.{1}.{2}' -f $targetVersion.Major, $targetVersion.Minor, ($targetVersion.Build + $VersionOffset)
    $root = Join-Path $testRoot "future-fixture-$nextVersion"
    $payload = Join-Path $root 'payload'
    $published = Join-Path $root 'managed'
    $installerIntermediate = (Join-Path $root 'installer-obj') + '/'
    $cetCompat = if ($CompatibilityMode) { 'false' } else { 'true' }
    $compatibilityValue = if ($CompatibilityMode) { 'true' } else { 'false' }
    $installationMode = if ($CompatibilityMode) { 'cet-disabled' } else { 'standard' }
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -Path (Join-Path $candidateArtifactRoot 'resodrive\*') -Destination $payload -Recurse -Force
    dotnet publish (Join-Path $PSScriptRoot '..\src\ResoDrive.App\ResoDrive.App.csproj') -c Release -r win-x64 --self-contained false --no-restore `
        --output $published "-p:VersionPrefix=$nextVersion" "-p:CETCompat=$cetCompat" "-p:ResoDriveCompatibilityMode=$compatibilityValue" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Temporary future fixture publish failed.' }
    Get-ChildItem -LiteralPath $published -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'packages.lock.json' } |
        Copy-Item -Destination $payload -Force
    . (Join-Path $PSScriptRoot '..\installer\ComponentIdentity.ps1')
    $props = $project.SelectSingleNode('/Project/PropertyGroup')
    dotnet build (Join-Path $PSScriptRoot '..\installer\ResoDrive.Installer.wixproj') -c Release --no-restore --output $root `
        "-p:IntermediateOutputPath=$installerIntermediate" `
        "-p:PackageSource=$payload" "-p:ResoDriveVersion=$nextVersion" '-p:ResoDriveRuntime=win-x64' `
        "-p:ResoDriveComponentSeed=$(Get-ResoDriveComponentSeed $nextVersion)" "-p:ResoDriveUpgradeCode=$sameVersionUpgradeCode" `
        "-p:ResoDriveProductName=$($props.ProductDisplayName)" "-p:ResoDrivePublisher=$($props.ProductPublisher)" `
        "-p:ResoDriveDescription=$($props.ProductDescription.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C'))" `
        '-p:ResoDriveExecutableBaseName=resodrive' '-p:ResoDriveExecutableName=resodrive.exe' "-p:ResoDriveCompatibilityMode=$compatibilityValue" "-p:ResoDriveInstallationMode=$installationMode" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Temporary future fixture MSI build failed.' }
    return [pscustomobject]@{ Version = $nextVersion; Msi = (Join-Path $root "resodrive-win-x64-$nextVersion.msi");
        AppHash = (Get-FileHash -LiteralPath (Join-Path $payload 'resodrive.exe')).Hash }
}

function New-RollbackFailureFixture([string]$Msi) {
    # Only the disposable copy is changed. Production packages contain no test
    # action or switch. Execute the pending native script before failing so the
    # test exercises replacement and rollback, rather than just script creation.
    $copy = Join-Path $testRoot 'rollback-failure.msi'
    Copy-Item -LiteralPath $Msi -Destination $copy
    $installer = $database = $view = $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($copy, 1)
        $view = $database.OpenView('SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')
        $view.Execute() | Out-Null
        $actions = @{}
        while ($null -ne ($record = $view.Fetch())) {
            $name = [string]$record.StringData(1)
            $actions[$name] = [int]$record.IntegerData(2)
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
            $record = $null
        }
        $view.Close() | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
        $view = $null
        if (-not $actions.ContainsKey('InstallFinalize') -or -not $actions.ContainsKey('InstallInitialize') -or
            -not $actions.ContainsKey('RemoveExistingProducts') -or -not $actions.ContainsKey('WriteRegistryValues') -or
            $actions.ContainsKey('InstallExecute') -or $actions.ContainsKey('InstallExecuteAgain')) {
            throw 'Rollback fixture requires the expected native transaction sequence.'
        }
        $executeSequence = $actions['InstallFinalize'] - 2
        $failureSequence = $executeSequence + 1
        if ($actions['RemoveExistingProducts'] -le $actions['InstallInitialize'] -or
            $actions['RemoveExistingProducts'] -ge $actions['WriteRegistryValues'] -or
            @($actions.Values | Where-Object { $_ -ge $executeSequence -and $_ -lt $actions['InstallFinalize'] }).Count -ne 0) {
            throw 'Rollback fixture could not reserve actions inside the transaction.'
        }
        $queries = @(
            'INSERT INTO `CustomAction` (`Action`, `Type`, `Target`) VALUES (''SmokeRollbackFailure'', 19, ''Intentional disposable rollback fixture failure.'')',
            ('INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (''InstallExecute'', ''NOT Installed AND WIX_UPGRADE_DETECTED'', {0})' -f $executeSequence),
            ('INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (''SmokeRollbackFailure'', ''NOT Installed AND WIX_UPGRADE_DETECTED'', {0})' -f $failureSequence)
        )
        foreach ($query in $queries) {
            $view = $database.OpenView($query)
            $view.Execute() | Out-Null
            $view.Close() | Out-Null
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
            $view = $null
        }
        $database.Commit() | Out-Null
        return $copy
    } finally {
        if ($null -ne $view) { $view.Close() | Out-Null }
        foreach ($item in @($record, $view, $database, $installer)) {
            if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
        }
    }
}

$sameVersionPaths = @($SameVersionBaselineMsiPath, $SameVersionBaselineSetupPath, $CandidateMsiPath)
$sameVersionRecovery = @($sameVersionPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
$bundleUpgradeCode = '{5B94F457-820F-4B41-B609-071179764B08}'
$msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
$candidateSuffix = if ($CompatibilityMode) { '-compatibility' } else { '' }
$candidateArtifactRoot = [IO.Path]::GetDirectoryName($setup)
if ($CompatibilityMode) { $candidateArtifactRoot = Join-Path (Split-Path -Parent $candidateArtifactRoot) 'win-x64-compatibility' }
$candidateMsi = Join-Path $candidateArtifactRoot "resodrive-win-x64-$expectedVersion$candidateSuffix.msi"
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
    Invoke-Installer $setup "/install /quiet /norestart ResoDriveCompatibilityMode=2 /log `"$testRoot\invalid-mode.log`"" -ExpectFailure
    if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $legacyApp) -or
        @(Get-RelatedMsiProducts $sameVersionUpgradeCode).Count -ne 0) {
        throw 'An invalid compatibility choice changed the installation.'
    }
    Assert-DataPreserved
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\fresh.log`""
    $candidateAppHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
    $candidateProductVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
    if (($candidateProductVersion -split '\+', 2)[0] -cne $expectedVersion) { throw 'Fresh installation has an unexpected product version.' }
    Assert-OneRemovableAppEntry
    Assert-WerPolicy
    Assert-NativeInstalledLocator $app $expectedVersion
    Assert-CompatibilityMode
    & (Join-Path $PSScriptRoot 'native-crash-smoke.ps1') -MonitorPath $launcher -VerifyWer -VerifyClipboard
    Assert-DataPreserved
    # Opposite equal-version products must be rejected by both entry points,
    # rather than silently registering two products in one installation root.
    $oppositeSuffix = if ($CompatibilityMode) { '' } else { '-compatibility' }
    $oppositeRoot = Join-Path (Split-Path -Parent ([IO.Path]::GetDirectoryName($setup))) $(if ($CompatibilityMode) { 'win-x64' } else { 'win-x64-compatibility' })
    $oppositeMsi = Join-Path $oppositeRoot "resodrive-win-x64-$expectedVersion$oppositeSuffix.msi"
    if ((Get-MsiProperty $oppositeMsi 'UpgradeCode') -ine $sameVersionUpgradeCode -or
        (Get-MsiProperty $oppositeMsi 'ProductCode') -ieq $candidateProductCode) {
        throw 'The two modes must have distinct products in the same application family.'
    }
    $oppositeMode = if ($CompatibilityMode) { 0 } else { 1 }
    Invoke-Installer $setup "/install /quiet /norestart ResoDriveCompatibilityMode=$oppositeMode /log `"$testRoot\blocked-mode-switch.log`"" -ExpectFailure
    Invoke-Installer $msiexec "/i `"$oppositeMsi`" /quiet /norestart /l*v `"$testRoot\blocked-opposite-msi.log`"" -ExpectFailure
    Assert-CandidateInstalled
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    Invoke-Installer $setup "/uninstall /quiet /norestart /log `"$testRoot\unsupported-setup-uninstall.log`"" -ExpectFailure
    Assert-CandidateInstalled
    Assert-OneRemovableAppEntry
    $running = Start-TestApplication
    Invoke-Installer $setup "/repair /quiet /norestart /log `"$testRoot\repair.log`"" -PreserveSetupMode
    Assert-Stopped $running
    Assert-WerPolicy
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    $running = Start-TestApplication
    Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\uninstall.log`""
    Assert-Stopped $running
    if (Test-Path -LiteralPath $app) { throw 'Uninstall left the application executable.' }
    Assert-WerPolicyRemoved
    Assert-BundleCount 0
    Assert-DataPreserved

    # Cover the actual previous public MSI independently of the frozen updater
    # protocol. Its fresh installation can be either legacy or canonical.
    $latestPublic = Get-VerifiedPublicMsi $PreviousVersion
    $latestPublicCode = Get-MsiProperty $latestPublic 'ProductCode'
    Invoke-Installer $msiexec "/i `"$latestPublic`" /quiet /norestart /l*v `"$testRoot\latest-public-baseline.log`""
    Assert-OneRemovableAppEntry $latestPublicCode $PreviousVersion
    $latestPublicApps = @(@($app, $legacyApp) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($latestPublicApps.Count -ne 1) { throw 'The previous public MSI must install exactly one application.' }
    $latestPublicApp = $latestPublicApps[0]
    Assert-NativeInstalledLocator $latestPublicApp $PreviousVersion
    Assert-DataPreserved
    $running = Start-TestApplication $latestPublicApp
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\latest-public-upgrade.log`""
    Assert-Stopped $running $latestPublicApp
    Assert-CandidateInstalled
    Assert-OneRemovableAppEntry
    if (Test-Path -LiteralPath $legacyApp) { throw 'The current Setup left an old application after the latest public upgrade.' }
    Assert-DataPreserved
    $running = Start-TestApplication
    Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\latest-public-uninstall.log`""
    Assert-Stopped $running
    if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $legacyApp)) { throw 'The latest public upgrade uninstall left an application.' }
    Assert-WerPolicyRemoved
    if (@(Get-RelatedMsiProducts $sameVersionUpgradeCode).Count -ne 0) { throw 'The latest public upgrade uninstall left a registered product.' }
    Assert-BundleCount 0
    Assert-DataPreserved

    # Frozen older copied updaters must retain their original activation path.
    # A current helper then relocates an independently published future version.
    $previous = if ($LegacyUpdaterVersion -ceq $PreviousVersion) { $latestPublic } else { Get-VerifiedPublicMsi $LegacyUpdaterVersion }
    Invoke-Installer $msiexec "/i `"$previous`" /quiet /norestart /l*v `"$testRoot\previous.log`""
    if (-not (Test-Path -LiteralPath $legacyApp) -or (Test-Path -LiteralPath $app)) { throw 'The frozen legacy updater baseline must install into rdrive.' }
    $running = Start-TestApplication $legacyApp
    Invoke-CopiedUpdater $legacyApp $candidateMsi $expectedVersion $legacyApp $candidateAppHash -LegacyProtocol
    if (-not $running.WaitForExit(15000)) { throw 'The old UI survived its copied updater.' }
    $running.Dispose()
    Assert-CandidateInstalled $legacyApp
    if (Test-Path -LiteralPath $app) { throw 'The legacy updater prematurely moved its activation path.' }
    Assert-OneRemovableAppEntry
    Assert-DataPreserved

    # Maintenance must restore the own-product registered location even though
    # Setup/MSI authoring defaults to ResoDrive and repair passes migration=1.
    $current = @(Get-Process resodrive -ErrorAction SilentlyContinue | Where-Object Path -EQ $legacyApp | Where-Object MainWindowHandle -NE 0)
    if ($current.Count -ne 1) { throw 'The compatibility updater did not relaunch exactly one UI.' }
    # Windows Installer records the source filename used at installation. The
    # frozen updater intentionally stages the original name, even when supplied
    # the compatibility fixture. Repair must use that same verified source.
    $repairMsi = Join-Path $env:RDRIVE_DATA_DIR "updates\resodrive-win-x64-$expectedVersion.msi"
    if ((Get-FileHash -LiteralPath $repairMsi).Hash -ine (Get-FileHash -LiteralPath $candidateMsi).Hash) {
        throw 'The legacy-updater repair source differs from the accepted candidate.'
    }
    Invoke-Installer $msiexec "/i `"$repairMsi`" REINSTALL=ALL REINSTALLMODE=amus /quiet /norestart /l*v `"$testRoot\compatibility-repair.log`""
    Assert-Stopped $current[0] $legacyApp
    Assert-CandidateInstalled $legacyApp
    if (Test-Path -LiteralPath $app) { throw 'Repair relocated an installed product.' }
    $future = New-FutureMigrationFixture
    $futureCode = Get-MsiProperty $future.Msi 'ProductCode'
    $legacyUnowned = Join-Path (Split-Path -Parent $legacyApp) 'user-owned-preserve.txt'
    [IO.File]::WriteAllText($legacyUnowned, 'Installer must not recursively delete unrelated old-directory files.')
    New-DisabledStartupFixture
    $running = Start-TestApplication $legacyApp
    Invoke-CopiedUpdater $legacyApp $future.Msi $future.Version $app $future.AppHash
    if (-not $running.WaitForExit(15000)) { throw 'The compatibility UI survived its relocation updater.' }
    $running.Dispose()
    if ((Test-Path -LiteralPath $legacyApp) -or (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $legacyApp) 'resodrive-launcher.exe'))) { throw 'Migration left duplicate old application files.' }
    if (-not (Test-Path -LiteralPath $legacyUnowned)) { throw 'Migration removed an unrelated file in the previous installation folder.' }
    Assert-MigrationReceipt
    Assert-DisabledStartupFixtureMigrated
    Assert-OneRemovableAppEntry $futureCode $future.Version
    Assert-WerPolicy
    if ((Get-FileHash -LiteralPath $app).Hash -ine $future.AppHash) { throw 'The future fixture installed different managed binaries.' }
    Assert-DataPreserved
    # A user can return after several releases with an owned task still pointing
    # at the original folder. Retain that receipt through a canonical upgrade.
    $later = New-FutureMigrationFixture 2
    $laterCode = Get-MsiProperty $later.Msi 'ProductCode'
    Remove-StartupFixture
    New-DisabledStartupFixture
    $current = @(Get-Process resodrive -ErrorAction SilentlyContinue | Where-Object Path -EQ $app | Where-Object MainWindowHandle -NE 0)
    if ($current.Count -ne 1) { throw 'The first canonical updater did not relaunch exactly one UI.' }
    Invoke-CopiedUpdater $app $later.Msi $later.Version $app $later.AppHash
    try {
        if (-not $current[0].WaitForExit(15000)) { throw 'The first canonical UI survived the next copied updater.' }
    } finally { $current[0].Dispose() }
    Assert-MigrationReceipt
    Assert-DisabledStartupFixtureMigrated
    Assert-OneRemovableAppEntry $laterCode $later.Version
    Assert-WerPolicy
    Assert-DataPreserved
    if ((Test-Path -LiteralPath $legacyApp) -or -not (Test-Path -LiteralPath $legacyUnowned)) { throw 'The next canonical upgrade changed the old folder unexpectedly.' }
    $rollback = New-FutureMigrationFixture 3
    $rollbackMsi = New-RollbackFailureFixture $rollback.Msi
    $rollbackLog = Join-Path $testRoot 'transactional-rollback.log'
    $priorWerPolicy = Get-WerPolicySnapshot "$werKeyPath\resodrive.exe"
    $current = @(Get-Process resodrive -ErrorAction SilentlyContinue | Where-Object Path -EQ $app | Where-Object MainWindowHandle -NE 0)
    if ($current.Count -ne 1) { throw 'The next canonical updater did not relaunch exactly one UI.' }
    Invoke-Installer $msiexec "/i `"$rollbackMsi`" /quiet /norestart /l*v `"$rollbackLog`"" -ExpectFailure
    Assert-Stopped $current[0]
    $rollbackText = [IO.File]::ReadAllText($rollbackLog)
    $removed = [regex]::Match($rollbackText, 'Action ended [^\r\n]*RemoveExistingProducts\. Return value 1\.')
    $executed = [regex]::Match($rollbackText, 'Action ended [^\r\n]*InstallExecute\. Return value 1\.')
    $failed = [regex]::Match($rollbackText, 'Action ended [^\r\n]*SmokeRollbackFailure\. Return value 3\.')
    if (-not $removed.Success -or -not $executed.Success -or -not $failed.Success -or
        $removed.Index -ge $executed.Index -or $executed.Index -ge $failed.Index -or
        $rollbackText -notmatch 'Executing op: RollbackInfo') {
        throw 'The expected rollback did not follow actual old-product removal and replacement execution.'
    }
    Assert-OneRemovableAppEntry $laterCode $later.Version
    Assert-NativeInstalledLocator $app $later.Version
    if ((Get-FileHash -LiteralPath $app).Hash -ine $later.AppHash -or
        (Get-WerPolicySnapshot "$werKeyPath\resodrive.exe") -cne $priorWerPolicy) {
        throw 'Rollback did not restore the exact previous binary and WER policy.'
    }
    Assert-MigrationReceipt
    Assert-DisabledStartupFixtureMigrated
    Assert-WerPolicy
    Assert-DataPreserved
    if ((Test-Path -LiteralPath $legacyApp) -or -not (Test-Path -LiteralPath $legacyUnowned)) { throw 'Rollback changed unrelated legacy-folder contents.' }
    $running = Start-TestApplication
    Invoke-Installer $msiexec "/x $laterCode /quiet /norestart /l*v `"$testRoot\future-uninstall.log`""
    Assert-Stopped $running
    if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $legacyApp)) { throw 'Migration uninstall left an executable.' }
    Assert-WerPolicyRemoved
    Assert-DataPreserved
    Remove-Item -LiteralPath $legacyUnowned

    # Skipping this candidate must still preserve .30's hardcoded activation path.
    Invoke-Installer $msiexec "/i `"$previous`" /quiet /norestart /l*v `"$testRoot\skip-baseline.log`""
    $running = Start-TestApplication $legacyApp
    Invoke-CopiedUpdater $legacyApp $future.Msi $future.Version $legacyApp $future.AppHash -LegacyProtocol
    if (-not $running.WaitForExit(15000)) { throw 'Skipped-version updater left the old UI alive.' }
    $running.Dispose()
    Assert-OneRemovableAppEntry $futureCode $future.Version
    if ((Test-Path -LiteralPath $app) -or (Get-FileHash -LiteralPath $legacyApp).Hash -ine $future.AppHash) { throw 'Skipped-version legacy update failed to preserve its registered folder.' }
    Assert-DataPreserved
    Invoke-Installer $msiexec "/x $futureCode /quiet /norestart /l*v `"$testRoot\skip-uninstall.log`""
    if (Test-Path -LiteralPath $legacyApp) { throw 'Skipped-version uninstall left its executable.' }
    Assert-WerPolicyRemoved
    Assert-DataPreserved

    # A legacy Setup owns a hidden MSI. Reject MSI-only migration before any
    # preparation or replacement, then let native Burn remove its own old entry.
    # v0.3.20 is a frozen published hidden-MSI/Burn fixture. The latest public
    # v0.3.30 Setup already has corrected MSI ownership and cannot cover this case.
    $previousSetupName = "resodrive-win-x64-$LegacySetupVersion-setup.exe"
    $previousSetup = Join-Path $testRoot $previousSetupName
    $previousSetupUrl = "https://github.com/alphasixtyfive/ResoDrive/releases/download/v$LegacySetupVersion/$previousSetupName"
    Invoke-WebRequest -Uri $previousSetupUrl -OutFile $previousSetup
    Invoke-WebRequest -Uri "$previousSetupUrl.sha256" -OutFile ($previousSetup + '.sha256')
    Assert-VerifiedAsset $previousSetup | Out-Null
    Invoke-Installer $previousSetup "/install /quiet /norestart /log `"$testRoot\legacy-setup.log`""
    Assert-BundleCount 1
    $legacyProducts = @(Get-RelatedMsiProducts $sameVersionUpgradeCode)
    if ($legacyProducts.Count -ne 1) { throw 'The legacy Setup did not register exactly one related MSI.' }
    $legacyEntry = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$($legacyProducts[0])"
    if ($legacyEntry.SystemComponent -ne 1 -or $legacyEntry.DisplayVersion -cne $LegacySetupVersion) {
        throw 'The frozen legacy Setup fixture does not own a hidden MSI of the expected version.'
    }
    $legacyHash = (Get-FileHash -LiteralPath $legacyApp).Hash
    $running = Start-TestApplication $legacyApp
    $blockedLog = Join-Path $testRoot 'legacy-msi-blocked.log'
    Invoke-Installer $msiexec "/i `"$candidateMsi`" /quiet /norestart /l*v `"$blockedLog`"" -ExpectFailure
    if ((Get-FileHash -LiteralPath $legacyApp).Hash -ine $legacyHash -or $running.HasExited) {
        throw 'The rejected MSI migration changed or stopped the legacy installation.'
    }
    if (-not (Select-String -LiteralPath $blockedLog -Pattern 'Use ResoDrive-Setup\.exe to update this installation' -Quiet) -or
        (Select-String -LiteralPath $blockedLog -Pattern 'Doing action: PrepareInstalledResoDriveForUpgrade' -Quiet)) {
        throw 'MSI-only legacy migration did not stop at its actionable launch condition.'
    }
    Assert-BundleCount 1
    Assert-DataPreserved
    Invoke-Installer $setup "/install /quiet /norestart /log `"$testRoot\legacy-setup-migration.log`""
    Assert-Stopped $running $legacyApp
    Assert-CandidateInstalled
    Assert-MigrationReceipt
    if (Test-Path -LiteralPath $legacyApp) { throw 'Current Setup left a duplicate legacy application.' }
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    $running = Start-TestApplication
    # /f ignores command-line properties, including this fixture's custom root.
    # Reinstall maintenance accepts the root and exercises the same native repair.
    Invoke-Installer $msiexec "/i `"$candidateMsi`" REINSTALL=ALL REINSTALLMODE=amus /quiet /norestart /l*v `"$testRoot\migrated-msi-repair.log`""
    Assert-Stopped $running
    Assert-WerPolicy
    Assert-OneRemovableAppEntry
    Assert-DataPreserved
    Invoke-Installer $msiexec "/x $candidateProductCode /quiet /norestart /l*v `"$testRoot\migrated-msi-uninstall.log`""
    if (Test-Path -LiteralPath $app) { throw 'The migrated MSI could not remove the application.' }
    Assert-WerPolicyRemoved
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
        CetCompatibilityMode = [bool]$CompatibilityMode
        UnifiedSetupSha256 = (Get-FileHash -LiteralPath $setup).Hash
        InvalidCompatibilitySelectionRejectedBeforeInstallation = $true
        OppositeEqualVersionSetupAndMsiRejected = $true
        RepairRetainedModeWithoutCommandLineOverride = $true
        CompatibilityChoicePreservedByModernUpdater = $true
        Version = $expectedVersion
        PreviousPublicVersion = $PreviousVersion
        LatestPublicUpgradePassed = $true
        FrozenLegacyUpdaterVersion = $LegacyUpdaterVersion
        LegacyHiddenMsiSetupVersion = $LegacySetupVersion
        LegacyHiddenMsiSetupSha256 = (Get-FileHash -LiteralPath $previousSetup).Hash
        OneRemovableMsiEntry = $true
        RetainedSetupBundleEntries = 0
        DirectMsiUpgradePassed = $true
        LegacyMsiUpgradeRejectedBeforePreparation = $true
        LegacySetupMigrationPassed = $true
        RunningAppRepairAndRemovalPassed = $true
        UserDataPreserved = $true
        DisposableDpapiSecretAndConfigPreserved = $true
        LegacyCopiedUpdaterPreservedActivation = $true
        CompatibilityRepairPreservedRegisteredLocation = $true
        ModernCopiedUpdaterRelocationAcknowledged = $true
        CanonicalFutureUpgradeRetainedLegacyReceipt = $true
        DelayedOwnedDisabledStartupTaskMigrated = $true
        NativeTransactionalRollbackRestoredPreviousInstallation = $true
        OwnedDisabledStartupTaskMigrated = $true
        SkippedVersionLegacyUpdaterPreservedActivation = $true
        FutureFixtureVersion = $future.Version
        FutureFixtureMsiSha256 = (Get-FileHash -LiteralPath $future.Msi).Hash
        LaterFutureFixtureVersion = $later.Version
        LaterFutureFixtureMsiSha256 = (Get-FileHash -LiteralPath $later.Msi).Hash
        InstalledWerPolicyVerified = $true
        ActualWerDumpVerified = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'installer-ownership-result.json') -Encoding utf8
    Write-Output 'Installer smoke passed: one removable app entry, fresh install, running-app repair/removal, direct MSI upgrade, blocked legacy MSI migration, native Setup migration and preserved user data.'
} catch {
    # Report source location without dumping command arguments, bound parameters,
    # fixture configuration or account contents into workflow logs.
    Write-Host ("Installer smoke failure type: {0}" -f $_.Exception.GetType().FullName)
    if ($null -ne $_.InvocationInfo) {
        Write-Host ("Installer smoke failure location: {0}:{1}:{2}" -f
            $_.InvocationInfo.ScriptName, $_.InvocationInfo.ScriptLineNumber, $_.InvocationInfo.OffsetInLine)
    }
    if (-not [string]::IsNullOrWhiteSpace($_.ScriptStackTrace)) {
        Write-Host ("Installer smoke script stack:`n{0}" -f $_.ScriptStackTrace)
    }
    throw
} finally {
    try {
        $updatesEvidenceDirectory = Join-Path $env:RDRIVE_DATA_DIR 'updates'
        Save-IsolatedUpdaterEvidence $updatesEvidenceDirectory $testRoot $expectedVersion
        foreach ($offset in @(1, 2)) {
            $futureEvidenceVersion = '{0}.{1}.{2}' -f $targetVersion.Major, $targetVersion.Minor, ($targetVersion.Build + $offset)
            Save-IsolatedUpdaterEvidence $updatesEvidenceDirectory $testRoot $futureEvidenceVersion
        }
        Remove-StartupFixture
        if ((Get-GlobalWerPolicySnapshot) -cne $initialGlobalWerPolicy) {
            throw 'Installer lifecycle changed the pre-existing global Windows Error Reporting policy.'
        }
        foreach ($relative in @('logs\resodrive-ui.log', 'updates\installer-preparation.json')) {
            $diagnostic = Join-Path $env:RDRIVE_DATA_DIR $relative
            if (Test-Path -LiteralPath $diagnostic) {
                Copy-Item -LiteralPath $diagnostic -Destination (Join-Path $testRoot ('isolated-' + [IO.Path]::GetFileName($diagnostic) + '.log'))
            }
        }
    } finally { $env:RDRIVE_DATA_DIR = $oldDataRoot }
}

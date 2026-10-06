param(
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [bool] $BuildMsi = $true,

    [switch] $CompatibilityMode,

    # Called by build.ps1 after the standard payload's tests pass.
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'SourceIdentity.ps1')
$sourceFingerprint = Get-ResoDriveSourceFingerprint $projectRoot
. (Join-Path $projectRoot 'installer\ComponentIdentity.ps1')
. (Join-Path $projectRoot 'installer\Read-CetCompatibility.ps1')
$variantSuffix = if ($CompatibilityMode) { '-compatibility' } else { '' }
$cetCompat = (-not $CompatibilityMode).ToString().ToLowerInvariant()
$compatibilityProperty = $CompatibilityMode.IsPresent.ToString().ToLowerInvariant()
$installationMode = if ($CompatibilityMode) { 'cet-disabled' } else { 'standard' }
$artifactRoot = Join-Path $projectRoot "artifacts\$Runtime$variantSuffix"
$stageRoot = Join-Path $artifactRoot '.stage'
$appOutput = Join-Path $stageRoot 'app'
$installerOutput = Join-Path $stageRoot 'installer'
# WiX incremental tracking does not include version/define-constant changes.
# Keep each package's build state in the fresh stage, separate from NuGet assets.
$installerIntermediate = (Join-Path $stageRoot 'installer-obj') + '/'
$nativeOutput = Join-Path $stageRoot 'native'
$managedBuildRoot = Join-Path $stageRoot 'managed'
$symbolsOutput = Join-Path $artifactRoot 'symbols'
$buildProperties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
$versionNode = $buildProperties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
$releaseVersion = if ($null -eq $versionNode) { '' } else { $versionNode.InnerText.Trim() }
$productDisplayName = $buildProperties.SelectSingleNode('/Project/PropertyGroup/ProductDisplayName').InnerText.Trim()
$productPublisher = $buildProperties.SelectSingleNode('/Project/PropertyGroup/ProductPublisher').InnerText.Trim()
$productDescription = $buildProperties.SelectSingleNode('/Project/PropertyGroup/ProductDescription').InnerText.Trim()
$executableBaseName = $buildProperties.SelectSingleNode('/Project/PropertyGroup/ExecutableBaseName').InnerText.Trim()
$dotNetDesktopRuntimeMinimumVersion = $buildProperties.SelectSingleNode('/Project/PropertyGroup/DotNetDesktopRuntimeMinimumVersion').InnerText.Trim()
$msbuildProductDescription = $productDescription.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C')
$packageOutput = Join-Path $artifactRoot $executableBaseName
if ([string]::IsNullOrWhiteSpace($releaseVersion)) {
    throw 'VersionPrefix is missing from Directory.Build.props.'
}
if ([string]::IsNullOrWhiteSpace($productDisplayName) -or
    [string]::IsNullOrWhiteSpace($productPublisher) -or
    [string]::IsNullOrWhiteSpace($productDescription)) {
    throw 'Product metadata is incomplete in Directory.Build.props.'
}
if ($BuildMsi -and [string]::IsNullOrWhiteSpace($dotNetDesktopRuntimeMinimumVersion)) {
    throw '.NET Desktop Runtime minimum version is missing from Directory.Build.props.'
}
if ($executableBaseName -notmatch '^[a-z0-9-]+$') {
    throw "ExecutableBaseName '$executableBaseName' must contain only lowercase letters, digits, or hyphens."
}
if ($releaseVersion -notmatch '^(\d{1,3})\.(\d{1,3})\.(\d{1,5})$' -or
    [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
    throw "VersionPrefix '$releaseVersion' is not compatible with Windows Installer. Use major.minor.build (255.255.65535 maximum)."
}
$archiveOutput = Join-Path $artifactRoot "$executableBaseName-$Runtime-$releaseVersion$variantSuffix.zip"
$archiveChecksumOutput = "$archiveOutput.sha256"
$msiOutput = Join-Path $artifactRoot "$executableBaseName-$Runtime-$releaseVersion$variantSuffix.msi"
$msiChecksumOutput = "$msiOutput.sha256"
$msiUpgradeCode = '8D0BD004-119E-4589-B816-7D5A27D94561'
$resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
$resolvedArtifactParent = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if (-not $resolvedArtifacts.StartsWith($resolvedArtifactParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an artifact path outside $resolvedArtifactParent."
}
if (Test-Path -LiteralPath $resolvedArtifacts) {
    # Keep the runtime directory itself so a File Explorer window or terminal whose
    # current location is this folder does not prevent otherwise safe cleanup.
    Get-ChildItem -LiteralPath $resolvedArtifacts -Force |
        Remove-Item -Recurse -Force
}

dotnet restore (Join-Path $projectRoot 'resodrive.slnx') --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
dotnet restore (Join-Path $projectRoot 'installer\ResoDrive.Installer.wixproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Installer restore failed with exit code $LASTEXITCODE." }
if (-not $SkipTests) {
    dotnet test (Join-Path $projectRoot 'resodrive.slnx') --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}
dotnet restore (Join-Path $projectRoot 'src\ResoDrive.App\ResoDrive.App.csproj') `
    --artifacts-path $managedBuildRoot --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Isolated application restore failed with exit code $LASTEXITCODE." }
dotnet publish (Join-Path $projectRoot 'src\ResoDrive.App\ResoDrive.App.csproj') `
    --configuration $Configuration --runtime $Runtime --self-contained false --no-restore `
    --artifacts-path $managedBuildRoot `
    "-p:CETCompat=$cetCompat" "-p:ResoDriveCompatibilityMode=$compatibilityProperty" `
    --output $appOutput
if ($LASTEXITCODE -ne 0) { throw "App publish failed with exit code $LASTEXITCODE." }
$actualCetCompat = Get-PeCetCompatibility (Join-Path $appOutput "$executableBaseName.exe")
if ($actualCetCompat -ne (-not $CompatibilityMode)) {
    throw "Published application CET marking does not match the requested $installationMode package."
}
$managedConfiguration = $Configuration.ToLowerInvariant()
foreach ($managedAssembly in @("ResoDrive.App\${managedConfiguration}_$Runtime\resodrive.dll", "ResoDrive.Windows\$managedConfiguration\ResoDrive.Windows.dll")) {
    if ((Get-ManagedCetCompatibility (Join-Path $managedBuildRoot "bin\$managedAssembly")) -ne $CompatibilityMode.IsPresent) {
        throw 'The compiled application/updater compatibility metadata does not match this package.'
    }
}

& (Join-Path $projectRoot 'native\ResoDrive.CrashMonitor\build.ps1') `
    -OutputDirectory $nativeOutput -Configuration $Configuration
if (-not (Test-Path -LiteralPath (Join-Path $nativeOutput 'resodrive-launcher.exe') -PathType Leaf)) {
    throw 'The native crash monitor build did not produce resodrive-launcher.exe.'
}

New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
Get-ChildItem -LiteralPath $appOutput -File |
    Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'packages.lock.json' } |
    Copy-Item -Destination $packageOutput -Force
Copy-Item -LiteralPath (Join-Path $nativeOutput 'resodrive-launcher.exe') -Destination $packageOutput -Force
New-Item -ItemType Directory -Path $symbolsOutput -Force | Out-Null
foreach ($symbolSource in @($appOutput, $nativeOutput)) {
    Get-ChildItem -LiteralPath $symbolSource -File -Filter '*.pdb' |
        Copy-Item -Destination $symbolsOutput -Force
}
foreach ($requiredSymbol in @('resodrive.pdb', 'ResoDrive.Core.pdb', 'ResoDrive.Windows.pdb', 'ResoDrive.Host.pdb', 'resodrive-launcher.pdb')) {
    if (-not (Test-Path -LiteralPath (Join-Path $symbolsOutput $requiredSymbol) -PathType Leaf)) {
        throw "The build did not retain matching diagnostic symbols: $requiredSymbol."
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'profiles.sample.json') -Destination $packageOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $packageOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $packageOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packageOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\CRASH-DIAGNOSTICS.md') -Destination $packageOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\INSTALL-DIRECTORY-MIGRATION.md') -Destination $packageOutput -Force

Get-ChildItem -LiteralPath $packageOutput -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -LiteralPath (Join-Path $packageOutput 'SHA256SUMS.txt') -Encoding ascii

if ($BuildMsi) {
    dotnet build (Join-Path $projectRoot 'installer\ResoDrive.Installer.wixproj') `
        --configuration $Configuration `
        --no-restore `
        --output $installerOutput `
        "-p:IntermediateOutputPath=$installerIntermediate" `
        -p:PackageSource=$packageOutput `
        -p:ResoDriveVersion=$releaseVersion `
        "-p:ResoDriveComponentSeed=$(Get-ResoDriveComponentSeed $releaseVersion)" `
        -p:ResoDriveRuntime=$Runtime `
        -p:ResoDriveUpgradeCode=$msiUpgradeCode `
        "-p:ResoDriveInstallationMode=$installationMode" `
        "-p:ResoDriveProductName=$productDisplayName" `
        "-p:ResoDrivePublisher=$productPublisher" `
        "-p:ResoDriveDescription=$msbuildProductDescription" `
        "-p:ResoDriveExecutableBaseName=$executableBaseName" `
        "-p:ResoDriveExecutableName=$executableBaseName.exe"
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE." }

    $builtMsi = Join-Path $installerOutput "$executableBaseName-$Runtime-$releaseVersion.msi"
    if (-not (Test-Path -LiteralPath $builtMsi -PathType Leaf)) {
        throw "The MSI build did not produce '$builtMsi'."
    }
    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = $windowsInstaller.GetType().InvokeMember(
        'OpenDatabase',
        'InvokeMethod',
        $null,
        $windowsInstaller,
        @([string] $builtMsi, [int] 0))
    $view = $database.GetType().InvokeMember(
        'OpenView',
        'InvokeMethod',
        $null,
        $database,
        @('SELECT `FileName` FROM `File`'))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $packagedFiles = @()
    while ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) {
        $msiFileName = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
        $packagedFiles += ($msiFileName -split '\|')[-1]
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    $registryView = $database.GetType().InvokeMember(
        'OpenView', 'InvokeMethod', $null, $database,
        @('SELECT `Name`, `Value`, `Root`, `Key` FROM `Registry` WHERE `Component_` = ''ResoDriveLocalDumps'''))
    $registryView.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $registryView, $null) | Out-Null
    $dumpValues = @{}
    while ($record = $registryView.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $registryView, $null)) {
        $name = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
        $value = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 2)
        $root = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 3)
        $key = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 4)
        $dumpValues[$name] = @{ Value = $value; Root = $root; Key = $key }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
    }
    $registryView.GetType().InvokeMember('Close', 'InvokeMethod', $null, $registryView, $null) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($registryView) | Out-Null
    # Check the compiled MSI, including formatted policy strings and the
    # executable used by advertised shortcuts, rather than only its XML source.
    $compiledChecks = @(
        @{ Query = 'SELECT `DefaultDir` FROM `Directory` WHERE `Directory` = ''INSTALLFOLDER'''; Expected = 'ResoDrive'; LongName = $true },
        @{ Query = 'SELECT `Source` FROM `CustomAction` WHERE `Action` = ''LaunchResoDrive'''; Expected = 'ResoDriveLauncherFile' },
        @{ Query = 'SELECT `Component_` FROM `Shortcut` WHERE `Shortcut` = ''ResoDriveStartMenuShortcut'''; Expected = 'ResoDriveLauncher' },
        @{ Query = 'SELECT `Type` FROM `CompLocator` WHERE `Signature_` = ''PreviousInstallFolder'''; Expected = '1' },
        @{ Query = 'SELECT `Target` FROM `CustomAction` WHERE `Action` = ''RestoreInstalledFolder'''; Expected = '[RDRIVE_INSTALLED_ROOT]' },
        @{ Query = 'SELECT `Value` FROM `Registry` WHERE `Name` = ''CompatibilityMode'' AND `Component_` = ''ResoDriveCompatibilityMode'''; Expected = $installationMode }
    )
    foreach ($check in $compiledChecks) {
        $checkView = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($check.Query))
        try {
            $checkView.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $checkView, $null) | Out-Null
            $record = $checkView.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $checkView, $null)
            if ($null -eq $record) { throw "MSI validation found a missing installation contract: $($check.Query)" }
            try {
                $actual = [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
                if ($check.ContainsKey('LongName')) { $actual = ($actual -split '\|')[-1] }
                if ($actual -cne $check.Expected) { throw "MSI validation found an incorrect installation contract: $($check.Query)" }
            } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null }
        } finally {
            $checkView.GetType().InvokeMember('Close', 'InvokeMethod', $null, $checkView, $null) | Out-Null
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($checkView) | Out-Null
        }
    }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller) | Out-Null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    $embeddedHelper = Join-Path $stageRoot 'embedded-preparation-helper.exe'
    Export-MsiPreparationHelper -MsiPath $builtMsi -OutputPath $embeddedHelper
    if ((Get-FileHash -LiteralPath $embeddedHelper -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $packageOutput "$executableBaseName.exe") -Algorithm SHA256).Hash -or
        (Get-PeCetCompatibility $embeddedHelper) -ne $actualCetCompat) {
        throw 'The compiled MSI preparation helper does not match the verified application variant.'
    }
    foreach ($requiredFile in @("$executableBaseName.exe", 'resodrive-launcher.exe', 'profiles.sample.json')) {
        if ($requiredFile -notin $packagedFiles) {
            throw "MSI validation could not find '$requiredFile' in the File table."
        }
    }
    if ('rclone.exe' -in $packagedFiles) {
        throw "MSI validation found rclone.exe, which must remain an app-managed per-user component."
    }
    if (@($packagedFiles | Where-Object { $_ -like '*.pdb' }).Count -ne 0) {
        throw 'MSI validation found diagnostic symbols in the public package.'
    }
    $expectedDumpValues = @{
        DumpFolder = '#%[\%]LOCALAPPDATA[\%]\rdrive-diagnostics\dumps'
        DumpCount = '#3'
        DumpType = '#2'
    }
    foreach ($name in $expectedDumpValues.Keys) {
        if (-not $dumpValues.ContainsKey($name) -or
            $dumpValues[$name].Value -cne $expectedDumpValues[$name] -or
            $dumpValues[$name].Root -ne '2' -or
            $dumpValues[$name].Key -cne 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\resodrive.exe') {
            throw "MSI validation found incorrect Windows Error Reporting authoring: $name."
        }
    }

    Copy-Item -LiteralPath $builtMsi -Destination $msiOutput -Force
    $msiHash = (Get-FileHash -LiteralPath $msiOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    "$msiHash  $(Split-Path -Leaf $msiOutput)" |
        Set-Content -LiteralPath $msiChecksumOutput -Encoding ascii


}

Remove-Item -LiteralPath $stageRoot -Recurse -Force
Compress-Archive -Path (Join-Path $packageOutput '*') -DestinationPath $archiveOutput -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archiveOutput -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $(Split-Path -Leaf $archiveOutput)" |
    Set-Content -LiteralPath $archiveChecksumOutput -Encoding ascii

# Symbols are workflow artifacts, never public release assets. Record the exact
# shipped file hashes so a same-version rebuild cannot be mistaken for a match.
$sourceCommit = git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not record the build source commit.' }
$sourceChanges = @(git -C $projectRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Could not record the source working-tree state.' }
if ((Get-ResoDriveSourceFingerprint $projectRoot) -cne $sourceFingerprint) {
    throw 'Source files changed while building this payload. Run the build again after finishing source edits.'
}
$sdkVersion = dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Could not record the .NET SDK version.' }
$manifestFiles = @(Get-ChildItem -LiteralPath $packageOutput -File) +
    @(Get-ChildItem -LiteralPath $symbolsOutput -File) +
    @(Get-ChildItem -LiteralPath $artifactRoot -File | Where-Object { $_.Extension -in '.zip', '.msi', '.exe' })
$manifest = [ordered]@{
    schemaVersion = 1
    product = $productDisplayName
    version = $releaseVersion
    runtime = $Runtime
    configuration = $Configuration
    compatibilityMode = $CompatibilityMode.IsPresent
    cetCompat = $actualCetCompat
    sourceCommit = "$sourceCommit".Trim()
    sourceFingerprint = $sourceFingerprint
    sourceModified = $sourceChanges.Count -ne 0
    dotNetSdk = "$sdkVersion".Trim()
    nativeCompilerVersion = (Get-Item -LiteralPath (Get-Command cl.exe).Source).VersionInfo.FileVersion
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    workflowRunId = $env:GITHUB_RUN_ID
    files = @($manifestFiles | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($artifactRoot, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $symbolsOutput 'build-manifest.json') -Encoding utf8
Write-Host "Published $productDisplayName to $packageOutput"
Write-Host "Release archive: $archiveOutput"
Write-Host "Matching diagnostic symbols: $symbolsOutput"
if ($BuildMsi) {
    Write-Host "Windows installer: $msiOutput"
}

[CmdletBinding()]
param(
    [ValidateSet('win-x64')][string] $Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'SourceIdentity.ps1')
$sourceFingerprint = Get-ResoDriveSourceFingerprint $projectRoot
$properties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
function Get-BuildProperty([string]$Name) {
    $node = $properties.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Required Setup build property is missing: $Name."
    }
    return $node.InnerText.Trim()
}
$version = Get-BuildProperty 'VersionPrefix'
$executable = Get-BuildProperty 'ExecutableBaseName'
$runtimeUrl = Get-BuildProperty 'DotNetDesktopRuntimeUrl'
$runtimeSha512 = Get-BuildProperty 'DotNetDesktopRuntimeSha512'
$runtimeSizeText = Get-BuildProperty 'DotNetDesktopRuntimeSize'
[Uri] $runtimeUri = $null
[long] $runtimeSize = 0
if (-not [Uri]::TryCreate($runtimeUrl, [UriKind]::Absolute, [ref] $runtimeUri) -or
    $runtimeUri.Scheme -ne [Uri]::UriSchemeHttps -or $runtimeSha512 -notmatch '^[0-9a-fA-F]{128}$' -or
    -not [long]::TryParse($runtimeSizeText, [ref] $runtimeSize) -or $runtimeSize -le 0) {
    throw '.NET Desktop Runtime metadata is invalid.'
}
$artifactRoot = Join-Path $projectRoot "artifacts\$Runtime"
$compatibilityRoot = Join-Path $projectRoot "artifacts\$Runtime-compatibility"
$standardMsi = Join-Path $artifactRoot "$executable-$Runtime-$version.msi"
$compatibilityMsi = Join-Path $compatibilityRoot "$executable-$Runtime-$version-compatibility.msi"
$sourceCommit = (git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the Setup source commit.' }

function Read-VerifiedVariant([string]$Root, [string]$Msi, [bool]$Compatibility) {
    $manifestPath = Join-Path $Root 'symbols\build-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $version -or $manifest.runtime -cne $Runtime -or
        $manifest.configuration -cne $Configuration -or $manifest.sourceCommit -cne $sourceCommit -or
        $manifest.sourceFingerprint -cne $sourceFingerprint -or
        $manifest.compatibilityMode -ne $Compatibility -or $manifest.cetCompat -eq $Compatibility) {
        throw "Setup refuses an inconsistent payload manifest: $manifestPath."
    }
    foreach ($requiredPath in @($Msi, (Join-Path $Root "$executable\$executable.exe"))) {
        $relative = [IO.Path]::GetRelativePath($Root, $requiredPath).Replace('\', '/')
        $entry = @($manifest.files | Where-Object path -CEQ $relative)
        if ($entry.Count -ne 1 -or -not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $requiredPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry[0].sha256) {
            throw "Setup refuses a missing or changed verified payload: $requiredPath."
        }
    }
    return $manifest
}
$standardManifest = Read-VerifiedVariant $artifactRoot $standardMsi $false
$compatibilityManifest = Read-VerifiedVariant $compatibilityRoot $compatibilityMsi $true
if ($standardManifest.dotNetSdk -cne $compatibilityManifest.dotNetSdk -or
    $standardManifest.sourceModified -ne $compatibilityManifest.sourceModified) {
    throw 'Setup payloads must come from the same source state and .NET SDK.'
}

$stageRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot '.setup-stage'))
$resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot)
if (-not $stageRoot.StartsWith($resolvedArtifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a Setup stage outside $resolvedArtifactRoot."
}
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
$nativeOutput = Join-Path $stageRoot 'native'
$bundleOutput = Join-Path $stageRoot 'bootstrapper'
$bundleIntermediate = (Join-Path $stageRoot 'bootstrapper-obj') + '/'
& (Join-Path $projectRoot 'native\ResoDrive.SetupFunctions\build.ps1') -OutputDirectory $nativeOutput -Configuration $Configuration
$functions = Join-Path $nativeOutput 'resodrive-setup-functions.dll'
if (-not (Test-Path -LiteralPath $functions -PathType Leaf)) {
    throw 'The native Setup functions build did not produce resodrive-setup-functions.dll.'
}
dotnet restore (Join-Path $projectRoot 'installer\ResoDrive.Bootstrapper.wixproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Setup bundle restore failed with exit code $LASTEXITCODE." }
dotnet build (Join-Path $projectRoot 'installer\ResoDrive.Bootstrapper.wixproj') `
    --configuration $Configuration --no-restore --output $bundleOutput `
    "-p:IntermediateOutputPath=$bundleIntermediate" `
    "-p:StandardMsiSource=$standardMsi" "-p:CompatibilityMsiSource=$compatibilityMsi" `
    "-p:SetupFunctionsSource=$functions" `
    "-p:ResoDriveVersion=$version" "-p:ResoDriveRuntime=$Runtime" `
    '-p:ResoDriveBundleUpgradeCode=5B94F457-820F-4B41-B609-071179764B08' `
    '-p:ResoDriveUpgradeCode=8D0BD004-119E-4589-B816-7D5A27D94561' `
    "-p:ResoDriveProductName=$(Get-BuildProperty 'ProductDisplayName')" `
    "-p:ResoDrivePublisher=$(Get-BuildProperty 'ProductPublisher')" `
    "-p:ResoDriveExecutableBaseName=$executable" `
    "-p:DotNetDesktopRuntimeVersion=$(Get-BuildProperty 'DotNetDesktopRuntimeVersion')" `
    "-p:DotNetDesktopRuntimeMinimumVersion=$(Get-BuildProperty 'DotNetDesktopRuntimeMinimumVersion')" `
    "-p:DotNetDesktopRuntimeUrl=$(Get-BuildProperty 'DotNetDesktopRuntimeUrl')" `
    "-p:DotNetDesktopRuntimeSha512=$(Get-BuildProperty 'DotNetDesktopRuntimeSha512')" `
    "-p:DotNetDesktopRuntimeSize=$(Get-BuildProperty 'DotNetDesktopRuntimeSize')"
if ($LASTEXITCODE -ne 0) { throw "Setup bundle build failed with exit code $LASTEXITCODE." }
if ((Get-ResoDriveSourceFingerprint $projectRoot) -cne $sourceFingerprint) {
    throw 'Source files changed while building Setup. Run the build again after finishing source edits.'
}

$setupName = "$executable-$Runtime-$version-setup.exe"
$builtSetup = Join-Path $bundleOutput $setupName
if (-not (Test-Path -LiteralPath $builtSetup -PathType Leaf)) { throw "The Setup build did not produce $setupName." }
$setupOutput = Join-Path $artifactRoot $setupName
Copy-Item -LiteralPath $builtSetup -Destination $setupOutput -Force
$setupHash = (Get-FileHash -LiteralPath $setupOutput -Algorithm SHA256).Hash.ToLowerInvariant()
"$setupHash  $setupName" | Set-Content -LiteralPath "$setupOutput.sha256" -Encoding ascii
$symbols = Join-Path $artifactRoot 'symbols'
Copy-Item -LiteralPath $functions -Destination $symbols -Force
$functionsPdb = Join-Path $nativeOutput 'resodrive-setup-functions.pdb'
if (-not (Test-Path -LiteralPath $functionsPdb -PathType Leaf)) { throw 'The Setup functions build did not retain matching symbols.' }
Copy-Item -LiteralPath $functionsPdb -Destination $symbols -Force
$functions = Join-Path $symbols 'resodrive-setup-functions.dll'
$functionsPdb = Join-Path $symbols 'resodrive-setup-functions.pdb'
# The common bundle has its own receipt binding both already-verified payloads.
# Keep each payload's original manifest intact for application diagnostics.
$bundleManifest = [ordered]@{
    schemaVersion = 1
    version = $version
    runtime = $Runtime
    configuration = $Configuration
    sourceCommit = $sourceCommit
    sourceFingerprint = $sourceFingerprint
    sourceModified = $standardManifest.sourceModified
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    workflowRunId = $env:GITHUB_RUN_ID
    files = @(@($setupOutput, $standardMsi, $compatibilityMsi, $functions, $functionsPdb) | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($artifactRoot, $_).Replace('\', '/')
            bytes = (Get-Item -LiteralPath $_).Length
            sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}
$bundleManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $symbols 'setup-build-manifest.json') -Encoding utf8
Remove-Item -LiteralPath $stageRoot -Recurse -Force
Write-Host "Windows setup (standard and compatibility choices): $setupOutput"

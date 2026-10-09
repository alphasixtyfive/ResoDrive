param(
    [Parameter(Mandatory)][string] $SetupPath,
    [Parameter(Mandatory)][string] $AppPath
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'CET upgrade acceptance requires a disposable GitHub-hosted Windows runner.'
}
$root = Split-Path $PSScriptRoot -Parent
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$app = (Resolve-Path -LiteralPath $AppPath).Path
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-cet-update-smoke'
$null = New-Item -ItemType Directory -Path $evidence -Force
# Build the next version from the exact tested source, in its own directory.
# This exercises a real version change without a second compatibility variant
# or overwriting the current artifacts and managed build outputs.
$fixture = Join-Path $env:RUNNER_TEMP ('resodrive-cet-next-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$archive = Join-Path $fixture 'source.zip'
git -C $root archive --format=zip "--output=$archive" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not stage the exact source for the next-version fixture.' }
$source = Join-Path $fixture 'source'
Expand-Archive -LiteralPath $archive -DestinationPath $source
$propertiesPath = Join-Path $source 'Directory.Build.props'
$properties = [xml](Get-Content -LiteralPath $propertiesPath -Raw)
$versionNode = $properties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
$version = [version] $versionNode.InnerText
if ($version.Build -ge 65535) { throw 'Cannot increment the MSI fixture version.' }
$versionNode.InnerText = '{0}.{1}.{2}' -f $version.Major, $version.Minor, ($version.Build + 1)
$properties.Save($propertiesPath)
& (Join-Path $source 'build.ps1') -Configuration Release -BuildMsi $true *> (Join-Path $evidence 'next-version-build.log')
if (-not $?) { throw 'Next-version fixture build failed; see next-version-build.log.' }
$futureArtifacts = Join-Path $source 'artifacts/win-x64'
& (Join-Path $PSScriptRoot 'cet-update-smoke.ps1') -CurrentSetupPath $setup -CurrentAppPath $app `
    -FutureSetupPath (Get-ChildItem -LiteralPath $futureArtifacts -Filter '*-setup.exe').FullName `
    -FutureAppPath (Join-Path $futureArtifacts 'resodrive/resodrive.exe') `
    -ProductionWindowsAssemblyPath (Join-Path $root 'src/ResoDrive.Windows/bin/Release/net10.0-windows10.0.17763.0/ResoDrive.Windows.dll')

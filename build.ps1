[CmdletBinding()]
param(
    [ValidateSet('win-x64')][string] $Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [bool] $BuildMsi = $true,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer\SourceIdentity.ps1')
$sourceFingerprint = Get-ResoDriveSourceFingerprint $PSScriptRoot
$variantBuilder = Join-Path $PSScriptRoot 'installer\Build-Variant.ps1'
# Both builds start from freshly cleaned, isolated stages. The shared Setup is
# created only after each application and embedded MSI helper passed verification.
& $variantBuilder -Runtime $Runtime -Configuration $Configuration -BuildMsi $BuildMsi -SkipTests:$SkipTests
& $variantBuilder -Runtime $Runtime -Configuration $Configuration -BuildMsi $BuildMsi -CompatibilityMode -SkipTests
if ((Get-ResoDriveSourceFingerprint $PSScriptRoot) -cne $sourceFingerprint) {
    throw 'Source files changed while building the two payloads. Run the build again after finishing source edits.'
}
if ($BuildMsi) {
    & (Join-Path $PSScriptRoot 'installer\Build-Setup.ps1') -Runtime $Runtime -Configuration $Configuration
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$dependencies = Join-Path $PSScriptRoot 'ApiDependencies.csproj'
& $DotNetPath restore $dependencies --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned WiX native API headers failed.' }
$properties = (& $DotNetPath msbuild $dependencies -getProperty:PkgWixToolset_BootstrapperApplicationApi,PkgWixToolset_WixStandardBootstrapperApplicationFunctionApi | Out-String | ConvertFrom-Json).Properties
if ($LASTEXITCODE -ne 0) { throw 'Resolving the WiX native API include directories failed.' }
$apiInclude = Join-Path $properties.PkgWixToolset_BootstrapperApplicationApi 'build\native\include'
$functionsInclude = Join-Path $properties.PkgWixToolset_WixStandardBootstrapperApplicationFunctionApi 'lib\native\include'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Building Setup functions requires Visual Studio x64 C++ Build Tools and the Windows SDK.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio x64 C++ Build Tools were not found.' }
& (Join-Path $installation 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
$optimization = if ($Configuration -eq 'Release') { '/O2' } else { '/Od' }
$dll = Join-Path $destination 'resodrive-setup-functions.dll'
$pdb = Join-Path $destination 'resodrive-setup-functions.pdb'
& cl.exe /nologo /std:c++17 /W4 /WX /MT /Zi /guard:cf /DUNICODE /D_UNICODE $optimization /LD "/I$apiInclude" "/I$functionsInclude" "/Fo$(Join-Path $destination 'setup-functions.obj')" "/Fd$(Join-Path $destination 'setup-functions-compiler.pdb')" "/Fe$dll" (Join-Path $PSScriptRoot 'setup-functions.cpp') /link /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /GUARD:CF /DEBUG:FULL /INCREMENTAL:NO /Brepro "/PDB:$pdb" msi.lib advapi32.lib shell32.lib
if ($LASTEXITCODE -ne 0) { throw "Native Setup functions compilation failed ($LASTEXITCODE)." }
$testExe = Join-Path $destination 'setup-selection-tests.exe'
& cl.exe /nologo /std:c++17 /W4 /WX /MT "/Fo$(Join-Path $destination 'test-selection.obj')" "/Fe$testExe" (Join-Path $PSScriptRoot 'test-selection.cpp') /link /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO
if ($LASTEXITCODE -ne 0) { throw "Native Setup selection tests failed to compile ($LASTEXITCODE)." }
& $testExe
if ($LASTEXITCODE -ne 0) { throw "Native Setup selection tests failed ($LASTEXITCODE)." }
Write-Output $dll

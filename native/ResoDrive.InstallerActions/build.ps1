param(
    [Parameter(Mandatory)][string] $ApplicationPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $PolicyScriptPath = (Join-Path $PSScriptRoot '../../installer/CetPolicy.ps1')
)

$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$policyScript = (Resolve-Path -LiteralPath $PolicyScriptPath).Path
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ build tools are required.' }
Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

# Embed the existing published image unchanged: no second application build or
# binary patch. Keep generated files outside the source tree.
$resource = Join-Path $destination 'Application.rc'
$resourceObject = Join-Path $destination 'Application.res'
$resourcePath = $application.Replace('\', '/')
$policyPath = $policyScript.Replace('\', '/')
[IO.File]::WriteAllText($resource, "101 RCDATA `"$resourcePath`"`r`n102 RCDATA `"$policyPath`"`r`n", [Text.UTF8Encoding]::new($false))
& rc.exe /nologo /c65001 "/fo$resourceObject" $resource
if ($LASTEXITCODE -ne 0) { throw "Installer resource compilation failed ($LASTEXITCODE)." }

$library = Join-Path $destination 'ResoDrive.InstallerActions.dll'
$object = Join-Path $destination 'InstallerActions.obj'
$importLibrary = Join-Path $destination 'InstallerActions.lib'
$definition = Join-Path $PSScriptRoot 'InstallerActions.def'
$source = Join-Path $PSScriptRoot 'InstallerActions.cpp'
& cl.exe /nologo /LD /MT /O2 /W4 /WX /EHsc /std:c++20 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /guard:cf "/Fo$object" $source $resourceObject /link /WX "/OUT:$library" "/DEF:$definition" "/IMPLIB:$importLibrary" /DYNAMICBASE /NXCOMPAT /guard:cf advapi32.lib msi.lib ole32.lib userenv.lib
if ($LASTEXITCODE -ne 0) { throw "Installer native compilation failed ($LASTEXITCODE)." }

& (Join-Path $PSScriptRoot 'verify.ps1') -LibraryPath $library -ApplicationPath $application -PolicyScriptPath $policyScript

Write-Host "Native installer actions: $library"

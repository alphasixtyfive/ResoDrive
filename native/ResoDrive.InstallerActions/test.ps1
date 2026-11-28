param(
    [Parameter(Mandatory)][string] $ApplicationPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $PolicyScriptPath = (Join-Path $PSScriptRoot '../../installer/CetPolicy.ps1')
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -ApplicationPath $ApplicationPath -OutputDirectory $OutputDirectory -PolicyScriptPath $PolicyScriptPath
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$test = Join-Path $destination 'InstallerActionsTests.exe'
$source = Join-Path $PSScriptRoot 'InstallerActionsTests.cpp'
$object = Join-Path $destination 'InstallerActionsTests.obj'
$resource = Join-Path $destination 'Application.res'
& cl.exe /nologo /MT /O2 /W4 /WX /EHsc /std:c++20 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /guard:cf "/Fo$object" $source $resource /link /WX "/OUT:$test" /CETCOMPAT /DYNAMICBASE /NXCOMPAT /guard:cf advapi32.lib msi.lib ole32.lib userenv.lib
if ($LASTEXITCODE -ne 0) { throw "Installer native test compilation failed ($LASTEXITCODE)." }
& $test
if ($LASTEXITCODE -ne 0) { throw "Installer native checks failed ($LASTEXITCODE)." }

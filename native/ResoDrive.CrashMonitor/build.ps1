[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$IncludeTestFixture
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Building the native crash monitor requires Visual Studio C++ Build Tools (x64) and the Windows SDK.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio x64 C++ Build Tools were not found. Install the Desktop development with C++ workload.' }
$developerShell = Join-Path $installation 'Common7\Tools\Launch-VsDevShell.ps1'
& $developerShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'The MSVC compiler is unavailable after loading its developer environment.' }
$optimization = if ($Configuration -eq 'Release') { '/O2' } else { '/Od' }
$source = Join-Path $PSScriptRoot 'monitor.c'
$exe = Join-Path $destination 'resodrive-launcher.exe'
$pdb = Join-Path $destination 'resodrive-launcher.pdb'
$objDirectory = $destination + [IO.Path]::DirectorySeparatorChar
$compilerPdb = Join-Path $destination 'resodrive-launcher-compiler.pdb'
& cl.exe /nologo /std:c17 /W4 /WX /MT /Zi /guard:cf /DUNICODE /D_UNICODE $optimization "/Fo$objDirectory" "/Fd$compilerPdb" "/Fe$exe" $source (Join-Path $PSScriptRoot 'event-evidence.c') /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /GUARD:CF /DEBUG:FULL /INCREMENTAL:NO /Brepro "/PDB:$pdb" /MANIFEST:EMBED "/MANIFESTINPUT:$(Join-Path $PSScriptRoot 'launcher.manifest')" user32.lib shell32.lib comctl32.lib bcrypt.lib version.lib advapi32.lib ole32.lib wevtapi.lib
if ($LASTEXITCODE -ne 0) { throw "Native crash monitor compilation failed ($LASTEXITCODE)." }
if ($IncludeTestFixture) {
    $fixture = Join-Path $destination 'resodrive.exe'
    & cl.exe /nologo /std:c17 /W4 /WX /MT "/Fo$(Join-Path $destination 'fixture.obj')" "/Fe$fixture" (Join-Path $PSScriptRoot 'test-fixture.c') /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO user32.lib shell32.lib wer.lib
    if ($LASTEXITCODE -ne 0) { throw "Native crash fixture compilation failed ($LASTEXITCODE)." }
    & cl.exe /nologo /std:c17 /W4 /WX /MT "/Fo$objDirectory" "/Fe$(Join-Path $destination 'event-evidence-tests.exe')" (Join-Path $PSScriptRoot 'test-events.c') (Join-Path $PSScriptRoot 'event-evidence.c') /link /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO wevtapi.lib
    if ($LASTEXITCODE -ne 0) { throw "Native event correlation test compilation failed ($LASTEXITCODE)." }
    & cl.exe /nologo /std:c17 /W4 /WX /MT "/Fo$objDirectory" "/Fe$(Join-Path $destination 'path-evidence-tests.exe')" (Join-Path $PSScriptRoot 'test-paths.c') (Join-Path $PSScriptRoot 'event-evidence.c') /link /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT /INCREMENTAL:NO /MANIFEST:EMBED "/MANIFESTINPUT:$(Join-Path $PSScriptRoot 'launcher.manifest')" user32.lib shell32.lib comctl32.lib bcrypt.lib version.lib advapi32.lib ole32.lib wevtapi.lib
    if ($LASTEXITCODE -ne 0) { throw "Native path evidence test compilation failed ($LASTEXITCODE)." }
}
Write-Output $exe

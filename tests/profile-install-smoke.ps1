param([Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppPath).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-profile-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$data = Join-Path $fixture 'active'
$source = Join-Path $fixture 'source.json'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\profiles.sample.json') -Destination $source
function Invoke-Import([string[]]$Arguments, [int]$Expected) {
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['RDRIVE_DATA_DIR'] = $data
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(20000)) { throw "Profile import fixture did not finish. Evidence: $fixture" }
        if ($process.ExitCode -ne $Expected) { throw "Expected profile exit $Expected, got $($process.ExitCode). Evidence: $fixture" }
    } finally { $process.Dispose() }
}
Invoke-Import @('--install-profiles') 2
if (Test-Path -LiteralPath $data) { throw 'Malformed arguments created account data.' }
Invoke-Import @('--install-profiles', $source) 0
$catalog = Join-Path $data 'profiles.json'
$hash = (Get-FileHash -LiteralPath $catalog).Hash
Invoke-Import @('--install-profiles', $source) 10
if ((Get-FileHash -LiteralPath $catalog).Hash -ne $hash) { throw 'Valid existing catalog changed.' }
[IO.File]::WriteAllText($source, '{invalid')
Invoke-Import @('--install-profiles', $source) 1
if ((Get-FileHash -LiteralPath $catalog).Hash -ne $hash) { throw 'Invalid input changed the catalog.' }
'Profile import process smoke passed: isolated active root, argument rejection, installation, preservation and invalid-input rejection.'

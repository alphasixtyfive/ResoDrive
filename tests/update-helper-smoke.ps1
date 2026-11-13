param([Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$app = (Resolve-Path -LiteralPath $AppPath).Path
$version = ((Get-Item -LiteralPath $app).VersionInfo.ProductVersion -split '\+', 2)[0]
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = [IO.Path]::GetFullPath((Join-Path $temporaryBase ('resodrive-helper-smoke-' + [Guid]::NewGuid().ToString('N'))))
if (-not $fixture.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The helper fixture must stay inside the temporary directory.'
}
$updates = Join-Path $fixture 'updates'
$source = Join-Path $fixture 'resodrive.exe'
$helperPath = Join-Path $updates 'resodrive-update-helper.exe'
$installer = Join-Path $updates "resodrive-win-x64-$version-setup.exe"
$outcome = Join-Path $updates 'application-update-result.json'
$pipeName = 'ResoDrive.Test.Helper.' + [Guid]::NewGuid().ToString('N')
$pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
$process = $null
try {
    New-Item -ItemType Directory -Path $updates | Out-Null
    Copy-Item -LiteralPath $app -Destination $source
    Copy-Item -LiteralPath $app -Destination $helperPath
    [IO.File]::WriteAllText($installer, 'Disposable installer sentinel; must never execute.')
    [IO.File]::WriteAllText($outcome, 'Parent-owned failure receipt; must remain unchanged.')
    $sourceHash = (Get-FileHash -LiteralPath $source).Hash
    $installerHash = (Get-FileHash -LiteralPath $installer).Hash
    $outcomeHash = (Get-FileHash -LiteralPath $outcome).Hash
    $start = [Diagnostics.ProcessStartInfo]::new($helperPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('--complete-update', $version, $installer, $source, $source, $outcome, $installerHash, [string]$PID)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['RDRIVE_DATA_DIR'] = $fixture
    $start.Environment['RDRIVE_UPDATE_HANDOFF_DIR'] = $updates
    $start.Environment['RDRIVE_UPDATE_READY_PIPE'] = $pipeName
    $connection = $pipe.WaitForConnectionAsync()
    $process = [Diagnostics.Process]::Start($start)
    if (-not $connection.Wait(15000)) { throw 'The published helper did not connect within 15 seconds.' }
    $connection.GetAwaiter().GetResult()
    $buffer = [byte[]]::new(1)
    $read = $pipe.ReadAsync($buffer, 0, 1)
    if (-not $read.Wait(5000) -or $read.Result -ne 1 -or $buffer[0] -ne 1) {
        throw 'The published helper did not signal readiness.'
    }
    # Simulate the parent rejecting or cancelling the handoff. Never authorize installation.
    $pipe.Dispose()
    if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 1) {
        throw 'A rejected helper did not exit safely.'
    }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash -or
        (Get-FileHash -LiteralPath $installer).Hash -ne $installerHash -or
        (Get-FileHash -LiteralPath $outcome).Hash -ne $outcomeHash) {
        throw 'A rejected helper changed the source, installer or parent receipt.'
    }
    Write-Host 'Update helper smoke passed: published helper ready; rejected handoff exited without installation or data changes.'
}
finally {
    $pipe.Dispose()
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

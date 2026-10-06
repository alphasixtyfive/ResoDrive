# Structural parser fixtures contain no captured process memory and cause no crash.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Read-FullMemoryDumpEvidence.ps1')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-dump-format-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$valid = Join-Path $fixtureRoot 'valid-fixture.dmp'
$stream = [IO.File]::Create($valid)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $stream.SetLength(560)
    $writer.Write([uint32]0x504d444d); $writer.Write([uint32]0xa793)
    $writer.Write([uint32]4); $writer.Write([uint32]32)
    $writer.Write([uint32]0); $writer.Write([uint32]0); $writer.Write([uint64]2)
    foreach ($entry in @(@(3,52,80), @(4,112,132), @(6,168,244), @(9,32,412))) {
        foreach ($value in $entry) { $writer.Write([uint32]$value) }
    }
    $stream.Position = 80; $writer.Write([uint32]1); $writer.Write([uint32]123)
    $stream.Position = 132; $writer.Write([uint32]1)
    $stream.Position = 156; $writer.Write([uint32]492)
    $stream.Position = 244; $writer.Write([uint32]123)
    $stream.Position = 252; $writer.Write([Convert]::ToUInt32('C0000602', 16))
    $stream.Position = 404; $writer.Write([uint32]32); $writer.Write([uint32]444)
    $stream.Position = 412; $writer.Write([uint64]1); $writer.Write([uint64]476)
    $writer.Write([uint64]0x1000); $writer.Write([uint64]16)
    $stream.Position = 492
    $name = [Text.Encoding]::Unicode.GetBytes('coreclr.dll')
    $writer.Write([uint32]$name.Length); $writer.Write($name)
} finally { $writer.Dispose() }
$evidence = Read-FullMemoryDumpEvidence -Path $valid -RequiredModule coreclr.dll
if ($evidence.threadCount -ne 1 -or $evidence.moduleCount -ne 1 -or $evidence.memoryBytes -ne 16) { throw 'Valid dump fixture was parsed incorrectly.' }
$checks = [Collections.Generic.List[string]]::new()
$checks.Add('Valid full-memory fixture, exception context and required CLR module accepted.')
function Reject-ModifiedFixture([string]$name, [long]$offset, [byte[]]$replacement) {
    $path = Join-Path $fixtureRoot "$name.dmp"
    $bytes = [IO.File]::ReadAllBytes($valid)
    [Array]::Copy($replacement, 0, $bytes, $offset, $replacement.Length)
    [IO.File]::WriteAllBytes($path, $bytes)
    $rejected = $false
    try { $null = Read-FullMemoryDumpEvidence $path -RequiredModule coreclr.dll }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid dump fixture was accepted: $name" }
    $checks.Add("Rejected $name")
}
Reject-ModifiedFixture 'bad-signature' 0 ([BitConverter]::GetBytes([uint32]0))
Reject-ModifiedFixture 'no-full-memory-flag' 24 ([BitConverter]::GetBytes([uint64]0))
Reject-ModifiedFixture 'unbounded-stream-count' 8 ([BitConverter]::GetBytes([uint32]100000))
Reject-ModifiedFixture 'directory-outside-file' 12 ([BitConverter]::GetBytes([uint32]550))
Reject-ModifiedFixture 'missing-exception-stream' 56 ([BitConverter]::GetBytes([uint32]7))
Reject-ModifiedFixture 'empty-thread-list' 80 ([BitConverter]::GetBytes([uint32]0))
Reject-ModifiedFixture 'empty-module-list' 132 ([BitConverter]::GetBytes([uint32]0))
Reject-ModifiedFixture 'wrong-exception-thread' 244 ([BitConverter]::GetBytes([uint32]999))
Reject-ModifiedFixture 'context-outside-file' 408 ([BitConverter]::GetBytes([uint32]550))
Reject-ModifiedFixture 'unbounded-memory-range-count' 412 ([BitConverter]::GetBytes([uint64]1000001))
Reject-ModifiedFixture 'truncated-memory-payload' 436 ([BitConverter]::GetBytes([uint64]100000))
Reject-ModifiedFixture 'invalid-module-name-pointer' 156 ([BitConverter]::GetBytes([uint32]100000))
[pscustomobject]@{ status = 'PASS'; fixtureRoot = $fixtureRoot; checks = $checks } | ConvertTo-Json -Depth 4

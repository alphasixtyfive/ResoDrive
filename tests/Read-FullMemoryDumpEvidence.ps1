# Test-only structural acceptance. Read metadata/descriptors, never process-memory payloads.
# Layouts: Microsoft MINIDUMP_HEADER, DIRECTORY, THREAD_LIST, MODULE_LIST,
# EXCEPTION_STREAM and MEMORY64_LIST in minidumpapiset.h (packing 4).
function Read-FullMemoryDumpEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [string]$RequiredModule)
    $reader = [IO.BinaryReader]::new([IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    try {
        $length = [uint64]$reader.BaseStream.Length
        function Assert-DumpRange([uint64]$offset, [uint64]$size, [string]$label) {
            if ($offset -lt 32 -or $offset -gt $length -or $size -gt ($length - $offset)) { throw "Invalid minidump $label range." }
        }
        if ($length -lt 32 -or $reader.ReadUInt32() -ne 0x504d444d) { throw 'Invalid minidump signature/header.' }
        $formatVersion = $reader.ReadUInt32()
        if (($formatVersion -band 0xffff) -ne 0xa793) { throw 'Unsupported minidump format version.' }
        $count = $reader.ReadUInt32()
        $directory = $reader.ReadUInt32()
        $null = $reader.ReadUInt32() # checksum
        $null = $reader.ReadUInt32() # timestamp
        $flags = $reader.ReadUInt64()
        if (($flags -band 2) -ne 2) { throw 'The dump does not declare MiniDumpWithFullMemory.' }
        if ($count -lt 1 -or $count -gt 256) { throw 'Invalid minidump stream count.' }
        Assert-DumpRange $directory ([uint64]$count * 12) 'stream directory'
        $streams = @{}
        for ($index = 0; $index -lt $count; $index++) {
            $reader.BaseStream.Position = [long]$directory + $index * 12
            $type = $reader.ReadUInt32(); $size = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
            if ($type -eq 0 -and $size -eq 0) { continue }
            Assert-DumpRange $offset $size "stream $type"
            if ($streams.ContainsKey($type)) { throw "Duplicate minidump stream $type." }
            $streams[$type] = [pscustomobject]@{ offset = [long]$offset; size = [long]$size }
        }
        foreach ($type in [uint32[]]@(3, 4, 6, 9)) {
            if (-not $streams.ContainsKey($type) -or $streams[$type].size -eq 0) { throw "Required minidump stream $type is missing/empty." }
        }
        $threads = $streams[[uint32]3]
        if ($threads.size -lt 4) { throw 'Truncated thread list.' }
        $reader.BaseStream.Position = $threads.offset
        $threadCount = $reader.ReadUInt32()
        if ($threadCount -lt 1 -or $threadCount -gt 65536 -or (4 + [uint64]$threadCount * 48) -gt $threads.size) { throw 'Invalid minidump thread list.' }
        $threadIds = [Collections.Generic.HashSet[uint32]]::new()
        for ($index = 0; $index -lt $threadCount; $index++) {
            $reader.BaseStream.Position = $threads.offset + 4 + $index * 48
            $null = $threadIds.Add($reader.ReadUInt32())
        }
        $modules = $streams[[uint32]4]
        if ($modules.size -lt 4) { throw 'Truncated module list.' }
        $reader.BaseStream.Position = $modules.offset
        $moduleCount = $reader.ReadUInt32()
        if ($moduleCount -lt 1 -or $moduleCount -gt 65536 -or (4 + [uint64]$moduleCount * 108) -gt $modules.size) { throw 'Invalid minidump module list.' }
        $requiredModuleFound = [string]::IsNullOrEmpty($RequiredModule)
        if ($RequiredModule) {
            for ($index = 0; $index -lt $moduleCount; $index++) {
                $reader.BaseStream.Position = $modules.offset + 4 + $index * 108 + 20
                $nameOffset = $reader.ReadUInt32()
                Assert-DumpRange $nameOffset 4 'module name header'
                $reader.BaseStream.Position = $nameOffset
                $nameSize = $reader.ReadUInt32()
                if ($nameSize -gt 65536 -or ($nameSize -band 1) -ne 0) { throw 'Invalid minidump module name length.' }
                Assert-DumpRange ([uint64]$nameOffset + 4) $nameSize 'module name'
                $name = [Text.Encoding]::Unicode.GetString($reader.ReadBytes($nameSize))
                if ([IO.Path]::GetFileName($name).Equals($RequiredModule, [StringComparison]::OrdinalIgnoreCase)) { $requiredModuleFound = $true }
            }
            if (-not $requiredModuleFound) { throw "Required runtime module $RequiredModule is absent from the dump." }
        }
        $exception = $streams[[uint32]6]
        if ($exception.size -lt 168) { throw 'Truncated minidump exception stream.' }
        $reader.BaseStream.Position = $exception.offset
        $exceptionThread = $reader.ReadUInt32()
        if (-not $threadIds.Contains($exceptionThread)) { throw 'Exception thread is absent from the thread list.' }
        $reader.BaseStream.Position = $exception.offset + 8
        $exceptionCode = $reader.ReadUInt32()
        if (-not $exceptionCode) { throw 'Minidump exception code is empty.' }
        $reader.BaseStream.Position = $exception.offset + 160
        $contextSize = $reader.ReadUInt32(); $contextOffset = $reader.ReadUInt32()
        if (-not $contextSize) { throw 'Exception CPU context is empty.' }
        Assert-DumpRange $contextOffset $contextSize 'exception CPU context'
        $memory = $streams[[uint32]9]
        if ($memory.size -lt 16) { throw 'Truncated Memory64List stream.' }
        $reader.BaseStream.Position = $memory.offset
        $rangeCount = $reader.ReadUInt64(); $baseOffset = $reader.ReadUInt64()
        if ($rangeCount -lt 1 -or $rangeCount -gt 1000000 -or (16 + $rangeCount * 16) -gt $memory.size) { throw 'Invalid full-memory range count.' }
        Assert-DumpRange $baseOffset 0 'full-memory base'
        $remaining = $length - $baseOffset
        $memoryBytes = [uint64]0
        for ($index = 0; $index -lt $rangeCount; $index++) {
            $null = $reader.ReadUInt64() # virtual address; never dereferenced/exported
            $size = $reader.ReadUInt64()
            if ($size -gt $remaining) { throw 'Truncated/out-of-bounds full-memory payload.' }
            $remaining -= $size
            $memoryBytes += $size
        }
        if (-not $memoryBytes) { throw 'Full-memory payload is empty.' }
        return [pscustomobject]@{ streamCount = $count; threadCount = $threadCount; moduleCount = $moduleCount; exceptionCode = ('0x{0:X8}' -f $exceptionCode); memoryRanges = $rangeCount; memoryBytes = $memoryBytes; requiredModuleFound = $requiredModuleFound }
    } finally { $reader.Dispose() }
}

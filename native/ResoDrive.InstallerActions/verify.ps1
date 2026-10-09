param(
    [Parameter(Mandatory)][string] $LibraryPath,
    [Parameter(Mandatory)][string] $ApplicationPath,
    [Parameter(Mandatory)][string] $PolicyScriptPath
)

$ErrorActionPreference = 'Stop'
if (-not ('ResoDrive.InstallerResourceVerification' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ResoDrive {
    public static class InstallerResourceVerification {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")]
        public static extern bool FreeLibrary(IntPtr module);
    }
}
'@
}

function Read-U16([byte[]] $bytes, [long] $offset) {
    if ($offset -lt 0 -or $offset + 2 -gt $bytes.Length) { throw 'Invalid executable PE bounds.' }
    [BitConverter]::ToUInt16($bytes, [int]$offset)
}

function Read-U32([byte[]] $bytes, [long] $offset) {
    if ($offset -lt 0 -or $offset + 4 -gt $bytes.Length) { throw 'Invalid executable PE bounds.' }
    [BitConverter]::ToUInt32($bytes, [int]$offset)
}

function Assert-CetMarking([byte[]] $bytes) {
    if ((Read-U16 $bytes 0) -ne 0x5A4D) { throw 'The published app is not a PE executable.' }
    $pe = Read-U32 $bytes 0x3c
    if ((Read-U32 $bytes $pe) -ne 0x4550 -or (Read-U16 $bytes ($pe + 4)) -ne 0x8664) {
        throw 'The published application must be a Windows x64 executable.'
    }
    $sectionCount = Read-U16 $bytes ($pe + 6)
    $optionalSize = Read-U16 $bytes ($pe + 20)
    $optional = $pe + 24
    if ((Read-U16 $bytes $optional) -ne 0x20b -or $optionalSize -lt 168 -or
        (Read-U32 $bytes ($optional + 108)) -lt 7) { throw 'The published application has an invalid PE header.' }
    $debugRva = Read-U32 $bytes ($optional + 160)
    $debugSize = Read-U32 $bytes ($optional + 164)
    if (-not $debugRva -or -not $debugSize -or $debugSize % 28 -ne 0) {
        throw 'The published application has no valid CET debug metadata.'
    }
    $sectionStart = $optional + $optionalSize
    $debugOffset = $null
    for ($section = 0; $section -lt $sectionCount; $section++) {
        $header = $sectionStart + $section * 40
        $virtualSize = Read-U32 $bytes ($header + 8)
        $virtualAddress = Read-U32 $bytes ($header + 12)
        $rawSize = Read-U32 $bytes ($header + 16)
        $rawOffset = Read-U32 $bytes ($header + 20)
        if ($debugRva -ge $virtualAddress -and $debugRva -lt [long]$virtualAddress + [Math]::Max($virtualSize, $rawSize)) {
            $relative = [long]$debugRva - $virtualAddress
            if ($relative + $debugSize -gt $rawSize) { throw 'The application debug table is outside raw PE data.' }
            $debugOffset = [long]$rawOffset + $relative
            break
        }
    }
    if ($null -eq $debugOffset -or $debugOffset + $debugSize -gt $bytes.Length) {
        throw 'The application debug table cannot be read.'
    }
    $marked = $false
    for ($entry = 0; $entry -lt $debugSize; $entry += 28) {
        $directory = $debugOffset + $entry
        if ((Read-U32 $bytes ($directory + 12)) -ne 20) { continue }
        $size = Read-U32 $bytes ($directory + 16)
        $offset = Read-U32 $bytes ($directory + 24)
        if ($size -lt 4 -or [long]$offset + $size -gt $bytes.Length) { throw 'Invalid extended DLL characteristics.' }
        if (((Read-U32 $bytes $offset) -band 1) -eq 0) {
            throw 'The single published application must retain CET compatibility.'
        }
        $marked = $true
    }
    if (-not $marked) { throw 'The single published application must retain CET compatibility.' }
}

function Get-BytesHash([byte[]] $bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-Resource([IntPtr] $module, [int] $identifier, [byte[]] $expected) {
    $resource = [ResoDrive.InstallerResourceVerification]::FindResourceW($module, [IntPtr]$identifier, [IntPtr]10)
    if ($resource -eq [IntPtr]::Zero) { throw "Installer resource $identifier is missing." }
    $size = [ResoDrive.InstallerResourceVerification]::SizeofResource($module, $resource)
    if ($size -ne $expected.Length) { throw "Installer resource $identifier length differs from its shipped input." }
    $loaded = [ResoDrive.InstallerResourceVerification]::LoadResource($module, $resource)
    $address = [ResoDrive.InstallerResourceVerification]::LockResource($loaded)
    if ($address -eq [IntPtr]::Zero) { throw "Installer resource $identifier cannot be read." }
    $actual = [byte[]]::new($size)
    [Runtime.InteropServices.Marshal]::Copy($address, $actual, 0, $actual.Length)
    if ((Get-BytesHash $actual) -ne (Get-BytesHash $expected)) {
        throw "Installer resource $identifier hash differs from its shipped input."
    }
}

$application = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ApplicationPath).Path)
Assert-CetMarking $application
$scriptBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PolicyScriptPath).Path)
# DATAFILE_EXCLUSIVE reads resources without loading executable code
# or invoking DllMain, and keeps the library protected from writes during inspection.
$module = [ResoDrive.InstallerResourceVerification]::LoadLibraryExW(
    (Resolve-Path -LiteralPath $LibraryPath).Path, [IntPtr]::Zero, 0x40)
if ($module -eq [IntPtr]::Zero) {
    throw ('The native installer library cannot be inspected: Windows error ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
}
try {
    Assert-Resource $module 101 $application
    Assert-Resource $module 102 $scriptBytes
}
finally { $null = [ResoDrive.InstallerResourceVerification]::FreeLibrary($module) }
Write-Host 'Native installer resources match the shipped application and policy script; application CET marking retained.'

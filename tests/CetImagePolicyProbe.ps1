param([Parameter(Mandatory)][string]$ApplicationPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Image policy research requires a disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Image policy research requires an elevated disposable runner.'
}
Import-Module ProcessMitigations -ErrorAction Stop
# Research only: the 3xUInt64 image-policy ABI is observed in Microsoft's shipped
# ProcessMitigations module. This is not the process-only winnt.h DWORD policy.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CetImagePolicyProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowPolicy {
        public ulong UserShadowStack;
        public ulong SetContextIpValidation;
        public ulong BlockNonCetBinaries;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct CfgPolicy {
        public ulong ControlFlowGuard;
        public ulong StrictControlFlowGuard;
    }
    [DllImport("ntdll.dll", EntryPoint="RtlQueryImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int QueryShadow(string path, int policy, uint flags, out ShadowPolicy value, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlSetImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int SetShadow(string path, int policy, uint flags, ref ShadowPolicy value, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlQueryImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int QueryCfg(string path, int policy, uint flags, out CfgPolicy value, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlSetImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int SetCfg(string path, int policy, uint flags, ref CfgPolicy value, uint size);
    private static void Check(int status, string stage) {
        if (status != 0) throw new InvalidOperationException(stage+": NTSTATUS 0x"+unchecked((uint)status).ToString("X8"));
    }
    public static ShadowPolicy Read(string path, bool audit) {
        ShadowPolicy value;
        int status=QueryShadow(path,15,audit ? 8U : 0U,out value,24);
        if (unchecked((uint)status)==0xC0000034U) return default(ShadowPolicy);
        Check(status,"query CET "+(audit ? "audit" : "normal"));
        return value;
    }
    public static void Write(string path, ShadowPolicy value) {
        Check(SetShadow(path,15,0,ref value,24),"set CET normal");
    }
    public static ShadowPolicy WithState(ShadowPolicy value, uint state) {
        value.UserShadowStack=(value.UserShadowStack & ~3UL) | state;
        return value;
    }
    public static ShadowPolicy WithOriginalFirst(ShadowPolicy value, ShadowPolicy original) {
        value.UserShadowStack=original.UserShadowStack;
        return value;
    }
    public static ShadowPolicy WithSiblingOff(ShadowPolicy value) {
        value.SetContextIpValidation=(value.SetContextIpValidation & ~3UL) | 2UL;
        value.BlockNonCetBinaries=(value.BlockNonCetBinaries & ~3UL) | 2UL;
        return value;
    }
    public static CfgPolicy ReadCfg(string path) {
        CfgPolicy value;
        int status=QueryCfg(path,7,0,out value,16);
        if (unchecked((uint)status)==0xC0000034U) return default(CfgPolicy);
        Check(status,"query CFG normal"); return value;
    }
    public static void WriteCfg(string path, CfgPolicy value) {
        Check(SetCfg(path,7,0,ref value,16),"restore fixture CFG normal");
    }
}
'@
function Assert-ShadowEqual($Expected, $Actual, [string]$Stage) {
    foreach ($name in 'UserShadowStack','SetContextIpValidation','BlockNonCetBinaries') {
        if ($Expected.$name -ne $Actual.$name) { throw "$Stage changed $name unexpectedly." }
    }
}
function Read-WindowsPolicy([string]$Path) {
    $policies = @(Get-ProcessMitigation -Name $Path -ErrorAction Stop -WarningAction Stop)
    $exact = @($policies | Where-Object { $_.ProcessName -ieq $Path })
    if ($exact.Count -ne 1) { throw 'Windows did not return exactly one fixture path policy.' }
    return $exact[0]
}
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$fixtureDirectory = Join-Path $env:RUNNER_TEMP ('CetImagePolicyProbe-' + [guid]::NewGuid().ToString('N'))
$fixtureDirectory = [IO.Path]::GetFullPath($fixtureDirectory)
$runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
if (!$fixtureDirectory.StartsWith($runnerRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture escaped the disposable runner directory.'
}
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
# A unique executable name prevents collisions with all other policy fixtures.
$fixture = Join-Path $fixtureDirectory ('resodrive-image-policy-probe-' + [guid]::NewGuid().ToString('N') + '.exe')
Copy-Item -LiteralPath $application -Destination $fixture
$original = $null
$beforeSiblingSeed = $null
$cfgOriginal = $null
try {
    $cfgOriginal = [CetImagePolicyProbe]::ReadCfg($fixture)
    Set-ProcessMitigation -Name $fixture -Enable CFG -ErrorAction Stop -WarningAction Stop
    $beforeSiblingSeed = [CetImagePolicyProbe]::Read($fixture, $false)
    [CetImagePolicyProbe]::Write($fixture, [CetImagePolicyProbe]::WithSiblingOff($beforeSiblingSeed))
    $baseline = Read-WindowsPolicy $fixture
    if ($baseline.CFG.Enable.ToString() -ne 'ON') { throw 'Fixture CFG baseline was not ON.' }
    $original = [CetImagePolicyProbe]::Read($fixture, $false)
    if (($original.SetContextIpValidation -band 3) -ne 2 -or ($original.BlockNonCetBinaries -band 3) -ne 2) {
        throw 'Fixture nonzero sibling CET policy could not be seeded.'
    }
    $originalAudit = [CetImagePolicyProbe]::Read($fixture, $true)
    $baselineCfg = [CetImagePolicyProbe]::ReadCfg($fixture)
    $expected = [CetImagePolicyProbe]::WithState($original, 2)
    [CetImagePolicyProbe]::Write($fixture, $expected)
    $off = Read-WindowsPolicy $fixture
    if ($off.UserShadowStack.UserShadowStack.ToString() -ne 'OFF' -or $off.CFG.Enable.ToString() -ne 'ON') {
        throw 'Native CET-only write did not retain Windows CET OFF / CFG ON.'
    }
    Assert-ShadowEqual $expected ([CetImagePolicyProbe]::Read($fixture, $false)) 'CET OFF'
    Assert-ShadowEqual $originalAudit ([CetImagePolicyProbe]::Read($fixture, $true)) 'CET OFF audit'
    $cfg = [CetImagePolicyProbe]::ReadCfg($fixture)
    if ($cfg.ControlFlowGuard -ne $baselineCfg.ControlFlowGuard -or $cfg.StrictControlFlowGuard -ne $baselineCfg.StrictControlFlowGuard) {
        throw 'Native CET-only write changed CFG native fields.'
    }
    # Restore only our original first field using a fresh query, preserving siblings.
    $restore = [CetImagePolicyProbe]::WithOriginalFirst([CetImagePolicyProbe]::Read($fixture, $false), $original)
    [CetImagePolicyProbe]::Write($fixture, $restore)
    $restored = Read-WindowsPolicy $fixture
    if ($restored.UserShadowStack.UserShadowStack.ToString() -ne $baseline.UserShadowStack.UserShadowStack.ToString() -or
        $restored.CFG.Enable.ToString() -ne 'ON') { throw 'Native CET restoration changed the Windows baseline.' }
    Assert-ShadowEqual $original ([CetImagePolicyProbe]::Read($fixture, $false)) 'CET restore'
    Assert-ShadowEqual $originalAudit ([CetImagePolicyProbe]::Read($fixture, $true)) 'CET restore audit'
    Write-Host ('Native image policy probe passed: original CET {0}; OFF and restored; CFG ON preserved; sibling/audit fields unchanged.' -f $baseline.UserShadowStack.UserShadowStack)
} finally {
    # Restore only the unique fixture's native policies. No production name,
    # system policy, packed registry mitigation values or administrator state.
    if ($null -ne $original) {
        $cleanup = [CetImagePolicyProbe]::WithOriginalFirst([CetImagePolicyProbe]::Read($fixture, $false), $original)
        [CetImagePolicyProbe]::Write($fixture, $cleanup)
    }
    if ($null -ne $beforeSiblingSeed) { [CetImagePolicyProbe]::Write($fixture, $beforeSiblingSeed) }
    if ($null -ne $cfgOriginal) { [CetImagePolicyProbe]::WriteCfg($fixture, $cfgOriginal) }
    Remove-Item -LiteralPath $fixture
    Remove-Item -LiteralPath $fixtureDirectory
}

# A fresh exact-path filter must also preserve effective basename-wide policy.
# Use a second unique name, shared by two disposable fixture paths only.
$inheritDirectory = [IO.Path]::GetFullPath((Join-Path $env:RUNNER_TEMP ('CetImageInheritance-' + [guid]::NewGuid().ToString('N'))))
if (!$inheritDirectory.StartsWith($runnerRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Inheritance fixture escaped runner.' }
New-Item -ItemType Directory -Path $inheritDirectory | Out-Null
$otherDirectory = Join-Path $inheritDirectory 'other'
New-Item -ItemType Directory -Path $otherDirectory | Out-Null
$inheritName = 'resodrive-image-inheritance-' + [guid]::NewGuid().ToString('N') + '.exe'
$inheritPath = Join-Path $inheritDirectory $inheritName
$otherPath = Join-Path $otherDirectory $inheritName
Copy-Item -LiteralPath $application -Destination $inheritPath
Copy-Item -LiteralPath $application -Destination $otherPath
$baseCfg = $null
$inheritShadow = $null
try {
    $baseCfg = [CetImagePolicyProbe]::ReadCfg($inheritName)
    Set-ProcessMitigation -Name $inheritName -Enable CFG -ErrorAction Stop -WarningAction Stop
    $baseOn = [CetImagePolicyProbe]::ReadCfg($inheritName)
    $pathBefore = [CetImagePolicyProbe]::ReadCfg($inheritPath)
    $otherBefore = [CetImagePolicyProbe]::ReadCfg($otherPath)
    if (($baseOn.ControlFlowGuard -band 3) -ne 1 -or ($pathBefore.ControlFlowGuard -band 3) -ne 1 -or
        ($otherBefore.ControlFlowGuard -band 3) -ne 1) { throw 'Basename CFG ON did not apply before exact-path creation.' }
    $inheritShadow = [CetImagePolicyProbe]::Read($inheritPath, $false)
    $otherShadow = [CetImagePolicyProbe]::Read($otherPath, $false)
    $inheritAudit = [CetImagePolicyProbe]::Read($inheritPath, $true)
    [CetImagePolicyProbe]::Write($inheritPath, [CetImagePolicyProbe]::WithState($inheritShadow, 2))
    $pathAfter = [CetImagePolicyProbe]::ReadCfg($inheritPath)
    $baseAfter = [CetImagePolicyProbe]::ReadCfg($inheritName)
    $otherAfter = [CetImagePolicyProbe]::ReadCfg($otherPath)
    foreach ($pair in @(@($pathBefore,$pathAfter), @($baseOn,$baseAfter), @($otherBefore,$otherAfter))) {
        if ($pair[0].ControlFlowGuard -ne $pair[1].ControlFlowGuard -or $pair[0].StrictControlFlowGuard -ne $pair[1].StrictControlFlowGuard) {
            throw 'Creating an exact-path CET filter changed effective basename CFG inheritance or another path.'
        }
    }
    $windowsPath = Read-WindowsPolicy $inheritPath
    if ($windowsPath.CFG.Enable.ToString() -ne 'ON' -or $windowsPath.UserShadowStack.UserShadowStack.ToString() -ne 'OFF') {
        throw 'Windows exact-path view lost basename CFG ON after CET OFF.'
    }
    Assert-ShadowEqual $otherShadow ([CetImagePolicyProbe]::Read($otherPath, $false)) 'Other path CET'
    Assert-ShadowEqual $inheritAudit ([CetImagePolicyProbe]::Read($inheritPath, $true)) 'Inherited CET audit'
    Write-Host 'Native image inheritance probe passed: basename CFG ON and another path unchanged by exact-path CET OFF.'
} finally {
    if ($null -ne $inheritShadow) {
        [CetImagePolicyProbe]::Write($inheritPath, [CetImagePolicyProbe]::WithOriginalFirst([CetImagePolicyProbe]::Read($inheritPath, $false), $inheritShadow))
    }
    if ($null -ne $baseCfg) { [CetImagePolicyProbe]::WriteCfg($inheritName, $baseCfg) }
    Remove-Item -LiteralPath $inheritPath
    Remove-Item -LiteralPath $otherPath
    Remove-Item -LiteralPath $otherDirectory
    Remove-Item -LiteralPath $inheritDirectory
}

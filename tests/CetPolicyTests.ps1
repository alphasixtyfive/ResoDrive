param([switch] $Native)

# Tests the actual transaction function with in-memory registry/Windows boundaries.
# No machine mitigation or installed application is changed by these tests.
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\CetPolicy.ps1'
$parseErrors = $null
$tree = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'CetPolicy.ps1 has a syntax error.' }
foreach ($name in @('Valid-PolicyValue', 'Parse-State', 'Invoke-CetAction')) {
    $function = $tree.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
    }, $false) | Where-Object { $_.Name -eq $name }
    if (@($function).Count -ne 1) { throw "Missing production function $name." }
    . ([ScriptBlock]::Create($function.Extent.Text))
}
$script:installedPath = 'C:\ResoDrive-Cet-Test\resodrive.exe'
$script:passed = 0

function Assert([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
    $script:passed++
}
function Reset-Fixture([string] $policy) {
    $script:state = @{}
    $script:policy = $policy
    $script:writes = 0
    $script:loads = 0
    $script:unsupported = $false
    $script:failAfterPolicyWrite = $false
    $script:conflictingOptions = $false
    $script:filterExists = $false
    $script:filterHasAdminSettings = $false
}
function Read-State([string] $name) { return $script:state[$name] }
function Write-State([string] $name, [string] $value) { $script:state[$name] = $value; $script:writes++ }
function Delete-State([string] $name) { $script:state.Remove($name); $script:writes++ }
function Load-MitigationApi {
    $script:loads++
    if ($script:unsupported) { throw 'Unsupported Windows capability.' }
}
function Read-Policy([bool] $forOptOut = $false) {
    if ($forOptOut -and $script:conflictingOptions) { throw 'Administrator CET audit or strict mode requires shadow stacks.' }
    return $script:policy
}
function Off-Policy([string] $original) {
    $word = [Convert]::ToUInt64($original, 16)
    return (($word -bxor ($word -band 3)) -bor 2).ToString('X16')
}
function Find-Filter { if ($script:filterExists) { return 'fixture-filter' } }
function Remove-OwnedEmptyFilter { if (-not $script:filterHasAdminSettings) { $script:filterExists = $false } }
function Write-Policy([string] $value, [string] $expected) {
    Assert ($script:state.ContainsKey('Pending')) 'Policy changed without a durable undo record.'
    if ($script:policy -ne $expected) { throw 'Administrator changed the setting before the native write.' }
    $script:policy = $value
    $script:filterExists = $true
    $script:writes++
    if ($script:failAfterPolicyWrite) { throw 'Injected failure after the Windows write.' }
}
function Expect-Failure([ScriptBlock] $operation) {
    $failed = $false
    try { & $operation } catch { $failed = $true }
    Assert $failed 'An unsafe or unsupported operation should fail.'
}

Reset-Fixture '0000000000000001'
$script:unsupported = $true
Invoke-CetAction 'Apply' '0'
Assert ($script:policy -eq '0000000000000001' -and $script:writes -eq 0 -and $script:loads -eq 0) 'Fresh default0 must not touch Windows or installer state.'

foreach ($original in @('0000000000000001', '0000000000000002', '0000000000000000', '0000000000000005', '0000000000000006')) {
    Reset-Fixture $original
    $off = Off-Policy $original
    Invoke-CetAction 'Apply' '1'
    Assert ($script:policy -eq $off) "Opt-out from $original failed."
    Assert ((Parse-State $script:state.Baseline $false).Original -eq $original) 'The original override was not saved.'
    Invoke-CetAction 'Commit' '0'
    Assert (-not $script:state.ContainsKey('Pending')) 'Commit did not clear the transaction.'

    # Repair/upgrade must carry forward the first baseline, not save our OFF value.
    Invoke-CetAction 'Apply' '1'
    Invoke-CetAction 'Commit' '0'
    Assert ((Parse-State $script:state.Baseline $false).Original -eq $original) 'Upgrade overwrote the baseline.'
    $beforeRemoval = $script:state.Baseline
    Invoke-CetAction 'Remove' '0'
    Assert ($script:policy -eq $original -and $script:state.Baseline -eq $beforeRemoval) 'Uninstall must retain its baseline until commit.'
    Invoke-CetAction 'Rollback' '0'
    Assert ($script:policy -eq $off -and $script:state.Baseline -eq $beforeRemoval) 'Failed uninstall did not restore the prior opt-out.'
    Invoke-CetAction 'Remove' '0'
    Invoke-CetAction 'Commit' '0'
    Assert ($script:policy -eq $original -and $script:state.Count -eq 0) 'Committed uninstall did not restore the baseline and clear its receipts.'
}

Reset-Fixture '0000000000000000'
Invoke-CetAction 'Apply' '1'
Invoke-CetAction 'Commit' '0'
$saved = $script:state.Baseline
Invoke-CetAction 'Apply' '0'
Assert ($script:policy -eq '0000000000000000' -and $script:state.Baseline -eq $saved) 'Explicit0 did not remove only our override.'
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq '0000000000000002' -and $script:state.Baseline -eq $saved) 'Explicit0 rollback did not restore the prior opt-out.'
Invoke-CetAction 'Apply' '0'
Invoke-CetAction 'Commit' '0'
Assert ($script:policy -eq '0000000000000000' -and $script:state.Count -eq 0) 'Explicit0 commit left owned state behind.'

foreach ($edited in @('0000000000000001', '0000000000000000')) {
    Reset-Fixture '0000000000000000'
    Invoke-CetAction 'Apply' '1'
    Invoke-CetAction 'Commit' '0'
    $script:policy = $edited
    Invoke-CetAction 'Remove' '0'
    Invoke-CetAction 'Commit' '0'
    Assert ($script:policy -eq $edited) 'Uninstall overwrote a subsequent administrator edit.'
}

Reset-Fixture '0000000000000000'
Invoke-CetAction 'Apply' '1'
Invoke-CetAction 'Commit' '0'
$firstBaseline = $script:state.Baseline
$script:policy = '0000000000000001'
Invoke-CetAction 'Apply' '1'
Assert ((Parse-State $script:state.Baseline $false).Original -eq '0000000000000001') 'A subsequent explicit opt-out must preserve the latest administrator baseline.'
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq '0000000000000001' -and $script:state.Baseline -eq $firstBaseline) 'Rollback did not restore both pre-transaction values.'

Reset-Fixture '0000000000000001'
$script:failAfterPolicyWrite = $true
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:policy -eq '0000000000000002' -and $script:state.ContainsKey('Pending')) 'Failure lost its durable undo record.'
$script:failAfterPolicyWrite = $false
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq '0000000000000001' -and $script:state.Count -eq 0) 'Partial apply was not recoverable.'

Reset-Fixture '0000000000000001'
Invoke-CetAction 'Apply' '1'
$unfinished = $script:state.Pending
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:state.Pending -eq $unfinished) 'A second transaction overwrote the first undo record.'
$script:policy = '0000000000000000'
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq '0000000000000000') 'Rollback overwrote an administrator edit after apply.'

Reset-Fixture '0000000000000001'
$script:unsupported = $true
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:writes -eq 0) 'An unsupported requested opt-out must fail before any write.'

Reset-Fixture '0000000000000001'
$script:conflictingOptions = $true
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:writes -eq 0 -and -not $script:state.ContainsKey('Pending')) 'A conflicting administrator CET option must fail before any write.'

Reset-Fixture '0000000000000001'
$script:state.Baseline = '{"Schema":2,"ExecutablePath":"C:\\Other\\resodrive.exe","Original":"0000000000000001","Applied":"0000000000000002"}'
Expect-Failure { Invoke-CetAction 'Remove' '0' }
Assert ($script:writes -eq 0 -and $script:loads -eq 0) 'State from another installation was accepted.'

foreach ($invalid in @('OFF', '0000000000000003', '000000000000000g', 'FFFFFFFFFFFFFFFF')) {
    Assert (-not (Valid-PolicyValue $invalid)) 'Malformed native policy metadata was accepted.'
}
Reset-Fixture '0000000000000000'
Invoke-CetAction 'Apply' '1'
Invoke-CetAction 'Commit' '0'
# An administrator can keep OFF while changing Force metadata; that is their edit.
$script:policy = '0000000000000006'
Invoke-CetAction 'Remove' '0'
Invoke-CetAction 'Commit' '0'
Assert ($script:policy -eq '0000000000000006') 'Uninstall overwrote an administrator Force edit.'

Reset-Fixture '0000000000000000'
Invoke-CetAction 'Apply' '1'
Invoke-CetAction 'Commit' '0'
Assert ($script:filterExists -and -not (Parse-State $script:state.Baseline $false).OriginalFilterExists) 'New filter ownership was not recorded.'
Invoke-CetAction 'Apply' '0'
Assert (-not $script:filterExists) 'Opting back in retained our empty filter.'
Invoke-CetAction 'Rollback' '0'
Assert ($script:filterExists -and $script:policy -eq '0000000000000002') 'Rollback did not restore the previous opt-out filter.'
Invoke-CetAction 'Remove' '0'
Invoke-CetAction 'Commit' '0'
Assert (-not $script:filterExists) 'Committed uninstall retained our empty filter.'

foreach ($preexisting in @($true, $false)) {
    Reset-Fixture '0000000000000000'
    $script:filterExists = $preexisting
    Invoke-CetAction 'Apply' '1'
    Invoke-CetAction 'Commit' '0'
    $script:filterHasAdminSettings = -not $preexisting
    Invoke-CetAction 'Remove' '0'
    Invoke-CetAction 'Commit' '0'
    Assert $script:filterExists 'An existing or administrator-edited filter was removed.'
}

# Exercise the actual script's diagnostic boundary before any registry/policy access.
$diagnosticFixture = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-cet-diagnostic-' + [Guid]::NewGuid().ToString('N'))
$copiedScript = Join-Path $diagnosticFixture 'CetPolicy.ps1'
$diagnosticFile = Join-Path $diagnosticFixture 'cet-policy-error.txt'
$outsideFile = Join-Path $diagnosticFixture 'outside.txt'
$testPowerShell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
$null = New-Item -ItemType Directory -Path $diagnosticFixture
Copy-Item -LiteralPath $source -Destination $copiedScript
$previousErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue' # These native invocations intentionally fail.
    & $testPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $copiedScript `
        -Action Apply -ExecutablePath 'relative\resodrive.exe' -DiagnosticPath $diagnosticFile 2>$null
    $failureExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert ($failureExit -eq 1 -and [IO.File]::Exists($diagnosticFile)) 'A policy failure did not produce its private diagnostic receipt.'
    $bytes = [IO.File]::ReadAllBytes($diagnosticFile)
    $message = [Text.Encoding]::UTF8.GetString($bytes)
    Assert ($bytes.Length -le 4096 -and $message.StartsWith('ResoDrive CET compatibility: ')) 'The native diagnostic receipt was not bounded UTF8.'
    Assert ($message.Contains('absolute installed')) 'The diagnostic receipt lost its specific failure reason.'
    [IO.File]::Delete($diagnosticFile)

    $ErrorActionPreference = 'Continue'
    & $testPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $copiedScript `
        -Action Apply -ExecutablePath 'C:\ResoDrive-Cet-Test\resodrive.exe' -DiagnosticPath $outsideFile 2>$null
    $failureExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert ($failureExit -eq 1 -and -not [IO.File]::Exists($outsideFile) -and -not [IO.File]::Exists($diagnosticFile)) 'An arbitrary diagnostic path was accepted.'
}
finally {
    $ErrorActionPreference = $previousErrorAction
    if ([IO.File]::Exists($diagnosticFile)) { [IO.File]::Delete($diagnosticFile) }
    [IO.File]::Delete($copiedScript)
    [IO.Directory]::Delete($diagnosticFixture)
}

Write-Host "CET policy transaction checks passed ($script:passed assertions); Windows boundaries simulated."

if ($Native) {
    # Opt-in acceptance for an elevated disposable Windows runner. Refuse to touch
    # existing installer-owned state; the fixture has its own unique executable path.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Native CET policy checks require an elevated disposable runner.'
        }
    }
    finally { $identity.Dispose() }
    $nativeRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    $nativeStatePath = 'SOFTWARE\ResoDrive\CetCompatibility'
    $existing = $nativeRegistry.OpenSubKey($nativeStatePath)
    $keyExisted = $null -ne $existing
    try {
        if ($null -ne $existing -and ($existing.ValueCount -ne 0 -or $existing.SubKeyCount -ne 0)) {
            throw 'Native CET fixtures require no existing installer-owned CET state.'
        }
    }
    finally { if ($null -ne $existing) { $existing.Dispose() } }

    $fixtureIfeoPath = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\resodrive.exe'
    $existingIfeo = $nativeRegistry.OpenSubKey($fixtureIfeoPath)
    if ($null -ne $existingIfeo) {
        $existingIfeo.Dispose()
        throw 'Native CET fixtures require no existing resodrive.exe IFEO configuration.'
    }
    # Read actual native fields independently of the production wrapper and the
    # PowerShell getter, whose output omits sibling image-policy data.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CetPolicyFixture {
    [StructLayout(LayoutKind.Sequential)] public struct Shadow {
        public ulong UserShadowStack, SetContextIpValidation, BlockNonCetBinaries;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Cfg {
        public ulong ControlFlowGuard, StrictControlFlowGuard;
    }
    [DllImport("ntdll.dll", EntryPoint="RtlQueryImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int QueryShadow(string path, int family, uint flags, out Shadow value, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlSetImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int SetShadow(string path, int family, uint flags, ref Shadow value, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlQueryImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int QueryCfg(string path, int family, uint flags, out Cfg value, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlSetImageMitigationPolicy", CharSet=CharSet.Unicode)]
    private static extern int SetCfg(string path, int family, uint flags, ref Cfg value, uint size);
    private static void Check(int status) {
        if (status != 0) throw new InvalidOperationException("Fixture policy NTSTATUS 0x" + unchecked((uint)status).ToString("X8"));
    }
    public static Shadow Read(string path, bool audit) {
        Shadow value; int status = QueryShadow(path, 15, audit ? 8U : 0U, out value, 24);
        if (unchecked((uint)status) == 0xC0000034U) return default(Shadow);
        Check(status); return value;
    }
    public static void Write(string path, Shadow value) { Check(SetShadow(path, 15, 0, ref value, 24)); }
    public static Cfg ReadCfg(string path) {
        Cfg value; int status = QueryCfg(path, 7, 0, out value, 16);
        if (unchecked((uint)status) == 0xC0000034U) return default(Cfg);
        Check(status); return value;
    }
    public static void WriteCfg(string path, Cfg value) { Check(SetCfg(path, 7, 0, ref value, 16)); }
}
'@

    $systemDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    $nativePowerShell = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe'
    $nativeModule = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\Modules\ProcessMitigations\ProcessMitigations.psd1'
    Import-Module $nativeModule -ErrorAction Stop
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-cet-policy-' + [Guid]::NewGuid().ToString('N'))
    $fixtureExecutable = Join-Path $fixture 'resodrive.exe'
    $fixtureScript = Join-Path $fixture 'CetPolicy.ps1'
    $fixtureDiagnostic = Join-Path $fixture 'cet-policy-error.txt'
    $otherExecutable = Join-Path $fixture 'other\resodrive.exe'
    $null = New-Item -ItemType Directory -Path $fixture
    $null = New-Item -ItemType Directory -Path (Split-Path $otherExecutable -Parent)
    Copy-Item -LiteralPath (Join-Path $systemDirectory 'cmd.exe') -Destination $fixtureExecutable
    Copy-Item -LiteralPath $fixtureExecutable -Destination $otherExecutable
    Copy-Item -LiteralPath $source -Destination $fixtureScript

    function Invoke-NativeAction([string] $operation, [string] $disable = '0') {
        Write-Host "Native CET action: $operation DisableCet=$disable"
        if ([IO.File]::Exists($fixtureDiagnostic)) { [IO.File]::Delete($fixtureDiagnostic) }
        $actionErrorPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue' # Collect the child's specific error and exit status.
            & $nativePowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $fixtureScript `
                -Action $operation -ExecutablePath $fixtureExecutable -DisableCet $disable -DiagnosticPath $fixtureDiagnostic
            $actionExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $actionErrorPreference }
        if ($actionExit -ne 0) {
            $reason = if ([IO.File]::Exists($fixtureDiagnostic)) { [IO.File]::ReadAllText($fixtureDiagnostic) } else { 'No diagnostic receipt.' }
            throw "Native CET action $operation failed with exit ${actionExit}: $reason"
        }
    }
    function Assert-NativePolicy([string] $cet) {
        $actual = ProcessMitigations\Get-ProcessMitigation -Name $fixtureExecutable -ErrorAction Stop
        Write-Host ('Native policy readback: ' + (ConvertTo-Json -InputObject $actual -Depth 4 -Compress))
        Assert ($null -ne $actual -and $actual.UserShadowStack.UserShadowStack.ToString() -eq $cet) 'Native CET readback did not match the requested state.'
        Assert ($actual.Cfg.Enable.ToString() -eq 'ON') 'A different administrator mitigation was overwritten.'
        Assert ($actual.UserShadowStack.AuditUserShadowStack.ToString() -eq 'NOTSET') 'A different CET option was overwritten.'
        $normal = [CetPolicyFixture]::Read($fixtureExecutable, $false)
        Assert ($normal.SetContextIpValidation -eq 2 -and $normal.BlockNonCetBinaries -eq 2) 'Nonzero native CET sibling fields were overwritten.'
        $audit = [CetPolicyFixture]::Read($fixtureExecutable, $true)
        Assert ($audit.UserShadowStack -eq $initialAudit.UserShadowStack -and
            $audit.SetContextIpValidation -eq $initialAudit.SetContextIpValidation -and
            $audit.BlockNonCetBinaries -eq $initialAudit.BlockNonCetBinaries) 'A native CET audit field was overwritten.'
        $cfg = [CetPolicyFixture]::ReadCfg($fixtureExecutable)
        Assert ($cfg.ControlFlowGuard -eq $initialCfg.ControlFlowGuard -and
            $cfg.StrictControlFlowGuard -eq $initialCfg.StrictControlFlowGuard) 'A native CFG field was overwritten.'
    }
    function Native-FilterExists {
        $root = $nativeRegistry.OpenSubKey($fixtureIfeoPath)
        if ($null -eq $root) { return $false }
        try {
            foreach ($name in $root.GetSubKeyNames()) {
                $filter = $root.OpenSubKey($name)
                try { if ($filter.GetValue('FilterFullPath') -eq $fixtureExecutable) { return $true } }
                finally { $filter.Dispose() }
            }
            return $false
        }
        finally { $root.Dispose() }
    }
    $nativeFailure = $null
    $cleanupFailures = New-Object 'Collections.Generic.List[string]'
    try {
        Invoke-NativeAction 'Apply' '0'
        $afterDefault = $nativeRegistry.OpenSubKey($nativeStatePath)
        try {
            Assert ($null -eq $afterDefault -or $afterDefault.ValueCount -eq 0) 'Native default0 wrote installer state.'
        }
        finally { if ($null -ne $afterDefault) { $afterDefault.Dispose() } }
        # A fresh opt-out owns a new filter. Restoring NOTSET must remove that
        # empty filter, so later basename administrator rules are inherited again.
        Invoke-NativeAction 'Apply' '1'
        Invoke-NativeAction 'Commit'
        Assert (Native-FilterExists) 'Fresh opt-out did not create an exact executable filter.'
        Invoke-NativeAction 'Apply' '0'
        Assert (-not (Native-FilterExists)) 'Opting back in retained the owned empty filter.'
        Invoke-NativeAction 'Rollback'
        Assert ((Native-FilterExists) -and ([CetPolicyFixture]::Read($fixtureExecutable, $false)).UserShadowStack -eq 2) 'Rollback did not restore the previous fresh opt-out.'
        Invoke-NativeAction 'Remove'
        Invoke-NativeAction 'Commit'
        Assert (-not (Native-FilterExists)) 'Uninstall retained the owned empty filter.'
        $basenameRule = New-Object CetPolicyFixture+Cfg
        $basenameRule.ControlFlowGuard = 1
        [CetPolicyFixture]::WriteCfg('resodrive.exe', $basenameRule)
        Assert (([CetPolicyFixture]::ReadCfg($fixtureExecutable)).ControlFlowGuard -eq 1 -and
            ([CetPolicyFixture]::ReadCfg($otherExecutable)).ControlFlowGuard -eq 1) 'Removed opt-out filter still shadowed a future administrator basename policy.'
        $emptyRoot = $nativeRegistry.OpenSubKey($fixtureIfeoPath)
        try { Assert ($emptyRoot.SubKeyCount -eq 0) 'The fresh fixture unexpectedly retained an IFEO filter.' }
        finally { $emptyRoot.Dispose() }
        $nativeRegistry.DeleteSubKey($fixtureIfeoPath, $false) # Only the fixture's just-created basename CFG rule.
        # Audit ON requires shadow stacks ON, so it cannot be combined with the
        # requested OFF fixture. Preserve its original NOTSET value and CFG ON.
        $null = ProcessMitigations\Set-ProcessMitigation -Name $fixtureExecutable -Enable CFG -ErrorAction Stop
        $seed = [CetPolicyFixture]::Read($fixtureExecutable, $false)
        $seed.SetContextIpValidation = 2
        $seed.BlockNonCetBinaries = 2
        [CetPolicyFixture]::Write($fixtureExecutable, $seed)
        $initialAudit = [CetPolicyFixture]::Read($fixtureExecutable, $true)
        $initialCfg = [CetPolicyFixture]::ReadCfg($fixtureExecutable)
        Write-Host ('Native initial policy: ' + (ConvertTo-Json -InputObject (ProcessMitigations\Get-ProcessMitigation -Name $fixtureExecutable) -Depth 4 -Compress))
        Invoke-NativeAction 'Apply' '1'
        Assert-NativePolicy 'OFF'
        Invoke-NativeAction 'Commit'
        Invoke-NativeAction 'Apply' '1'
        Invoke-NativeAction 'Commit'
        Assert-NativePolicy 'OFF'
        Invoke-NativeAction 'Apply' '0'
        Assert-NativePolicy 'NOTSET'
        Invoke-NativeAction 'Rollback'
        Assert-NativePolicy 'OFF'
        Invoke-NativeAction 'Remove'
        Invoke-NativeAction 'Rollback'
        Assert-NativePolicy 'OFF'
        Invoke-NativeAction 'Remove'
        Invoke-NativeAction 'Commit'
        Assert-NativePolicy 'NOTSET'

        # Deliberately create a basename administrator rule in this otherwise
        # empty disposable namespace. The actual installer must reject it before
        # changing any state, inheritance, sibling field or other executable.
        [CetPolicyFixture]::WriteCfg('resodrive.exe', $basenameRule)
        $pathBefore = [CetPolicyFixture]::Read($fixtureExecutable, $false)
        $otherBefore = [CetPolicyFixture]::Read($otherExecutable, $false)
        $basenameCfg = [CetPolicyFixture]::ReadCfg('resodrive.exe')
        $otherCfg = [CetPolicyFixture]::ReadCfg($otherExecutable)
        Expect-Failure { Invoke-NativeAction 'Apply' '1' }
        Assert ([IO.File]::ReadAllText($fixtureDiagnostic).Contains('every copy')) 'The basename conflict did not provide an actionable diagnostic.'
        $rejectedState = $nativeRegistry.OpenSubKey($nativeStatePath)
        try { Assert ($null -eq $rejectedState -or $rejectedState.ValueCount -eq 0) 'Rejected basename policy wrote a transaction receipt.' }
        finally { if ($null -ne $rejectedState) { $rejectedState.Dispose() } }
        foreach ($comparison in @(
            @($pathBefore, [CetPolicyFixture]::Read($fixtureExecutable, $false)),
            @($otherBefore, [CetPolicyFixture]::Read($otherExecutable, $false)))) {
            foreach ($field in @('UserShadowStack', 'SetContextIpValidation', 'BlockNonCetBinaries')) {
                Assert ($comparison[0].$field -eq $comparison[1].$field) 'Rejected basename policy changed a CET image field.'
            }
        }
        Assert (([CetPolicyFixture]::ReadCfg('resodrive.exe')).ControlFlowGuard -eq $basenameCfg.ControlFlowGuard -and
            ([CetPolicyFixture]::ReadCfg($otherExecutable)).ControlFlowGuard -eq $otherCfg.ControlFlowGuard) 'Rejected basename policy changed administrator inheritance.'
        Write-Host 'Native CET policy checks passed: transactions preserve CFG, nonzero sibling/audit fields; basename conflicts leave policies untouched.'
    }
    catch {
        $nativeFailure = $_
        Write-Host ('FIRST native CET acceptance failure: ' + $_.Exception.Message)
        try {
            Write-Host ('Native failure policy: ' + (ConvertTo-Json -InputObject (ProcessMitigations\Get-ProcessMitigation -Name $fixtureExecutable) -Depth 4 -Compress))
            $failureState = $nativeRegistry.OpenSubKey($nativeStatePath)
            if ($null -ne $failureState) {
                try {
                    Write-Host ('Native Baseline: ' + $failureState.GetValue('Baseline'))
                    Write-Host ('Native Pending: ' + $failureState.GetValue('Pending'))
                }
                finally { $failureState.Dispose() }
            }
        }
        catch { Write-Warning ('Failure evidence could not be collected: ' + $_.Exception.Message) }
    }
    finally {
        # Restore only this fixture and the installer state created during its test.
        foreach ($operation in @('Rollback', 'Remove', 'Commit')) {
            try { Invoke-NativeAction $operation }
            catch { $cleanupFailures.Add($_.Exception.Message); Write-Warning ('Cleanup: ' + $_.Exception.Message) }
        }
        try {
            # This namespace was proven absent before the test. Verify every
            # generated filter belongs to one of our two disposable paths before
            # removing the fixture configuration; never use the broken cmdlet.
            $fixtureIfeo = $nativeRegistry.OpenSubKey($fixtureIfeoPath)
            if ($null -ne $fixtureIfeo) {
                try {
                    foreach ($name in $fixtureIfeo.GetSubKeyNames()) {
                        $filter = $fixtureIfeo.OpenSubKey($name)
                        try {
                            if ($filter.GetValue('FilterFullPath') -notin @($fixtureExecutable, $otherExecutable)) {
                                throw 'Fixture cleanup found an unexpected IFEO filter.'
                            }
                        }
                        finally { $filter.Dispose() }
                    }
                }
                finally { $fixtureIfeo.Dispose() }
                $nativeRegistry.DeleteSubKeyTree($fixtureIfeoPath, $false)
            }
        }
        catch { $cleanupFailures.Add($_.Exception.Message); Write-Warning ('Fixture policy cleanup: ' + $_.Exception.Message) }
        $remaining = $null
        try {
            $remaining = $nativeRegistry.OpenSubKey($nativeStatePath)
            if (-not $keyExisted -and $null -ne $remaining -and $remaining.ValueCount -eq 0 -and $remaining.SubKeyCount -eq 0) {
                $remaining.Dispose()
                $remaining = $null
                $nativeRegistry.DeleteSubKey($nativeStatePath, $false)
            }
        }
        catch { $cleanupFailures.Add($_.Exception.Message); Write-Warning ('Fixture state cleanup: ' + $_.Exception.Message) }
        finally {
            if ($null -ne $remaining) { $remaining.Dispose() }
            $nativeRegistry.Dispose()
        }
        try {
            if ([IO.File]::Exists($fixtureDiagnostic)) { [IO.File]::Delete($fixtureDiagnostic) }
            [IO.File]::Delete($fixtureScript)
            [IO.File]::Delete($fixtureExecutable)
            [IO.File]::Delete($otherExecutable)
            [IO.Directory]::Delete((Split-Path $otherExecutable -Parent))
            [IO.Directory]::Delete($fixture)
        }
        catch { $cleanupFailures.Add($_.Exception.Message); Write-Warning ('Fixture file cleanup: ' + $_.Exception.Message) }
    }
    if ($null -ne $nativeFailure) { throw $nativeFailure }
    if ($cleanupFailures.Count -gt 0) { throw ('Native CET fixture cleanup failed: ' + ($cleanupFailures -join '; ')) }
}

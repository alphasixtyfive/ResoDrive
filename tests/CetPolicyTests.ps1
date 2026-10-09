param([switch] $Native)

# Tests the actual transaction function with in-memory registry/Windows boundaries.
# No machine mitigation or installed application is changed by these tests.
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\CetPolicy.ps1'
$parseErrors = $null
$tree = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'CetPolicy.ps1 has a syntax error.' }
foreach ($name in @('Parse-State', 'Invoke-CetAction')) {
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
}
function Read-State([string] $name) { return $script:state[$name] }
function Write-State([string] $name, [string] $value) { $script:state[$name] = $value; $script:writes++ }
function Delete-State([string] $name) { $script:state.Remove($name); $script:writes++ }
function Load-MitigationModule {
    $script:loads++
    if ($script:unsupported) { throw 'Unsupported Windows capability.' }
}
function Read-Policy([bool] $forOptOut = $false) {
    if ($forOptOut -and $script:conflictingOptions) { throw 'Administrator CET audit or strict mode requires shadow stacks.' }
    return $script:policy
}
function Write-Policy([string] $value) {
    Assert ($script:state.ContainsKey('Pending')) 'Policy changed without a durable undo record.'
    $script:policy = $value
    $script:writes++
    if ($script:failAfterPolicyWrite) { throw 'Injected failure after the Windows write.' }
}
function Expect-Failure([ScriptBlock] $operation) {
    $failed = $false
    try { & $operation } catch { $failed = $true }
    Assert $failed 'An unsafe or unsupported operation should fail.'
}

Reset-Fixture 'ON'
$script:unsupported = $true
Invoke-CetAction 'Apply' '0'
Assert ($script:policy -eq 'ON' -and $script:writes -eq 0 -and $script:loads -eq 0) 'Fresh default0 must not touch Windows or installer state.'

foreach ($original in @('ON', 'OFF', 'NOTSET')) {
    Reset-Fixture $original
    Invoke-CetAction 'Apply' '1'
    Assert ($script:policy -eq 'OFF') "Opt-out from $original failed."
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
    Assert ($script:policy -eq 'OFF' -and $script:state.Baseline -eq $beforeRemoval) 'Failed uninstall did not restore the prior opt-out.'
    Invoke-CetAction 'Remove' '0'
    Invoke-CetAction 'Commit' '0'
    Assert ($script:policy -eq $original -and $script:state.Count -eq 0) 'Committed uninstall did not restore the baseline and clear its receipts.'
}

Reset-Fixture 'NOTSET'
Invoke-CetAction 'Apply' '1'
Invoke-CetAction 'Commit' '0'
$saved = $script:state.Baseline
Invoke-CetAction 'Apply' '0'
Assert ($script:policy -eq 'NOTSET' -and $script:state.Baseline -eq $saved) 'Explicit0 did not remove only our override.'
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq 'OFF' -and $script:state.Baseline -eq $saved) 'Explicit0 rollback did not restore the prior opt-out.'
Invoke-CetAction 'Apply' '0'
Invoke-CetAction 'Commit' '0'
Assert ($script:policy -eq 'NOTSET' -and $script:state.Count -eq 0) 'Explicit0 commit left owned state behind.'

foreach ($edited in @('ON', 'NOTSET')) {
    Reset-Fixture 'NOTSET'
    Invoke-CetAction 'Apply' '1'
    Invoke-CetAction 'Commit' '0'
    $script:policy = $edited
    Invoke-CetAction 'Remove' '0'
    Invoke-CetAction 'Commit' '0'
    Assert ($script:policy -eq $edited) 'Uninstall overwrote a subsequent administrator edit.'
}

Reset-Fixture 'NOTSET'
Invoke-CetAction 'Apply' '1'
Invoke-CetAction 'Commit' '0'
$firstBaseline = $script:state.Baseline
$script:policy = 'ON'
Invoke-CetAction 'Apply' '1'
Assert ((Parse-State $script:state.Baseline $false).Original -eq 'ON') 'A subsequent explicit opt-out must preserve the latest administrator baseline.'
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq 'ON' -and $script:state.Baseline -eq $firstBaseline) 'Rollback did not restore both pre-transaction values.'

Reset-Fixture 'ON'
$script:failAfterPolicyWrite = $true
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:policy -eq 'OFF' -and $script:state.ContainsKey('Pending')) 'Failure lost its durable undo record.'
$script:failAfterPolicyWrite = $false
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq 'ON' -and $script:state.Count -eq 0) 'Partial apply was not recoverable.'

Reset-Fixture 'ON'
Invoke-CetAction 'Apply' '1'
$unfinished = $script:state.Pending
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:state.Pending -eq $unfinished) 'A second transaction overwrote the first undo record.'
$script:policy = 'NOTSET'
Invoke-CetAction 'Rollback' '0'
Assert ($script:policy -eq 'NOTSET') 'Rollback overwrote an administrator edit after apply.'

Reset-Fixture 'ON'
$script:unsupported = $true
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:writes -eq 0) 'An unsupported requested opt-out must fail before any write.'

Reset-Fixture 'ON'
$script:conflictingOptions = $true
Expect-Failure { Invoke-CetAction 'Apply' '1' }
Assert ($script:writes -eq 0 -and -not $script:state.ContainsKey('Pending')) 'A conflicting administrator CET option must fail before any write.'

Reset-Fixture 'ON'
$script:state.Baseline = '{"Schema":1,"ExecutablePath":"C:\\Other\\resodrive.exe","Original":"ON","Applied":"OFF"}'
Expect-Failure { Invoke-CetAction 'Remove' '0' }
Assert ($script:writes -eq 0 -and $script:loads -eq 0) 'State from another installation was accepted.'

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

    $systemDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    $nativePowerShell = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe'
    $nativeModule = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\Modules\ProcessMitigations\ProcessMitigations.psd1'
    Import-Module $nativeModule -ErrorAction Stop
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-cet-policy-' + [Guid]::NewGuid().ToString('N'))
    $fixtureExecutable = Join-Path $fixture 'resodrive.exe'
    $fixtureScript = Join-Path $fixture 'CetPolicy.ps1'
    $fixtureDiagnostic = Join-Path $fixture 'cet-policy-error.txt'
    $null = New-Item -ItemType Directory -Path $fixture
    Copy-Item -LiteralPath (Join-Path $systemDirectory 'cmd.exe') -Destination $fixtureExecutable
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
        # Audit ON requires shadow stacks ON, so it cannot be combined with the
        # requested OFF fixture. Preserve its original NOTSET value and CFG ON.
        $null = ProcessMitigations\Set-ProcessMitigation -Name $fixtureExecutable -Enable CFG -ErrorAction Stop
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
        Write-Host 'Native CET policy checks passed for a disposable path; CFG and CET audit settings were preserved.'
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
            $null = ProcessMitigations\Set-ProcessMitigation -Name $fixtureExecutable -Remove -Disable UserShadowStack, CFG -ErrorAction Stop
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
            [IO.Directory]::Delete($fixture)
        }
        catch { $cleanupFailures.Add($_.Exception.Message); Write-Warning ('Fixture file cleanup: ' + $_.Exception.Message) }
    }
    if ($null -ne $nativeFailure) { throw $nativeFailure }
    if ($cleanupFailures.Count -gt 0) { throw ('Native CET fixture cleanup failed: ' + ($cleanupFailures -join '; ')) }
}

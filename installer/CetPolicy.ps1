param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Apply', 'Rollback', 'Commit', 'Remove')]
    [string] $Action,
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,
    [ValidateSet('0', '1')]
    [string] $DisableCet = '0',
    [string] $DiagnosticPath
)

# Windows Installer supplies INSTALLFOLDER. Only this installed executable's
# UserShadowStack override is changed; all other mitigations stay administrator-owned.
$ErrorActionPreference = 'Stop'
$statePath = 'SOFTWARE\ResoDrive\CetCompatibility'
$registry = $null
$key = $null
$diagnosticDestination = $null

function Read-State([string] $name) {
    if ($null -eq $key) { return $null }
    $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($null -eq $value) { return $null }
    if ($key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::String -or
        $value -isnot [string] -or $value.Length -eq 0 -or
        $value.Length -gt $(if ($name -eq 'Baseline') { 4096 } else { 16384 })) {
        throw "The saved CET compatibility $name is invalid."
    }
    return $value
}

function Parse-State([string] $value, [bool] $pending) {
    if ([string]::IsNullOrEmpty($value)) { return $null }
    $state = ConvertFrom-Json -InputObject $value
    if ($state.Schema -ne 1 -or $state.ExecutablePath -isnot [string] -or
        -not $state.ExecutablePath.Equals($script:installedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The saved CET compatibility state belongs to another installation or is invalid.'
    }
    if ($pending) {
        if ($state.Original -notin @('ON', 'OFF', 'NOTSET') -or
            $state.Expected -notin @('ON', 'OFF', 'NOTSET') -or $state.ClearBaseline -isnot [bool] -or
            ($null -ne $state.Baseline -and ($state.Baseline -isnot [string] -or $state.Baseline.Length -gt 4096))) {
            throw 'The saved CET compatibility transaction is invalid.'
        }
        if ($null -ne $state.Baseline) { $null = Parse-State $state.Baseline $false }
    }
    elseif ($state.Original -notin @('ON', 'OFF', 'NOTSET') -or $state.Applied -ne 'OFF') {
        throw 'The saved CET compatibility baseline is invalid.'
    }
    return $state
}

function Write-State([string] $name, [string] $value) {
    if ($null -eq $script:key) { $script:key = $registry.CreateSubKey($statePath, $true) }
    $script:key.SetValue($name, $value, [Microsoft.Win32.RegistryValueKind]::String)
    $script:key.Flush()
}

function Delete-State([string] $name) {
    if ($null -ne $key) {
        $key.DeleteValue($name, $false)
        $key.Flush()
    }
}

function Load-MitigationModule {
    $module = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) `
        'WindowsPowerShell\v1.0\Modules\ProcessMitigations\ProcessMitigations.psd1'
    Import-Module -Name $module -ErrorAction Stop
    $command = Get-Command 'ProcessMitigations\Set-ProcessMitigation' -ErrorAction Stop
    $supported = @($command.Parameters['Disable'].Attributes | ForEach-Object {
        if ($_ -is [System.Management.Automation.ValidateSetAttribute]) { $_.ValidValues }
    })
    if ('UserShadowStack' -notin $supported) {
        throw 'This Windows version cannot configure the requested ResoDrive CET compatibility option.'
    }
}

function Read-Policy {
    $policies = @(ProcessMitigations\Get-ProcessMitigation -Name $script:installedPath -ErrorAction Stop)
    if ($policies.Count -eq 0) { return 'NOTSET' }
    if ($policies.Count -ne 1 -or $null -eq $policies[0].UserShadowStack -or
        $null -eq $policies[0].UserShadowStack.UserShadowStack) {
        throw 'Windows could not identify the installed executable CET override.'
    }
    $value = $policies[0].UserShadowStack.UserShadowStack.ToString()
    if ($value -notin @('ON', 'OFF', 'NOTSET')) {
        throw 'Windows could not read the installed executable CET override.'
    }
    return $value
}

function Write-Policy([string] $value) {
    switch ($value) {
        'ON' { $null = ProcessMitigations\Set-ProcessMitigation -Name $script:installedPath -Enable UserShadowStack -ErrorAction Stop }
        'OFF' { $null = ProcessMitigations\Set-ProcessMitigation -Name $script:installedPath -Disable UserShadowStack -ErrorAction Stop }
        'NOTSET' { $null = ProcessMitigations\Set-ProcessMitigation -Name $script:installedPath -Remove -Disable UserShadowStack -ErrorAction Stop }
        default { throw 'Invalid CET compatibility policy.' }
    }
    if ((Read-Policy) -ne $value) { throw 'Windows did not apply the requested ResoDrive CET override.' }
}

function Invoke-CetAction([string] $Action, [string] $DisableCet) {
    $baselineText = Read-State 'Baseline'
    $pendingText = Read-State 'Pending'
    $baseline = Parse-State $baselineText $false
    $pending = Parse-State $pendingText $true

    if ($Action -eq 'Commit') {
        if ($null -ne $pending -and $pending.ClearBaseline) { Delete-State 'Baseline' }
        Delete-State 'Pending'
    }
    elseif ($Action -eq 'Rollback') {
        if ($null -ne $pending) {
            Load-MitigationModule
            # An administrator may have edited the setting after our action.
            # Restore only a value that still matches what this transaction wrote.
            $current = Read-Policy
            if ($current -eq $pending.Expected -and $current -ne $pending.Original) {
                Write-Policy $pending.Original
            }
            if ($null -eq $pending.Baseline) { Delete-State 'Baseline' }
            else { Write-State 'Baseline' $pending.Baseline }
            Delete-State 'Pending'
        }
    }
    elseif ($null -eq $baseline -and ($Action -eq 'Remove' -or $DisableCet -eq '0')) {
        if ($null -ne $pending) { throw 'An earlier CET compatibility transaction is unfinished.' }
        # Default-on CET with no earlier opt-out needs no policy or state writes.
    }
    else {
        if ($null -ne $pending) { throw 'An earlier CET compatibility transaction is unfinished.' }
        Load-MitigationModule
        $current = Read-Policy
        $optOut = $Action -eq 'Apply' -and $DisableCet -eq '1'
        if ($optOut) {
            # Preserve our baseline across repairs/upgrades. If an administrator
            # changed our last OFF value, their new value becomes the baseline.
            $original = if ($null -ne $baseline -and $current -eq $baseline.Applied) { $baseline.Original } else { $current }
            $target = 'OFF'
        }
        else {
            $target = if ($current -eq $baseline.Applied) { $baseline.Original } else { $current }
        }
        $transaction = [ordered]@{
            Schema = 1; ExecutablePath = $script:installedPath; Original = $current
            Expected = $target; Baseline = $baselineText; ClearBaseline = -not $optOut
        }
        # The undo record is durable before changing Windows or our baseline.
        Write-State 'Pending' (ConvertTo-Json -InputObject $transaction -Compress)
        if ($current -ne $target) { Write-Policy $target }
        if ($optOut) {
            $owned = [ordered]@{
                Schema = 1; ExecutablePath = $script:installedPath; Original = $original; Applied = 'OFF'
            }
            Write-State 'Baseline' (ConvertTo-Json -InputObject $owned -Compress)
        }
        # Opting back in/removing retains the baseline until the transaction commits.
    }
}

try {
    # The native action owns this private script directory. Never turn a public
    # installer argument into an arbitrary administrative file-write destination.
    if (-not [string]::IsNullOrEmpty($DiagnosticPath)) {
        $expected = Join-Path $PSScriptRoot 'cet-policy-error.txt'
        if ($DiagnosticPath -notmatch '^[A-Za-z]:[\\/]' -or
            -not [IO.Path]::GetFullPath($DiagnosticPath).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The CET diagnostic destination must be the private script directory cet-policy-error.txt.'
        }
        try {
            $attributes = [IO.File]::GetAttributes($expected)
            if (($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) -ne 0) {
                throw 'The CET diagnostic destination must be an ordinary file.'
            }
        }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        $diagnosticDestination = $expected
    }
    # Ordinary absolute drive paths only. Do not accept UNC/device paths or change
    # a basename-wide policy that would also affect portable copies.
    if ($ExecutablePath.Length -gt 1024 -or $ExecutablePath -notmatch '^[A-Za-z]:[\\/]' -or $ExecutablePath.IndexOf([char]0) -ge 0) {
        throw 'An absolute installed ResoDrive executable path is required.'
    }
    $script:installedPath = [IO.Path]::GetFullPath($ExecutablePath)
    if (-not [IO.Path]::GetFileName($script:installedPath).Equals('resodrive.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The CET compatibility option applies only to the installed resodrive.exe.'
    }
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    $key = $registry.OpenSubKey($statePath, $true)
    Invoke-CetAction $Action $DisableCet
}
catch {
    $message = 'ResoDrive CET compatibility: ' + $_.Exception.Message
    if ($message.Length -gt 1024) { $message = $message.Substring(0, 1024) }
    [Console]::Error.WriteLine($message)
    if ($null -ne $diagnosticDestination) {
        try {
            # CreateNew also rejects a leaf redirect introduced after validation.
            $stream = New-Object IO.FileStream($diagnosticDestination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($message)
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
        catch { } # Reporting must preserve the original policy failure.
    }
    exit 1
}
finally {
    if ($null -ne $key) { $key.Dispose() }
    if ($null -ne $registry) { $registry.Dispose() }
}
exit 0

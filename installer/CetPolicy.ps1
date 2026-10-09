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

function Valid-PolicyValue($value) {
    return $value -is [string] -and $value -cmatch '^[0-9A-F]{16}$' -and
        ([Convert]::ToUInt64($value, 16) -band 3) -ne 3
}

function Parse-State([string] $value, [bool] $pending) {
    if ([string]::IsNullOrEmpty($value)) { return $null }
    $state = ConvertFrom-Json -InputObject $value
    if ($state.Schema -ne 2 -or $state.ExecutablePath -isnot [string] -or
        -not $state.ExecutablePath.Equals($script:installedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The saved CET compatibility state belongs to another installation or is invalid.'
    }
    if ($pending) {
        if (-not (Valid-PolicyValue $state.Original) -or
            -not (Valid-PolicyValue $state.Expected) -or $state.ClearBaseline -isnot [bool] -or
            $state.OriginalFilterExists -isnot [bool] -or $state.RemoveFilter -isnot [bool] -or
            ($null -ne $state.Baseline -and ($state.Baseline -isnot [string] -or $state.Baseline.Length -gt 4096))) {
            throw 'The saved CET compatibility transaction is invalid.'
        }
        if ($null -ne $state.Baseline) { $null = Parse-State $state.Baseline $false }
    }
    elseif (-not (Valid-PolicyValue $state.Original) -or -not (Valid-PolicyValue $state.Applied) -or
        ([Convert]::ToUInt64($state.Applied, 16) -band 3) -ne 2 -or $state.OriginalFilterExists -isnot [bool]) {
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

function Load-MitigationApi {
    if ('ResoDrive.CetImagePolicy' -as [type]) { return }
    # RtlSetImageMitigationPolicy is the Windows per-image API. Its image-policy
    # ABI is 3 UInt64 fields in Microsoft's shipped ProcessMitigations module;
    # it is different from the process-only DWORD policy in winnt.h.
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace ResoDrive {
    public static class CetImagePolicy {
        [StructLayout(LayoutKind.Sequential)]
        private struct Policy {
            public ulong UserShadowStack, SetContextIpValidation, BlockNonCetBinaries;
        }
        [DllImport("ntdll.dll", EntryPoint="RtlQueryImageMitigationPolicy", CharSet=CharSet.Unicode)]
        private static extern int Query(string path, int family, uint flags, out Policy value, uint size);
        [DllImport("ntdll.dll", EntryPoint="RtlSetImageMitigationPolicy", CharSet=CharSet.Unicode)]
        private static extern int Set(string path, int family, uint flags, ref Policy value, uint size);
        private static void Check(int status, string stage) {
            if (status != 0) throw new InvalidOperationException(stage + ": NTSTATUS 0x" + unchecked((uint)status).ToString("X8"));
        }
        private static Policy Read(string path, bool audit) {
            if (String.IsNullOrEmpty(path) || Marshal.SizeOf(typeof(Policy)) != 24)
                throw new InvalidOperationException("An exact executable path and supported CET image-policy ABI are required.");
            Policy value;
            int status = Query(path, 15, audit ? 8U : 0U, out value, 24);
            if (unchecked((uint)status) == 0xC0000034U) return default(Policy);
            Check(status, "Read executable CET policy");
            if ((value.UserShadowStack & 3UL) == 3UL)
                throw new InvalidOperationException("Windows returned an unsupported CET override state.");
            return value;
        }
        public static string ReadFirst(string path, bool forOptOut) {
            Policy value = Read(path, false);
            if (forOptOut && ((value.UserShadowStack & 8UL) != 0 ||
                (Read(path, true).UserShadowStack & 3UL) == 1UL ||
                (value.BlockNonCetBinaries & 3UL) == 1UL))
                throw new InvalidOperationException("Administrator CET audit, strict mode or non-CET binary blocking requires shadow stacks and cannot be preserved with the requested CET opt-out.");
            return value.UserShadowStack.ToString("X16");
        }
        public static string Off(string original) {
            return ((Convert.ToUInt64(original, 16) & ~3UL) | 2UL).ToString("X16");
        }
        public static void WriteFirst(string path, string target, string expected) {
            Policy value = Read(path, false);
            if (value.UserShadowStack != Convert.ToUInt64(expected, 16))
                throw new InvalidOperationException("The executable CET setting changed before the transaction.");
            // Preserve fresh sibling fields. Only our exact first field changes;
            // no other mitigation family or audit policy is written.
            Policy previous = value;
            Policy audit = Read(path, true);
            value.UserShadowStack = Convert.ToUInt64(target, 16);
            Check(Set(path, 15, 0, ref value, 24), "Write executable CET policy");
            Policy actual = Read(path, false);
            Policy actualAudit = Read(path, true);
            if (actual.UserShadowStack != value.UserShadowStack ||
                actual.SetContextIpValidation != previous.SetContextIpValidation ||
                actual.BlockNonCetBinaries != previous.BlockNonCetBinaries ||
                actualAudit.UserShadowStack != audit.UserShadowStack ||
                actualAudit.SetContextIpValidation != audit.SetContextIpValidation ||
                actualAudit.BlockNonCetBinaries != audit.BlockNonCetBinaries)
                throw new InvalidOperationException("Windows did not preserve the requested CET policy transaction.");
        }
    }
}
"@ -ErrorAction Stop
}

function Assert-NoBasenamePolicy([string] $path) {
    # Creating a fullpath IFEO filter changes basename-rule inheritance.
    # Reject that administrator configuration rather than copy/rewrite it.
    $basename = $script:registry.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\' + [IO.Path]::GetFileName($path))
    if ($null -ne $basename) {
        try {
            $names = $basename.GetValueNames()
            if (@($names | Where-Object { $_ -ne 'UseFilter' }).Count -gt 0) {
                throw 'An administrator has configured debug or mitigation policies for every copy of this executable. Resolve that basename-wide configuration before selecting CET compatibility; mitigation rules can be limited to the exact installed file in Windows Exploit protection.'
            }
        }
        finally { $basename.Dispose() }
    }
}

function Find-Filter {
    $root = $script:registry.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\resodrive.exe')
    if ($null -eq $root) { return $null }
    try {
        $matches = @()
        foreach ($name in $root.GetSubKeyNames()) {
            $filter = $root.OpenSubKey($name)
            try {
                $path = $filter.GetValue('FilterFullPath', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($path -is [string] -and $path.Equals($script:installedPath, [StringComparison]::OrdinalIgnoreCase)) { $matches += $name }
            }
            finally { $filter.Dispose() }
        }
        if ($matches.Count -gt 1) { throw 'Windows has duplicate IFEO filters for the installed executable.' }
        if ($matches.Count -eq 1) { return $matches[0] }
    }
    finally { $root.Dispose() }
}

function Remove-OwnedEmptyFilter {
    $name = Find-Filter
    if ($null -eq $name) { return }
    $root = $script:registry.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\resodrive.exe', $true)
    if ($null -eq $root) { return }
    try {
        $filter = $root.OpenSubKey($name)
        if ($null -eq $filter) { return }
        try {
            $path = $filter.GetValue('FilterFullPath', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($path -isnot [string] -or -not $path.Equals($script:installedPath, [StringComparison]::OrdinalIgnoreCase)) { return }
            if ($filter.SubKeyCount -ne 0) { return }
            foreach ($valueName in $filter.GetValueNames()) {
                if ($valueName -eq 'FilterFullPath') { continue }
                if ($valueName -notin @('MitigationOptions', 'MitigationOptionsMask', 'MitigationAuditOptions', 'MitigationAuditOptionsMask')) { return }
                $bytes = $filter.GetValue($valueName)
                if ($bytes -isnot [byte[]] -or @($bytes | Where-Object { $_ -ne 0 }).Count -gt 0) { return }
            }
            # The durable receipt proves this filter did not predate our action.
            # Delete only its now-empty metadata; preserve all administrator edits.
        }
        finally { $filter.Dispose() }
        $root.DeleteSubKey($name, $false)
    }
    finally { $root.Dispose() }
}

function Read-Policy([bool] $forOptOut = $false) {
    if ($forOptOut) { Assert-NoBasenamePolicy $script:installedPath }
    return [ResoDrive.CetImagePolicy]::ReadFirst($script:installedPath, $forOptOut)
}

function Off-Policy([string] $original) { return [ResoDrive.CetImagePolicy]::Off($original) }

function Write-Policy([string] $value, [string] $expected) {
    [ResoDrive.CetImagePolicy]::WriteFirst($script:installedPath, $value, $expected)
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
            Load-MitigationApi
            # An administrator may have edited the setting after our action.
            # Restore only a value that still matches what this transaction wrote.
            $current = Read-Policy
            if ($current -eq $pending.Expected -and $current -ne $pending.Original) {
                Write-Policy $pending.Original $current
            }
            if (-not $pending.OriginalFilterExists -and $pending.Original -eq '0000000000000000' -and
                (Read-Policy) -eq $pending.Original) { Remove-OwnedEmptyFilter }
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
        Load-MitigationApi
        $optOut = $Action -eq 'Apply' -and $DisableCet -eq '1'
        $current = Read-Policy $optOut
        $filterExists = $null -ne (Find-Filter)
        if ($optOut) {
            # Preserve our baseline across repairs/upgrades. If an administrator
            # changed our last OFF value, their new value becomes the baseline.
            $original = if ($null -ne $baseline -and $current -eq $baseline.Applied) { $baseline.Original } else { $current }
            $originalFilterExists = if ($null -ne $baseline -and $current -eq $baseline.Applied) { $baseline.OriginalFilterExists } else { $filterExists }
            $target = Off-Policy $current
        }
        else {
            $target = if ($current -eq $baseline.Applied) { $baseline.Original } else { $current }
        }
        $transaction = [ordered]@{
            Schema = 2; ExecutablePath = $script:installedPath; Original = $current
            Expected = $target; Baseline = $baselineText; ClearBaseline = -not $optOut
            OriginalFilterExists = $filterExists
            RemoveFilter = -not $optOut -and -not $baseline.OriginalFilterExists -and $target -eq '0000000000000000'
        }
        # The undo record is durable before changing Windows or our baseline.
        Write-State 'Pending' (ConvertTo-Json -InputObject $transaction -Compress)
        if ($current -ne $target) { Write-Policy $target $current }
        if ($transaction.RemoveFilter) { Remove-OwnedEmptyFilter }
        if ($optOut) {
            $owned = [ordered]@{
                Schema = 2; ExecutablePath = $script:installedPath; Original = $original; Applied = $target
                OriginalFilterExists = $originalFilterExists
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

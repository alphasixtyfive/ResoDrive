param(
    [Parameter(Mandatory)]
    [string] $AppPath,

    [ValidateRange(15, 180)]
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceApp = [IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path -LiteralPath $sourceApp -PathType Leaf)) {
    throw "Process smoke app was not found at '$sourceApp'."
}
if ([IO.Path]::GetExtension($sourceApp) -ne '.exe') {
    throw "Process smoke requires a built Windows executable, not '$sourceApp'."
}

$runId = [Guid]::NewGuid().ToString('N')
$executionRoot = Join-Path ([IO.Path]::GetTempPath()) ("resodrive-process-binaries-" + $runId)
$resolvedExecutionRoot = [IO.Path]::GetFullPath($executionRoot)
$resolvedApp = Join-Path $resolvedExecutionRoot ([IO.Path]::GetFileName($sourceApp))
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ("resodrive-process-smoke-" + $runId)
$resolvedDataRoot = [IO.Path]::GetFullPath($dataRoot)
$resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $resolvedDataRoot.StartsWith($resolvedTempRoot + 'resodrive-process-smoke-', [StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedExecutionRoot.StartsWith($resolvedTempRoot + 'resodrive-process-binaries-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use smoke-test directories outside '$resolvedTempRoot'."
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class ResoDriveSmokeJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private IntPtr handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref ExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public ResoDriveSmokeJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var information = new ExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
                handle,
                9,
                ref information,
                (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            handle = IntPtr.Zero;
            throw new Win32Exception(error);
        }
    }

    public void AddProcess(IntPtr processHandle)
    {
        if (handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(ResoDriveSmokeJob));
        if (!AssignProcessToJobObject(handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero)
            return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}
'@

$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$job = [ResoDriveSmokeJob]::new()
$logPath = Join-Path $resolvedDataRoot 'logs\resodrive-ui.log'

function Start-OwnedProcess {
    param([string[]] $Arguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedApp
    $startInfo.WorkingDirectory = Split-Path -Parent $resolvedApp
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['RDRIVE_DATA_DIR'] = $resolvedDataRoot
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start '$resolvedApp'."
    }
    $owned.Add($process)
    try {
        if (-not $process.HasExited) {
            $job.AddProcess($process.Handle)
        }
    }
    catch {
        if (-not $process.HasExited) {
            $process.Kill($true)
        }
        throw
    }
    return $process
}

function Wait-Until {
    param(
        [scriptblock] $Condition,
        [string] $Description,
        [int] $Seconds = $TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ((Test-Path -LiteralPath $logPath) -and
            (Get-Content -LiteralPath $logPath -Raw) -match 'event=(startup\.fatal|exception\.dispatcher)') {
            throw "The application reported a UI failure while waiting for $Description."
        }
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    $log = if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath -Raw
    } else {
        '<UI log not created>'
    }
    throw "Timed out waiting for $Description.`n$log"
}

function Wait-ExitCode {
    param(
        [Diagnostics.Process] $Process,
        [int] $Expected = 0,
        [string] $Description = 'process completion'
    )

    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
        throw "Timed out waiting for $Description (PID $($Process.Id))."
    }
    if ($Process.ExitCode -ne $Expected) {
        $log = if (Test-Path -LiteralPath $logPath) {
            Get-Content -LiteralPath $logPath -Raw
        } else {
            '<UI log not created>'
        }
        throw "$Description exited with $($Process.ExitCode), expected $Expected.`n$log"
    }
}

function Get-SmokeProcessCount {
    $count = 0
    foreach ($candidate in Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($resolvedApp)) -ErrorAction SilentlyContinue) {
        try {
            $candidatePath = $candidate.Path
            if (-not [string]::IsNullOrWhiteSpace($candidatePath) -and
                [IO.Path]::GetFullPath($candidatePath).Equals($resolvedApp, [StringComparison]::OrdinalIgnoreCase)) {
                $count++
            }
        }
        catch [System.ComponentModel.Win32Exception] {
        }
        catch [System.InvalidOperationException] {
        }
    }
    return $count
}

try {
    New-Item -ItemType Directory -Path $resolvedExecutionRoot -Force | Out-Null
    Copy-Item -Path (Join-Path (Split-Path -Parent $sourceApp) '*') -Destination $resolvedExecutionRoot -Recurse -Force
    if (-not (Test-Path -LiteralPath $resolvedApp -PathType Leaf)) {
        throw "The staged smoke executable was not created at '$resolvedApp'."
    }
    New-Item -ItemType Directory -Path $resolvedDataRoot -Force | Out-Null
    # A populated row exercises the actual WPF template and read-only bindings.
    # The disabled fixture never mounts a drive or contacts a remote server.
    [IO.File]::WriteAllText((Join-Path $resolvedDataRoot 'settings.json'),
        '{"schemaVersion":1,"revision":7,"application":{"minimizeToTray":true,"startWithWindows":false},"mounts":[{"id":"20c09145-8dd0-45dc-b0a8-4e13d27f5067","displayName":"Smoke test drive","remoteName":"smoke","connectionHost":"example.invalid","connectionType":"webdav","enabled":false,"autoMount":"never","target":{"kind":"drive","driveLetter":"Z"}}]}')

    # A cold-start race should elect exactly one UI primary. Background secondaries
    # exit without stealing focus or waiting for the primary window.
    $coldLaunches = @(
        Start-OwnedProcess @('--background')
        Start-OwnedProcess @('--background')
        Start-OwnedProcess @('--background')
        Start-OwnedProcess @('--background')
    )
    Wait-Until {
        @($coldLaunches | Where-Object { -not $_.HasExited }).Count -eq 1
    } 'one primary instance after simultaneous cold launches'
    $primary = @($coldLaunches | Where-Object { -not $_.HasExited })[0]
    foreach ($secondary in $coldLaunches | Where-Object { $_.Id -ne $primary.Id }) {
        Wait-ExitCode $secondary 0 'background secondary launch'
    }
    Wait-Until {
        (Test-Path -LiteralPath $logPath) -and
        (Get-Content -LiteralPath $logPath -Raw).Contains('event=startup.window_shown', [StringComparison]::Ordinal)
    } 'cold background startup'

    # A foreground secondary exits only after the hidden primary restores a visible,
    # startup-ready window and acknowledges that exact request.
    $show = Start-OwnedProcess @('--show')
    Wait-ExitCode $show 0 'acknowledged show request'
    $activationLog = Get-Content -LiteralPath $logPath -Raw
    if (-not $activationLog.Contains('event=activation.request_completed', [StringComparison]::Ordinal)) {
        throw 'The primary did not record completion of the show request.'
    }

    # MainWindow owns automatic host recovery. Stop the host through its supported
    # protocol and verify a replacement process appears within the bounded window.
    Wait-Until { (Get-SmokeProcessCount) -ge 2 } 'background host startup'
    $stopHost = Start-OwnedProcess @('--prepare-update')
    Wait-ExitCode $stopHost 0 'background host shutdown'
    Wait-Until { (Get-SmokeProcessCount) -eq 1 } 'background host termination'
    Wait-Until { (Get-SmokeProcessCount) -ge 2 } 'automatic background host recovery'

    # Terminate only processes owned by this harness, then prove a clean subsequent
    # launch can again reach the acknowledged-ready state.
    $primary.Kill($true)
    $primary.WaitForExit()
    Wait-Until { (Get-SmokeProcessCount) -eq 0 } 'owned process-tree shutdown'
    $startupCountBeforeRelaunch = if (Test-Path -LiteralPath $logPath) {
        ([regex]::Matches((Get-Content -LiteralPath $logPath -Raw), 'event=startup\.window_shown')).Count
    } else {
        0
    }
    $relaunched = Start-OwnedProcess @('--background')
    Wait-Until {
        -not $relaunched.HasExited -and
        (Test-Path -LiteralPath $logPath) -and
        ([regex]::Matches((Get-Content -LiteralPath $logPath -Raw), 'event=startup\.window_shown')).Count -gt $startupCountBeforeRelaunch
    } 'ready background relaunch process'
    $showAfterRelaunch = Start-OwnedProcess @('--show')
    Wait-ExitCode $showAfterRelaunch 0 'acknowledged show request after relaunch'

    Write-Host 'ResoDrive process smoke passed: populated drive-row rendering, cold race, tray/show acknowledgement, host recovery, and relaunch.'
}
finally {
    $job.Dispose()
    foreach ($process in $owned) {
        $process.Dispose()
    }

    # Catch a child that raced process startup before assignment to the job. The app
    # path is a unique CI publish output, so this cannot target an installed copy.
    foreach ($candidate in Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($resolvedApp)) -ErrorAction SilentlyContinue) {
        try {
            $candidatePath = $candidate.Path
            if (-not [string]::IsNullOrWhiteSpace($candidatePath) -and
                [IO.Path]::GetFullPath($candidatePath).Equals($resolvedApp, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $candidate.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch [System.ComponentModel.Win32Exception] {
        }
        catch [System.InvalidOperationException] {
        }
    }

    for ($attempt = 0; $attempt -lt 25 -and (Test-Path -LiteralPath $resolvedDataRoot); $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        }
        catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 200
        }
        catch [System.UnauthorizedAccessException] {
            Start-Sleep -Milliseconds 200
        }
    }
    for ($attempt = 0; $attempt -lt 25 -and (Test-Path -LiteralPath $resolvedExecutionRoot); $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedExecutionRoot -Recurse -Force
        }
        catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 200
        }
        catch [System.UnauthorizedAccessException] {
            Start-Sleep -Milliseconds 200
        }
    }
    if (Test-Path -LiteralPath $resolvedDataRoot) {
        throw "Smoke-test data cleanup did not complete for '$resolvedDataRoot'."
    }
    if (Test-Path -LiteralPath $resolvedExecutionRoot) {
        throw "Smoke-test binary cleanup did not complete for '$resolvedExecutionRoot'."
    }
}

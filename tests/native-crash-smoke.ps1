[CmdletBinding()]
param([string]$MonitorPath, [switch]$VerifyDialog, [switch]$VerifyWer, [switch]$VerifyClipboard)
$ErrorActionPreference = 'Stop'
if (($VerifyWer -or $VerifyClipboard) -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'Real WER crash and clipboard acceptance are permitted only on a disposable GitHub-hosted runner. Local smoke uses synthetic exit codes, never changes machine policy, and leaves the user clipboard untouched.'
}
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Read-FullMemoryDumpEvidence.ps1')
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('resodrive-native-crash-' + [Guid]::NewGuid().ToString('N'))
$binaryRoot = Join-Path $runRoot 'bin with spaces'
& (Join-Path $repo 'native/ResoDrive.CrashMonitor/build.ps1') -OutputDirectory $binaryRoot -IncludeTestFixture
if ($MonitorPath) { Copy-Item -LiteralPath ([IO.Path]::GetFullPath($MonitorPath)) -Destination (Join-Path $binaryRoot 'resodrive-launcher.exe') }
$launcher = Join-Path $binaryRoot 'resodrive-launcher.exe'
$fixture = Join-Path $binaryRoot 'resodrive.exe'
if (-not ('NativeCrashWindowProbe' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeCrashWindowProbe {
    public delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    public static bool HasVisibleWindow(int pid) {
        bool visible = false;
        EnumWindows((window, parameter) => { uint owner; GetWindowThreadProcessId(window, out owner); if (owner == pid && IsWindowVisible(window)) visible = true; return true; }, IntPtr.Zero);
        return visible;
    }
    public static bool HasOwnedWindow(int pid) {
        bool found = false;
        EnumWindows((window, parameter) => { uint owner; GetWindowThreadProcessId(window, out owner); if (owner == pid) found = true; return true; }, IntPtr.Zero);
        return found;
    }
    public static int[] VisibleBounds(int pid) {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        int[] bounds = null;
        EnumWindows((window, parameter) => { uint owner; GetWindowThreadProcessId(window, out owner); Rect rect; if (owner == pid && IsWindowVisible(window) && GetWindowRect(window, out rect)) bounds = new int[] { rect.Left, rect.Top, rect.Right, rect.Bottom }; return true; }, IntPtr.Zero);
        return bounds;
    }
    public static IntPtr VisibleHandle(int pid) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, parameter) => { uint owner; GetWindowThreadProcessId(window, out owner); if (owner == pid && IsWindowVisible(window)) result = window; return true; }, IntPtr.Zero);
        return result;
    }
    public static void CloseVisible(int pid) {
        EnumWindows((window, parameter) => { uint owner; GetWindowThreadProcessId(window, out owner); if (owner == pid && IsWindowVisible(window)) SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
    }
    public static bool ClickCopy(int pid) {
        IntPtr window = VisibleHandle(pid);
        if (window == IntPtr.Zero) return false;
        IntPtr result;
        return SendMessageTimeout(window, 0x0400 + 102, new IntPtr(1001), IntPtr.Zero, 3, 5000, out result) != IntPtr.Zero; // TDM_CLICK_BUTTON
    }
    public static string ClipboardText() {
        if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("Clipboard is unavailable.");
        try {
            IntPtr memory = GetClipboardData(13); // CF_UNICODETEXT
            IntPtr data = memory == IntPtr.Zero ? IntPtr.Zero : GlobalLock(memory);
            if (data == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(data); } finally { GlobalUnlock(memory); }
        } finally { CloseClipboard(); }
    }
    public static bool HoldClipboard() { return OpenClipboard(IntPtr.Zero); }
    public static void ReleaseClipboard() { CloseClipboard(); }
    public static bool SignalSessionEnd(int pid) {
        bool sent = false;
        EnumWindows((window, parameter) => { uint owner; GetWindowThreadProcessId(window, out owner); if (owner == pid) { SendMessage(window, 0x0011, IntPtr.Zero, IntPtr.Zero); SendMessage(window, 0x0016, new IntPtr(1), IntPtr.Zero); sent = true; } return true; }, IntPtr.Zero);
        return sent;
    }
}
'@
}
$results = [Collections.Generic.List[string]]::new()
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message }; $results.Add($message) }
$null = & (Join-Path $PSScriptRoot 'full-memory-dump-smoke.ps1')
Assert $true 'Full-memory dump parser accepts a valid synthetic layout and rejects 12 corrupt layouts without a real crash.'
function Start-TestProcess([string]$executable, [string[]]$arguments, [string]$root, [switch]$ShowDialogs) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.Environment['RDRIVE_DATA_DIR'] = $root
    if ($ShowDialogs) { $info.Environment.Remove('RDRIVE_CRASH_NO_DIALOG') | Out-Null }
    else { $info.Environment['RDRIVE_CRASH_NO_DIALOG'] = '1' }
    return [Diagnostics.Process]::Start($info)
}
function Wait-TestProcess($process) {
    if (-not $process.WaitForExit(15000)) { throw "Native smoke process $($process.Id) did not finish. Its disposable data is at $runRoot" }
    return $process.ExitCode
}
function Reports([string]$root) { return ,@(Get-ChildItem -LiteralPath ($root + '-diagnostics') -Filter 'incident-*.txt' -ErrorAction SilentlyContinue) }
function Wait-FixtureIdentity([string]$path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $identity = [IO.File]::ReadAllLines($path)
            if ($identity.Count -eq 2 -and $identity[0] -match '^\d+$' -and $identity[1] -match '^\d+$') { return $identity }
        } catch [IO.IOException] { }
        Start-Sleep -Milliseconds 25
    }
    throw "Fixture did not write its ready identity: $path"
}

$eventTests = Start-TestProcess (Join-Path $binaryRoot 'event-evidence-tests.exe') @() (Join-Path $runRoot 'event-test-data')
Assert ((Wait-TestProcess $eventTests) -eq 0) 'Windows event metadata fixtures accept exact identity, reject wrong PID/time/provider, and exclude malformed/private fields.'

$normalRoot = Join-Path $runRoot 'normal-data'
$normalIdentity = Join-Path $runRoot 'normal-identity.txt'
$normalTrigger = Join-Path $runRoot 'normal-trigger.txt'
$process = Start-TestProcess $launcher @('--identity', $normalIdentity, '--wait-trigger', $normalTrigger, '--exit', '0') $normalRoot
$null = Wait-FixtureIdentity $normalIdentity
$process.Refresh()
Assert (-not [NativeCrashWindowProbe]::HasVisibleWindow($process.Id)) 'Normal startup has no visible window or taskbar item.'
[IO.File]::WriteAllText($normalTrigger, 'exit')
Assert ((Wait-TestProcess $process) -eq 0) 'Normal process exit is propagated.'
Assert ((Reports $normalRoot).Count -eq 0) 'Normal exit writes no incident.'
Assert (-not (Test-Path -LiteralPath $normalRoot)) 'Native launcher does not create account data.'

$fatalRoot = Join-Path $runRoot 'fatal-data'
$process = Start-TestProcess $launcher @('--exit', '0x80131506') $fatalRoot
$code = Wait-TestProcess $process
Assert ($code -eq [BitConverter]::ToInt32([BitConverter]::GetBytes([Convert]::ToUInt32('80131506', 16)), 0)) 'Fatal unsigned Windows exit code is preserved.'
$reports = Reports $fatalRoot
Assert ($reports.Count -eq 1) 'Fatal exit creates one metadata report.'
$summary = Get-Content -LiteralPath $reports[0].FullName -Raw
Assert ($summary -match '0x80131506' -and $summary -match 'SHA-256 at observer startup: [0-9A-F]{64}' -and $summary -match 'Process creation FILETIME: [1-9]') 'Report contains failure code, on-disk executable hash and process identity.'
Assert (-not (Test-Path -LiteralPath $fatalRoot)) 'Crash reporting never recreates account data.'

$displayedRoot = Join-Path $runRoot 'already-displayed-data'
$process = Start-TestProcess $launcher @('--sleep', '300', '--exit', '0xE0524447') $displayedRoot -ShowDialogs
$null = Wait-TestProcess $process
$reports = Reports $displayedRoot
Assert ($reports.Count -eq 1 -and (Get-Content -LiteralPath $reports[0].FullName -Raw) -match 'Failure already displayed by application: yes') 'Managed already-displayed failures retain an incident without a second dialog.'

$activationRoot = Join-Path $runRoot 'activation-timeout-data'
$process = Start-TestProcess $launcher @('--exit', '0xE0524448') $activationRoot
$null = Wait-TestProcess $process
$reports = Reports $activationRoot
Assert ($reports.Count -eq 1 -and (Get-Content -LiteralPath $reports[0].FullName -Raw) -match 'Event: activation-timeout') 'Activation timeout is recorded distinctly from an application crash.'

if ($VerifyDialog) {
    $dialogRoot = Join-Path $runRoot 'visible-dialog-data'
    $process = Start-TestProcess $launcher @('--exit', '0x80131506') $dialogRoot -ShowDialogs
    $bounds = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $bounds -and [DateTime]::UtcNow -lt $deadline) {
        $bounds = [NativeCrashWindowProbe]::VisibleBounds($process.Id)
        if (-not $bounds) { Start-Sleep -Milliseconds 100 }
    }
    try {
        Assert ($null -ne $bounds) 'A fatal pre-managed failure displays a native Windows error dialog.'
        $parallelRoot = Join-Path $runRoot 'parallel-dialog-data'
        $parallel = Start-TestProcess $launcher @('--exit', '1') $parallelRoot -ShowDialogs
        try {
            Assert ($parallel.WaitForExit(5000) -and $parallel.ExitCode -eq 1) 'Concurrent same-role failures show one dialog without queuing another modal window.'
            Assert ((Reports $parallelRoot).Count -eq 1) 'Concurrent failures still retain their own incident evidence.'
        } finally {
            if (-not $parallel.HasExited) { [NativeCrashWindowProbe]::CloseVisible($parallel.Id) }
        }
        Add-Type -AssemblyName System.Drawing
        $bitmap = [Drawing.Bitmap]::new($bounds[2] - $bounds[0], $bounds[3] - $bounds[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            try {
                Assert ([NativeCrashWindowProbe]::PrintWindow([NativeCrashWindowProbe]::VisibleHandle($process.Id), $deviceContext, 2)) 'The native crash dialog renders for visual inspection.'
            } finally { $graphics.ReleaseHdc($deviceContext) }
            $bitmap.Save((Join-Path $runRoot 'native-error-dialog.png'), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    } finally { [NativeCrashWindowProbe]::CloseVisible($process.Id) }
    $null = Wait-TestProcess $process

    $releasedRoot = Join-Path $runRoot 'released-dialog-data'
    $process = Start-TestProcess $launcher @('--exit', '1') $releasedRoot -ShowDialogs
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not [NativeCrashWindowProbe]::HasVisibleWindow($process.Id) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
    try { Assert ([NativeCrashWindowProbe]::HasVisibleWindow($process.Id)) 'Closing a crash dialog releases the gate for a subsequent independent failure.' }
    finally { [NativeCrashWindowProbe]::CloseVisible($process.Id) }
    $null = Wait-TestProcess $process
}

if ($VerifyClipboard) {
    $copyRoot = Join-Path $runRoot 'clipboard-data'
    $process = Start-TestProcess $launcher @('--exit', '0x80131506') $copyRoot -ShowDialogs
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not [NativeCrashWindowProbe]::HasVisibleWindow($process.Id) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
    try {
        Assert ([NativeCrashWindowProbe]::HoldClipboard()) 'Disposable hosted clipboard is locked for the copy-failure test.'
        try {
            Assert ([NativeCrashWindowProbe]::ClickCopy($process.Id) -and [NativeCrashWindowProbe]::HasVisibleWindow($process.Id)) 'A busy clipboard keeps the original error dialog usable without another modal dialog.'
        } finally { [NativeCrashWindowProbe]::ReleaseClipboard() }
        Assert ([NativeCrashWindowProbe]::ClickCopy($process.Id)) 'Copy details is invoked through the real native TaskDialog button.'
        $copied = [NativeCrashWindowProbe]::ClipboardText()
        $reports = Reports $copyRoot
        $summary = [IO.File]::ReadAllText($reports[0].FullName)
        Assert ($copied -eq $summary -and $copied -match '0x80131506') 'Copy details places the entire saved Unicode incident summary on the clipboard.'
        Assert ([NativeCrashWindowProbe]::HasVisibleWindow($process.Id)) 'Copy details keeps the error dialog open for further actions.'
    } finally { [NativeCrashWindowProbe]::CloseVisible($process.Id) }
    $null = Wait-TestProcess $process
}

$quoteRoot = Join-Path $runRoot 'quote-data'
$argumentFile = Join-Path $runRoot 'arguments.txt'
$sent = @('space value', 'quote"value', '', 'trailing\', 'unicode-船', 'password-canary-DO-NOT-LOG')
$process = Start-TestProcess $launcher (@('--record-args', $argumentFile) + $sent) $quoteRoot
Assert ((Wait-TestProcess $process) -eq 0) 'Quoted arguments exit normally.'
$received = [IO.File]::ReadAllText($argumentFile, [Text.Encoding]::Unicode).Split("`n")
Assert (($received.Count -eq $sent.Count + 1) -and (@(Compare-Object $sent $received[0..($sent.Count - 1)] -SyncWindow 0).Count -eq 0)) 'Argument forwarding preserves spaces, embedded quotes, empty values, Unicode and trailing backslashes.'
Assert ((Reports $quoteRoot).Count -eq 0) 'Forwarded command-line arguments are never logged.'

$observerRoot = Join-Path $runRoot 'observer-data'
$observerIdentity = Join-Path $runRoot 'observer-identity.txt'
$observerTrigger = Join-Path $runRoot 'observer-trigger.txt'
$child = Start-TestProcess $fixture @('--identity', $observerIdentity, '--wait-trigger', $observerTrigger, '--exit', '0xC0000005') $observerRoot
$identity = Wait-FixtureIdentity $observerIdentity
$created = $identity[1]
$observer = Start-TestProcess $launcher @('--observe', $child.Id.ToString(), $created, '--role', 'host', '--no-dialog') $observerRoot
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while (-not [NativeCrashWindowProbe]::HasOwnedWindow($observer.Id) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
Assert ([NativeCrashWindowProbe]::HasOwnedWindow($observer.Id)) 'Observer has verified and attached before the controlled child exits.'
[IO.File]::WriteAllText($observerTrigger, 'exit')
$null = Wait-TestProcess $child
$null = Wait-TestProcess $observer
$reports = Reports $observerRoot
Assert ($reports.Count -eq 1 -and (Get-Content -LiteralPath $reports[0].FullName -Raw) -match 'Role: host') 'Observer records the verified host process failure.'

$invalidRoot = Join-Path $runRoot 'invalid-observer-data'
$child = Start-TestProcess $fixture @('--sleep', '1000', '--exit', '0') $invalidRoot
$observer = Start-TestProcess $launcher @('--observe', $child.Id.ToString(), '1', '--no-dialog') $invalidRoot
Assert ((Wait-TestProcess $observer) -eq 2) 'Observer rejects a mismatched process creation time.'
$null = Wait-TestProcess $child
Assert ((Reports $invalidRoot).Count -eq 0) 'Rejected process identity creates no false incident.'

$foreignRoot = Join-Path $runRoot 'foreign-observer-data'
$foreignBinary = Join-Path $runRoot 'other-installation\resodrive.exe'
[IO.Directory]::CreateDirectory((Split-Path $foreignBinary -Parent)) | Out-Null
Copy-Item -LiteralPath $fixture -Destination $foreignBinary
$foreignIdentity = Join-Path $runRoot 'foreign-identity.txt'
$foreignTrigger = Join-Path $runRoot 'foreign-trigger.txt'
$child = Start-TestProcess $foreignBinary @('--identity', $foreignIdentity, '--wait-trigger', $foreignTrigger, '--exit', '0') $foreignRoot
$identity = Wait-FixtureIdentity $foreignIdentity
$observer = Start-TestProcess $launcher @('--observe', $child.Id.ToString(), $identity[1], '--no-dialog') $foreignRoot
Assert ((Wait-TestProcess $observer) -eq 2) 'Observer refuses another physical installation despite identical executable bytes.'
[IO.File]::WriteAllText($foreignTrigger, 'exit')
$null = Wait-TestProcess $child
Assert ((Reports $foreignRoot).Count -eq 0) 'Foreign installations create no incident in this installation diagnostics.'

$utilityRoot = Join-Path $runRoot 'utility-data'
$process = Start-TestProcess $launcher @('--prepare-update', '--exit', '1') $utilityRoot
Assert ((Wait-TestProcess $process) -eq 1 -and (Reports $utilityRoot).Count -eq 0) 'Expected utility failure is propagated without a crash dialog/report.'

$unwritableRoot = Join-Path $runRoot 'blocked-data'
[IO.File]::WriteAllText($unwritableRoot + '-diagnostics', 'existing-file-canary')
$process = Start-TestProcess $launcher @('--exit', '1') $unwritableRoot
Assert ((Wait-TestProcess $process) -eq 1) 'Unwritable diagnostics do not change the application exit code.'
Assert ([IO.File]::ReadAllText($unwritableRoot + '-diagnostics') -eq 'existing-file-canary') 'A file at the diagnostics path is preserved.'

$remoteRoot = '\\192.0.2.1\offline-share\resodrive-native-smoke'
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = Start-TestProcess $launcher @('--exit', '1') $remoteRoot
Assert ($process.WaitForExit(5000) -and $process.ExitCode -eq 1) 'Offline UNC diagnostics are refused promptly without network file I/O.'
$timer.Stop()
Assert ($timer.ElapsedMilliseconds -lt 5000) 'A disconnected diagnostics share cannot delay reporting for a network timeout.'

$junctionRoot = Join-Path $runRoot 'junction-data'
$junctionTarget = Join-Path $runRoot 'junction-target'
[IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
New-Item -ItemType Junction -Path ($junctionRoot + '-diagnostics') -Target $junctionTarget | Out-Null
$process = Start-TestProcess $launcher @('--exit', '1') $junctionRoot
Assert ((Wait-TestProcess $process) -eq 1 -and @(Get-ChildItem -LiteralPath $junctionTarget).Count -eq 0) 'Diagnostic path junctions are refused without writing through them.'

$sessionRoot = Join-Path $runRoot 'session-end-data'
$sessionIdentity = Join-Path $runRoot 'session-identity.txt'
$sessionTrigger = Join-Path $runRoot 'session-trigger.txt'
$process = Start-TestProcess $launcher @('--identity', $sessionIdentity, '--wait-trigger', $sessionTrigger, '--exit', '1') $sessionRoot
$null = Wait-FixtureIdentity $sessionIdentity
$sessionSignaled = $false
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while (-not $sessionSignaled -and [DateTime]::UtcNow -lt $deadline) {
    $sessionSignaled = [NativeCrashWindowProbe]::SignalSessionEnd($process.Id)
    if (-not $sessionSignaled) { Start-Sleep -Milliseconds 25 }
}
Assert $sessionSignaled 'Synthetic session-end notification reaches the hidden observer window.'
[IO.File]::WriteAllText($sessionTrigger, 'exit')
Assert ((Wait-TestProcess $process) -eq 1 -and (Reports $sessionRoot).Count -eq 0) 'Session shutdown suppresses crash dialogs and false incident reports.'

$retentionRoot = Join-Path $runRoot 'retention-data'
for ($index = 0; $index -lt 35; $index++) {
    $process = Start-TestProcess $launcher @('--exit', '1') $retentionRoot
    $null = Wait-TestProcess $process
}
Assert ((Reports $retentionRoot).Count -eq 32) 'Only the newest 32 native incident summaries are retained.'

if ($VerifyWer) {
    $policy = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\resodrive.exe'
    $dumpFolder = [Environment]::ExpandEnvironmentVariables($policy.DumpFolder)
    $expectedDumpFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'rdrive-diagnostics\dumps'
    Assert ([IO.Path]::GetFullPath($dumpFolder).Equals([IO.Path]::GetFullPath($expectedDumpFolder), [StringComparison]::OrdinalIgnoreCase) -and $policy.DumpType -eq 2 -and $policy.DumpCount -eq 3) 'Installed candidate configures the expected isolated hosted-user full-dump policy.'
    $werRoot = Join-Path $runRoot 'wer-acceptance-data'
    $identityPath = Join-Path $runRoot 'wer-fixture-identity.txt'
    $triggerPath = Join-Path $runRoot 'wer-fatal-trigger.txt'
    $process = Start-TestProcess $launcher @('--identity', $identityPath, '--wait-trigger', $triggerPath, '--fail-fast') $werRoot
    # The child writes its actual kernel creation identity before triggering failure.
    $identity = Wait-FixtureIdentity $identityPath
    Assert ($identity.Count -eq 2) 'Real WER fixture child writes a readiness identity before triggering failure.'
    $childPid = [int]$identity[0]
    $childCreation = [long]$identity[1]
    [IO.File]::WriteAllText($triggerPath, 'raise-fail-fast')
    if (-not $process.WaitForExit(60000)) { throw 'The fatal WER acceptance process did not finish within 60 seconds.' }
    Assert ($process.ExitCode -ne 0) 'Real RaiseFailFastException terminates with an abnormal exit.'
    $dumpPath = Join-Path $dumpFolder "resodrive.exe.$childPid.dmp"
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $dumpPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $dump = Get-Item -LiteralPath $dumpPath
    Assert ($dump.Length -gt 0 -and $dump.CreationTimeUtc.ToFileTimeUtc() -ge $childCreation -and $dump.LastWriteTimeUtc.ToFileTimeUtc() -ge $childCreation) 'WER writes a nonempty dump for the exact real-crash fixture PID and creation time.'
    $dumpEvidence = Read-FullMemoryDumpEvidence $dumpPath
    Assert ($dumpEvidence.threadCount -gt 0 -and $dumpEvidence.moduleCount -gt 0 -and $dumpEvidence.memoryBytes -gt 0) 'Native WER evidence declares full memory and contains valid thread/module/exception/context and Memory64 ranges.'
    $werReports = Reports $werRoot
    Assert ($werReports.Count -eq 1) 'Real native failure creates exactly one incident report.'
    $werSummary = Get-Content -LiteralPath $werReports[0].FullName -Raw
    Assert ($werSummary -match 'unexpected-exit') 'Native observer also records the actual fatal exception.'
    Assert ($werSummary -match 'MSI-owned default WER folder exists before application execution') 'Native preflight prepares the installed dump folder before the real crash without test-side directory creation.'

    $managedBinaryRoot = Join-Path $runRoot 'managed-clr-fixture'
    Push-Location $repo
    try {
        & dotnet publish (Join-Path $PSScriptRoot 'ResoDrive.CrashFixture/ResoDrive.CrashFixture.csproj') -c Release -r win-x64 --self-contained false -o $managedBinaryRoot
        if ($LASTEXITCODE -ne 0) { throw 'Pinned .NET 10 managed crash fixture build failed.' }
    } finally { Pop-Location }
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $managedBinaryRoot 'resodrive-launcher.exe')
    $managedRoot = Join-Path $runRoot 'managed-wer-data'
    $managedIdentity = Join-Path $runRoot 'managed-wer-identity.txt'
    $managedTrigger = Join-Path $runRoot 'managed-wer-trigger.txt'
    $process = Start-TestProcess (Join-Path $managedBinaryRoot 'resodrive-launcher.exe') @('--identity', $managedIdentity, '--wait-trigger', $managedTrigger) $managedRoot
    $identity = Wait-FixtureIdentity $managedIdentity
    $childPid = [int]$identity[0]; $childCreation = [long]$identity[1]
    [IO.File]::WriteAllText($managedTrigger, 'managed-fail-fast')
    if (-not $process.WaitForExit(60000)) { throw 'Managed CLR FailFast fixture did not finish within 60 seconds.' }
    Assert ($process.ExitCode -ne 0) '.NET 10 Environment.FailFast produces a real abnormal managed-process exit.'
    $managedDumpPath = Join-Path $dumpFolder "resodrive.exe.$childPid.dmp"
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $managedDumpPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $managedDump = Get-Item -LiteralPath $managedDumpPath
    Assert ($managedDump.CreationTimeUtc.ToFileTimeUtc() -ge $childCreation -and $managedDump.LastWriteTimeUtc.ToFileTimeUtc() -ge $childCreation) 'Managed WER dump matches the CLR fixture PID and creation identity.'
    $managedEvidence = Read-FullMemoryDumpEvidence $managedDumpPath -RequiredModule coreclr.dll
    Assert ($managedEvidence.requiredModuleFound -and $managedEvidence.memoryBytes -gt 0) 'Managed WER dump contains full memory, exception CPU context and the loaded CoreCLR module.'
    $managedReports = Reports $managedRoot
    Assert ($managedReports.Count -eq 1) 'The packaged native launcher also records the CLR-specific fatal failure.'
}

$missingRoot = Join-Path $runRoot 'missing-data'
Move-Item -LiteralPath $fixture -Destination (Join-Path $binaryRoot 'fixture-renamed.exe')
$process = Start-TestProcess $launcher @() $missingRoot
Assert ((Wait-TestProcess $process) -eq 2) 'Missing application reports Windows file-not-found code.'
$reports = Reports $missingRoot
Assert ($reports.Count -eq 1 -and (Get-Content -LiteralPath $reports[0].FullName -Raw) -match 'Event: launch-failed') 'Native launcher reports failures before managed code can run.'

[PSCustomObject]@{ status = 'PASS'; runRoot = $runRoot; checks = $results } | ConvertTo-Json -Depth 4
# Preserve disposable reports as inspectable evidence; no production process or data is touched.

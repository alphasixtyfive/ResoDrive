# Native launch and crash observation

`resodrive-launcher.exe` is an x64 Windows GUI executable with a statically linked
C runtime. It requires neither .NET nor WPF. On normal startup it opens no visible
window, taskbar item or console. The adjacent `resodrive.exe` keeps its existing
identity, installer path and single-instance behavior.

Build on Windows with Visual Studio's x64 C++ tools and the Windows SDK:

```powershell
./native/ResoDrive.CrashMonitor/build.ps1 -OutputDirectory ./artifacts/native
./tests/native-crash-smoke.ps1
```

The build uses MSVC warnings as errors, static CRT, ASLR, DEP and Control Flow Guard.
The matching `resodrive-launcher.pdb` belongs in separate release symbols, not the
installer. No compiler or package is downloaded. `-IncludeTestFixture` produces a
disposable `resodrive.exe` test fixture and must never be used for packaging.

## Contracts

- Normal launch forwards the original argument tail unchanged, names the adjacent
  executable explicitly, inherits the caller's token, and does not inherit handles.
- The child receives `RDRIVE_CRASH_SUPERVISED=1`. Managed Main must immediately read
  and clear it: suppress only that invocation's observer so later host/helper
  children can establish their own observer. The launcher itself observes a
  directly launched `--host` invocation as the host role.
- Direct managed launches can start
  `resodrive-launcher.exe --observe PID CREATION_FILETIME --role ui|host`.
  Both numbers are unsigned decimal, and creation is the actual process's Windows
  UTC FILETIME. Observation requires a matching creation time and physical identity
  of the adjacent executable; mismatches return 2 without recording an incident.
- The monitor propagates the actual child DWORD exit code. Zero exits produce no
  report. Installer/update/wipe utilities bypass reporting, because their nonzero
  exit codes are controlled results rather than crash evidence. Utility entry
  points should continue launching the raw executable directly.
- `0xE0524447` is the reserved managed failure-already-displayed exit code. Managed
  code may return it only after its error dialog successfully returns. It remains
  a nonzero failure, is recorded normally, and suppresses a duplicate native dialog.
  Failures before/while displaying a managed dialog must return a different code.
- `0xE0524448` is the reserved activation-timeout result. It records an
  `activation-timeout` incident and says the existing window did not respond;
  it does not claim that the primary application stopped.
- `0xE0524449` is the reserved verified installer UI-termination result, shared
  with `InstallerProcessInspection.ExpectedInstallerTerminationExitCode`.
  After the existing host/account/upload/process-identity checks authorize a UI
  stop, the installer uses this code on its already-verified process handle.
  UI observers propagate it without an incident or dialog. Hosts receiving it,
  and generic `0xFFFFFFFF` termination, remain reportable failures. The code
  grants no permission to stop a process and changes none of those checks.
- `RDRIVE_CRASH_NO_DIALOG=1`, or observer-only `--no-dialog`, suppresses dialogs for
  process tests but retains reporting. Never set it globally on production machines.
- A hidden top-level window receives Windows shutdown notifications. It is never
  shown and suppresses reporting during session shutdown. No process, child,
  background host or rclone is terminated or restarted by this component.

## Evidence and privacy

Reports live at `<resolved ApplicationPaths.Root>-diagnostics`, including for
relative/custom `RDRIVE_DATA_DIR` (relative roots resolve beside the application).
The default is `%LOCALAPPDATA%\rdrive-diagnostics`. The monitor never recreates
the account data directory and never reads credentials, configuration, logs or
cache. Its metadata reports contain timestamps, incident reference, process/role,
exit code, OS, executable path, file version and SHA-256. Command-line arguments
are deliberately excluded. Only the newest 32 summaries are retained; unrelated
files and dump retention are outside this component's ownership. Paths containing
reparse points are refused. Recording errors still leave Copy details available.
Remote UNC/mapped-drive destinations are refused so disconnected shares do not
hold up the crash dialog. The report remains available through Copy details even
when it could not be saved; no network or account-data fallback is written.

Supplementary Windows event evidence uses only the operating system's `EvtQuery`
and typed rendering APIs. It considers Application events 1000, 1001, 1023 and
1026 after process creation, requires an exact process identity, and saves only
provider/event/record/time, module/version, exception code/offset, report ID and
named runtime version. Application Error 1000 must match both PID and process
creation FILETIME. Events containing only PID 0, WerFault's PID or matching times
are excluded. Raw XML, event message bodies, stacks, attached-file lists and
unrelated application events are never exported. At most four correlated records
are saved; query failure or delayed logging is reported as unavailable. Module
names are evidence, not a diagnosed cause. Fields are restricted to module leaf
identifiers, numeric versions, hexadecimal codes/offsets and report GUIDs.
The original process handle stays open through collection to prevent PID reuse.

Windows Error Reporting owns dump collection. The monitor reads global/per-app
64-bit LocalDumps policy and checks a nonempty `resodrive.exe.PID.dmp` whose creation
and last-write times are not before the observed process creation. The report
labels it as a candidate file whose contents have not been validated. It neither
captures nor opens dumps and honestly reports missing/unverified evidence. WER
may finish later, be disabled by machine policy, fail on storage/permission
conditions, or not participate in a particular termination. A full dump contains
process memory and must be reviewed as sensitive, independently of this metadata.

Before execution it prepares the standard `%LOCALAPPDATA%\rdrive-diagnostics\dumps`
directory only when the MSI's `LocalDumpsOwned=1` marker exists and the per-app
policy resolves to that exact directory. It changes no registry values or ACLs
and never prepares administrator-customized folders. This default per-user dump
folder remains independent of custom `RDRIVE_DATA_DIR`; metadata honors that
custom root. Preflight failure is included in the report and does not prevent
launch. The hosted WER acceptance test does not manually create the dump directory
and therefore verifies native first-crash preparation.

On unexpected failure, the Windows task dialog offers Copy details, Open
diagnostics folder, expandable Technical details and Close. Common-controls
failure falls back to a plain native message box. The dialog never asserts a root
cause or that background uploads have stopped. There is no telemetry or restart
loop.
Concurrent errors retain separate reports while a per-user, per-installation,
per-role mutex allows one visible dialog. There is no popup queue. Copy failure
or folder-open failure updates the same native dialog rather than stacking a
second modal window.

Local smoke tests use synthetic process exit codes; they prove observer behavior,
not actual WER capture or .NET runtime diagnosis. `-VerifyDialog` additionally
renders the real native error window for inspection. `-VerifyWer` is allowed only
on disposable GitHub-hosted runners after the candidate MSI installs its per-app
policy. Readiness/trigger handshakes exercise both native `RaiseFailFastException`
and a separate .NET 10 `Environment.FailFast` fixture through the packaged
launcher. Each dump must match the exact process identity and timestamps, declare
full memory, and contain bounded thread, module, exception, CPU context and
Memory64 ranges. The CLR case additionally requires the loaded `coreclr.dll`
module. Synthetic valid/corrupt dump layouts test this acceptance parser locally;
production code never parses process-memory payloads.
The test neither edits machine policy nor crashes anything on the developer PC.
On hosted WER failure, sanitized JSON records the actual fixture error-mode/WER
flags, debugger presence, numeric Windows policy settings, reporting-service
status and correlated event/file metadata. It excludes debugger commands, raw
event messages and dump contents. Microsoft documents that
[automatic debugging](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)
and the inherited `SEM_NOGPFAULTERRORBOX`
[error mode](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-seterrormode)
can prevent WER collection; the test records evidence before attributing a cause.
The launcher clears only that inherited WER-suppression flag at entry and enables
`SEM_FAILCRITICALERRORS`, preserving every other error-mode bit. This changes its
own process and subsequent child inheritance, never machine policy or its parent.
Incident summaries label inherited/normalized observer modes and read-only
automatic-debugging/reporting switches; an attached raw process's error mode is
not inspected or changed. Fixture metadata also records immediate-job limit flags.
`-VerifyClipboard` is also restricted to a disposable hosted runner: it invokes
the real Copy details button and compares the complete Unicode clipboard value
with the saved report. Local visual tests leave the user's clipboard untouched.

The native launch path observes the process before managed code starts. Direct
raw executable launches can only attach their observer after managed Main begins;
a process dying before that point must rely on WER. No design can report power
loss, an OS failure or simultaneous loss of application and observer. Exit zero
alone is treated as orderly; it is not proof of completing every operation.

Microsoft references: [CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw),
[TaskDialogIndirect](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-taskdialogindirect),
[EvtRender](https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtrender),
[process-object lifetime and PID reuse](https://devblogs.microsoft.com/oldnewthing/20110107-00/?p=11803),
[WER LocalDumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps).
Test layouts follow Microsoft's [MINIDUMP_HEADER](https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_header)
and [MINIDUMP_MEMORY64_LIST](https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_memory64_list);
the CLR fixture uses [Environment.FailFast](https://learn.microsoft.com/en-us/dotnet/api/system.environment.failfast?view=net-10.0).

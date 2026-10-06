# Crash diagnostics

Open ResoDrive from its Start menu shortcut or, for the portable package, run
`resodrive-launcher.exe` beside `resodrive.exe`. The native launcher starts the
same application without elevation. It has no splash screen or normal startup
window. Its separate process can record a launch failure even when .NET fails
before the first managed instruction. Directly running `resodrive.exe` bypasses
this outer observation, but keeps the application's managed diagnostics and any
Windows Error Reporting policy.

The launcher clears an inherited Windows error-mode flag that would disable WER,
while retaining the other flags and protecting against critical-error prompts.
This process-local normalization changes no machine policy. Summaries report the
observer's inherited and normalized modes and read-only automatic-debugging/WER
settings; they do not claim to inspect an already-running raw process's mode.

The launcher is a local observer, not a debugger or an uploader. It must not
terminate the managed host, change upload protection, edit account data, or
reinterpret a normal secondary launch as a crashed primary process. Internal
maintenance commands keep using `resodrive.exe`, including the MSI's embedded
preparation helper and the copied update-completion helper. Windows installer
ownership, the executable identity, and the account-data directory remain intact.
Fresh installs use `%ProgramFiles%\ResoDrive`;
legacy clients retain their registered binary folder until a compatibility-aware
updater requests migration. Account data remains in `rdrive`. See
[installation-directory migration](INSTALL-DIRECTORY-MIGRATION.md).

## Local evidence

Diagnostic metadata is stored next to the active account-data directory, in
`<data-root>-diagnostics`; the default is `%LOCALAPPDATA%\rdrive-diagnostics`.
With `RDRIVE_DATA_DIR=D:\test\data`, metadata belongs in
`D:\test\data-diagnostics`. Keeping it outside account storage prevents a late
crash observer from recreating data removed by a completed remote wipe. Metadata
and memory dumps are local support evidence, retained separately from account
cleanup. A remote wipe does not assert that Windows crash evidence has been
erased.

Native summaries and managed logs refuse network, device and reparse destinations
before guarded filesystem access. An unavailable destination is reported honestly;
the native dialog can still copy the available summary.

Memory dumps can contain passwords, tokens, file content, and personal data.
They are optional sensitive attachments, not automatically included or sent.
Review the files before sharing them through an agreed support channel. Deleting
diagnostic evidence must not remove account settings, queued-upload cache, or
managed local copies.

## Windows Error Reporting dumps

The MSI configures the 64-bit, per-executable Windows Error Reporting (WER)
policy under:

```text
HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\resodrive.exe
```

Its defaults are `DumpType=2` (full user-mode dump), `DumpCount=3`, and
`DumpFolder=%LOCALAPPDATA%\rdrive-diagnostics\dumps` as `REG_EXPAND_SZ`. Windows
expands that path for the crashing user. The MSI escapes the literal percent
signs using its Formatted-string syntax; it must never substitute the installing
administrator's local directory. Only the executable-specific policy is written;
global WER settings are left alone.

If any supported per-executable dump setting already exists, the MSI leaves that
administrator-configured policy unchanged and does not claim its ownership. A
separate marker identifies a policy created by ResoDrive so later major upgrades
can reinstall it. The component uses `NeverOverwrite` to preserve an existing
keypath during repair. Settings created by the MSI participate in its rollback
and removal; dump files themselves are user evidence and are retained. Changing
an MSI-owned policy afterward does not transfer its registry ownership to the
administrator: removal can still remove those owned values.

This machine policy cannot interpolate `RDRIVE_DATA_DIR` separately for every
running process. Custom account roots change metadata placement, while WER uses
its configured folder. A custom administrator policy may choose a different
folder, dump count, or dump type. The portable ZIP does not install a WER policy
and does not request elevation to enable one.

WER dump collection is best effort. Folder permissions, storage capacity,
automatic crash debugging, and a process using its own crash handler can prevent
a dump. Missing evidence must be reported as unavailable. A dump is associated
with a failed launch only after checking its process identity and launch time;
the newest file alone is not evidence that it belongs to that failure. Dumps
under Windows' standard `CrashDumps` directory or administrator-defined locations
must not be copied indiscriminately into a support report.

Before launching, the native observer can prepare the default sibling dump
directory only when the machine marker and exact default policy prove MSI
ownership. It does not create account storage or administrator-selected dump
folders. The saved summary reports whether this preparation succeeded.

Microsoft documents [user-mode dump collection and per-application policy](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)
and [MSI Formatted-string escaping](https://learn.microsoft.com/en-us/windows/win32/msi/formatted).

## Matching symbols

Release builds produce portable .NET PDBs and a matching native launcher PDB.
`build.ps1` retains them in `artifacts/win-x64/symbols`, outside the portable
ZIP and MSI. Its `build-manifest.json` records the source commit, whether local
source was modified, .NET SDK, configuration, workflow run, and SHA-256 hashes of
the exact public binaries, packages, and symbols. A version label alone does not
establish a match.

CI stores symbols as a separate workflow artifact for 30 days; tagged release
runs retain them for 90 days. They are not GitHub release downloads. Workflow
artifact permissions follow repository access rules; a public repository does
not make them confidential. For long-term or restricted retention, copy the
accepted build's symbol artifact and manifest to controlled storage before the
workflow artifact expires. Never replace matching symbols with a rebuild of the
same version.

Native builds use an installed Visual Studio C++ x64 toolchain located through
`vswhere`, with a static C runtime. GitHub Windows runners provide that toolchain.
Local packaging requires the C++ build tools and fails clearly if unavailable;
the build does not download a compiler. `tests/native-crash-smoke.ps1` exercises
only disposable fixture processes and isolated metadata, without modifying WER
policy or installing ResoDrive.

## Reporting and analysis

Use **Copy details** in the native error dialog, then paste the result into the
support conversation. **Technical details** shows the same local summary.
**Open diagnostics folder** locates the saved summary. A summary is small text;
a memory dump is a separate sensitive file and is never put on the clipboard or
sent automatically. If storage is unavailable, the dialog still offers copying
the available evidence. Closing it does not terminate background transfers.

An investigation needs the failure time and reference, exact executable hash and
version, process identity, exit/exception code, Windows build, correlated crash
event fields when available, guarded startup/host logs, and a full dump with its
matching symbols. Evidence must distinguish the process that failed from other
UI/host instances. A missing dump or event is reported as unavailable; there is
no guarantee that every termination invokes WER or that storage remains writable.

Microsoft's [crash troubleshooting guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior)
uses Application Error events and full dumps together. A reported module such as
`KERNELBASE.dll` is not sufficient evidence of the cause. Analyze the dump in
WinDbg or Visual Studio with the accepted build's symbols before attributing a
failure to ResoDrive, .NET, Windows, or an injected module. Microsoft's
[Visual Studio problem-reporting workflow](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-report-a-problem-with-visual-studio?view=vs-2022)
also combines reproduction steps, traces and dump attachments reviewed before
submission. ResoDrive keeps this evidence local and provides explicit copying.

When a reproducible failure produces no WER dump, Microsoft
[Sysinternals ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump)
can launch the actual installed executable under observation. For a controlled
support reproduction, use `-ma -e -t -x <dump-folder> <resodrive.exe> --show`.
Specify the verified installed path, not a similarly named portable copy.
`-t` also captures orderly termination: label that evidence as an exit dump until
analysis establishes a crash. This is a troubleshooting step, not a permanent
startup debugger or automatic fallback installed by the application.

## Verification limits

Local native smoke uses synthetic abnormal exits to test process observation,
argument forwarding, process identity, metadata, retention, session shutdown,
unavailable destinations and standard dialog rendering. Those tests do not
reproduce a CLR runtime failure or prove WER capture. Hosted installer acceptance
separately triggers native and .NET 10 fatal fixture exceptions and checks the
resulting Windows full-memory dumps under the installed policy.
Desktop UAC and the affected vessel still require their own acceptance evidence.

The installer lifecycle test additionally calls that smoke test with `-VerifyWer
-VerifyClipboard`
after installing the candidate on a disposable GitHub-hosted Windows runner. It
checks the installed registry value types and literal path, triggers a real
fail-fast in separate disposable native and CLR fixtures supervised by the
packaged launcher. Each dump must match PID and creation time, declare full memory,
and contain valid thread, module, exception, CPU context and Memory64 ranges.
The CLR dump must also contain `coreclr.dll`. Local synthetic layouts exercise
the acceptance parser without capturing real memory. It also checks that global WER policy values survive install,
repair, upgrade and removal. This acceptance path refuses developer machines and
pre-existing per-executable policy; local metadata tests alone do not establish
that Windows dump collection worked.
The clipboard check presses the real dialog's copy button and compares its
Unicode text with the saved summary on the disposable hosted desktop.

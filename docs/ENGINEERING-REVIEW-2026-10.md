# October 2026 engineering review

This pass builds on [the transfer and connection review](TRANSFER-RELIABILITY.md).
It keeps the existing process model, settings and screens. No additional service,
VPN manager or installed helper is required.

## Readability and ownership

- Host commands, queued operations and upload/session protection have named
  boundaries within the existing Worker partial class. Reload-gate ordering,
  generation-bound retries, exact cancellation-source cleanup and separate
  mount/sync capacity are preserved.
- Transfer presentation separates upload rows, sync rows, row reconciliation and
  summary precedence. Stable row identities survive status updates.
- Main sync rows and the popup share display normalization. Negative and
  non-finite telemetry becomes unavailable; unrepresentable durations cannot
  reach TimeSpan formatting. Raw observations and fractional progress stay intact.
- Pinned SFTP host keys use one Core validator, also enforced before Windows
  password/key-file provisioning. The declared algorithm must match the SSH blob
  header. This consolidates the existing profile rule rather than inventing an
  additional credential policy.

## Work avoided on slower machines

Deserialization creates a new upload-file list on every host poll. Record
comparison previously treated equivalent lists as changed and notified all mount
card bindings. The card now compares file contents and visible status values,
retaining each newest raw observation. A regression submits 100 newly allocated
equivalent observations and expects zero property notifications after initial
presentation, for both idle and pending-upload cards. Progress, errors, recovery,
host outages and checking freshness still trigger updates.

This is a deterministic reduction in redundant UI work, not a measured claim
about total CPU, memory use or startup time on a physical older computer. Earlier
changes also bound concurrent mount/sync launches, back off unavailable endpoints
and avoid repeated clean cache scans for five seconds; cache invalidation bypasses
that shortcut.

## Installed crash evidence and hardening

Windows Application Error and .NET Runtime events identify one installed
`C:\Program Files\ResoDrive\resodrive.exe` crash in the available 40-day window,
at **2026-10-30 11:06:25 Europe/London (UTC)**. Development/test executable paths
and duplicate Windows Error Reporting records are excluded from that count.

The failing UI had started on .NET 10.0.13. Windows Installer recorded Core and
Desktop Runtime 10.0.14 installation at 10:21:15–17, Desktop 10.0.13 removal at
10:22:04, and Core 10.0.13 removal at 10:22:52 while that UI was running. The fatal
exception was FileNotFoundException for System.Diagnostics.StackTrace, inside
ProcessDiagnosticLog.Exception, called by the top-level UI exception handler.
This is consistent with lazy assembly loading after removal of the running
runtime. Logging masked the original exception, so these records do not establish
the original trigger or who initiated the runtime update. The UI restarted at
11:07:11 using 10.0.14; the existing host remained running.

Exception reporting now isolates optional StackTrace metadata behind a guarded,
non-inlined method. Missing formatting dependencies retain bounded, sanitized
original text plus exception type/HResult when possible. Resolving the UI logger
and displaying the startup error are also best effort, so a reporting failure
cannot replace the original exception. Account-data guards and local-only logging
remain in effect; there is no unguarded fallback destination.

A regression uses a collectible AssemblyLoadContext that refuses the StackTrace
assembly. Other cases simulate formatting/provider failures and check redaction
and bounds. No installed runtime was removed or changed to exercise this path.
These protections improve failure reporting; they cannot guarantee continued WPF
execution after another installer removes runtime files. The original exception
cannot be recovered retrospectively from the available records.

## Scope and verification

The full Release suite, constrained-CPU App suite, portable build, compiled WPF
renders and isolated process smoke results are recorded in the transfer review.
Network behavior uses injected probes and loopback TCP, not production VPN route
changes or server writes. Installation, runtime deployment and physical slow-PC
acceptance remain separate checks; the installed application was not replaced by
this source review.

Broader rewrites of setup orchestration and sync-run ownership were reviewed but
left out of this change. They would require separate provisioning and lifetime
regressions; adding abstractions alone would not simplify the existing behavior.

## Final cleanup and Settings review

The default main window is 920 by 720 device-independent pixels, increased from
920 by 610. The normal Settings page fits without scrolling in the compiled WPF
layout at 100%, 150% and 200% rendering scales. The minimum window remains 640 by
440 and retains scrolling; startup still constrains the window to its monitor's
work area. Error text, progress rows and smaller displays can require scrolling.
The last card now describes settings and diagnostics together.

An independent cleanup pass found that transient drive summaries were assigned
twice per poll. The final text is now composed before one assignment. Regression
coverage repeats 100 equivalent Starting, Stopping and WaitingToRestart
observations without another summary notification, then verifies one update on
the mounted transition.

The diagnostics follow-up expands the existing report rather than adding a new
screen or background collector. It includes anonymized host/transfer/recovery
context and a bounded timeline from current and rotated UI/host logs. Versions,
states, numeric measurements and known diagnostic categories are exported; raw
messages and connection/file identities remain local. This distinguishes a lack
of retained history from a lack of recorded errors. It is not a crash dump or a
complete Windows Event Log archive.

The collector reads at most 64 KiB and 250 lines from each current/rotated file
for each role (256 KiB total), off the UI thread. It refuses remote, device and
redirected paths, preserves cancellation and retains readable records when another
source is missing, locked or malformed. The report marks incomplete/truncated
sources and shows at most 80 events per role, reserving space for up to eight
recent startup identities when those remain in the excerpt. Retention limits
cannot reconstruct events already rotated away.

Shared reports include UI uptime, available logical processors, managed-memory
and working-set measurements without forcing garbage collection. Runtime history
comes from retained startup records, rather than claiming the host runs the UI's
current runtime. Exceptions export fixed categories and numeric HRESULT values;
raw CLR type names and stack frames stay local. Known Worker operations now have
fixed structured event names so redaction cannot erase their category. Ordinary
generic-host fatal detection and account-write protections are unchanged.

Failures caught by the central UI action handler now persist one local exception
record and show its error ID in the existing Activity entry and error dialog.
No periodic hardware inventory, additional logging thread or new preference is
introduced. The synthetic report captured during tests contains deliberately
private inputs whose absence is asserted before the review artifact is written.

## Performance work verified without a physical older PC

This pass removes measured redundant work while retaining the existing health
polling, destination retry timing and conservative upload decisions.

| Boundary | Verified change |
| --- | --- |
| Three small RC status responses | 8 KiB initial rentals, 24,576 bytes returned for clearing in the deterministic reader test. The former maximum-size request rents a 2 MiB bucket on this runtime: 6,291,456 bytes for three replies. |
| 100 hidden transfer-popup refreshes | 0 bytes allocated at the popup-refresh boundary, compared with 389,464 bytes when row construction is forced in the same fixture. Host observations and tray protection still update. |
| Ordinary UI startup | Two redundant local `rclone version` process launches removed: one duplicate component inspection and one duplicate verification within an update check. The update-service lock still encloses its own fresh canonical verification. |
| Unchanged recovery observations | The existing journal entry is retained without allocating an argument copy or attempting a write. The regression holds the journal exclusively during 20 observations. |

The startup observation is a local value, consumed only by the initial component
rendering. Setup discards it; runtime mutation invalidates it by generation; a
failed observation or ordinary refresh performs a fresh inspection. Tests exercise
the compiled MainWindow and injected process runner, including close cancellation.
Update checks reuse only a successful canonical verification from the same held
mutation lock. Backup recovery and installation retain fresh verification, and a
second update check inspects a replaced executable again.

The transfer popup builds once per visible update, skips row construction while
hidden and refreshes from current observations immediately before reopening.
The setup reload path also updates the tray/popup after accepting its response.
The model continues tracking all upload, sync, host and power-protection state.

RC reads retain the exact 1 MiB payload limit, deadline, authenticated loopback
transport, partial-read handling and clear-on-return buffer policy. Larger replies
grow their buffers as needed. Metadata and fresh component inspections now receive
the window lifetime cancellation token, allowing close to cancel pending work.

These are call-count, allocation and bounded-buffer measurements from controlled
fixtures, not claims about total CPU, startup wall time, or a speedup percentage on
the unavailable older machine. Numeric artifacts are retained under the workspace's
`output/performance-review/measurements` directory. Full-suite and constrained-CPU
results remain recorded in the transfer review.

## Release-gate cleanup test correction

The v0.3.47 tagged run (41052066148) passed its initial build and installer,
migration, recovery and previous-public-updater checks. Its final gate builds a
synthetic next version from the same source; that second full test run caught an
immediate-cleanup assertion in the live-image updater regression. Live replacement,
process ownership, cache preservation and launching the replacement had already
passed. No GitHub release was published from that run, and its tag is retained.

The test kept verified/original process handles alive and required all updater
artifacts to disappear immediately after WaitForExit. Production cleanup is
deliberately best effort while another handle holds a file. The corrected test
releases its process handles, deliberately holds the retained backup without
delete sharing, and verifies that an update check succeeds while preserving that
backup and the cache. After releasing the file lock, subsequent successful checks
must remove every updater artifact within the original ten-second deadline.
Remaining filenames are reported if cleanup fails. All live-image and rollback
assertions remain; there is no longer timeout, skipped test, production cleanup
change or forced tag replacement. The final release candidate is v0.3.48.

## Follow-up review — 13 November 2026

This pass starts from `8bff9bd`, following the previous connection/performance
review. It surveys the Core validation boundaries, App settings/setup/update
flows, Host queues and shutdown, Windows persistence/process/rclone/recovery
paths, and installer/build organization. Generated outputs, archived workspace
copies and the private deployment projects are outside the application review.
This is a risk-focused review, not a claim that every line is defect-free.

### Completed changes

- Recovery and mount-ownership stores could load a valid backup, then overwrite
  it with a corrupt primary on the first repair write. They now retain the verified
  backup for that write and resume normal backup rotation afterward. Regression
  fixtures corrupt the repaired primary again and verify recovery still works.
  Recovery-store cleanup also releases its mutation gate if temporary-file
  removal fails.
- Cache metadata containing valid JSON of the wrong shape (`null`, arrays or
  booleans) escaped as `InvalidOperationException` during offline inspection.
  Shape validation now yields the expected invalid-metadata result, preserving
  recovery records and treating uploads as unknown. Cancellation is observed
  before the cache-off fast path as well.
- With caching off, rclone returns an empty object for `vfs/queue`. The parser
  wrongly required a queue array and could leave an idle drive permanently
  unverified. Only the documented cache-off empty object is now accepted;
  direct uploads still come from `core/stats`, and missing cached-mode queues,
  error-shaped objects and malformed responses remain unknown. See
  [the rclone VFS control API](https://rclone.org/rc/#vfs-queue-queue-info-for-a-vfs).
- Graceful mount shutdown now sends authenticated `core/quit` directly to the
  existing loopback endpoint. It no longer launches another copy of rclone or
  depends on the replacement executable being launchable. Upload guards,
  ownership checks and exit confirmation remain in the coordinator. Transport
  code moved from the transfer-status model into `VfsStatusReader.cs`; the shared
  transport retains bounded replies, cancellation, no proxies and no redirects.
- A refused updater-helper launch now records a finalized failure instead of
  leaving a pending receipt. Signature, application-control policy and reputation
  failures retain their Windows error codes and give specific guidance. The same
  classification covers installer launch, Burn-wrapped errors and application
  relaunch. Access-denied and UAC cancellation remain distinct. A successful MSI
  followed by a blocked relaunch remains a successful installation with a failed
  relaunch; it is not mislabeled as a rolled-back update.
- A private, account-restricted readiness pipe now keeps the old UI open until
  the copied updater reaches its handoff code. The helper verifies the parent
  process and account and requires acceptance before waiting for exit. Timeout
  or cancellation finalizes the parent's failure receipt; a late helper exits
  without running installation. Legacy direct helper invocations remain supported.

Six new regression cases failed before the recovery/parser fixes and passed
afterward. Update-block tests inject Windows launch failures; they do not alter
Windows security policy. Real rclone 1.76.0 interoperability tests start isolated
local WebDAV VFS instances with disposable configuration, cache and credentials,
cover cache-off/full status, reject wrong credentials, accept authenticated quit
and preserve the local sentinel. No production mount or server account is used.

The reported 12 November App Control block happened on another machine. Local
event queries therefore do not identify its blocked file or policy. The local
installed executable is unsigned, consistent with the documented release build;
this establishes a distribution gap, not the remote incident's precise cause.
The [release guide](RELEASING.md#signing) now contains a prepared free-signing
enrollment brief and the required packaging order. Following the owner's request,
the free SignPath application was submitted and the owner installed the GitHub
App. Foundation approval and actual signing remain pending. Version 0.3.50 is
being prepared as an unsigned maintenance release at the owner's request.
The 0.3.49 tagged workflow was cancelled before publication to include the
owner's subsequent request for selection-only dropdowns. Its tag is retained.

### Remaining design work, in priority order

1. **Signed release chain and first-hop acceptance.** Sign the application and
   installer layers, including the extracted Burn engine, then test the actual
   affected policy. An unsigned old helper may require one manual signed Setup
   installation. The helper-ready acknowledgement is now implemented, covering
   helpers that fail during initialization as well as synchronous launch refusal.
2. **Durable sync-run ownership.** `RcloneSyncCoordinator` tracks live runs in
   memory and persists only terminal outcomes. Following an abrupt host death,
   ordinary startup cannot identify/rejoin a surviving sync by job identity.
   The wipe path conservatively detects surviving managed processes, but that
   is not ordinary sync recovery. Persist job/process identity and interrupted
   state before adding replay. Do not automatically rerun a destructive mirror
   after an ambiguous interruption. This needs crash/process acceptance, not
   just another coordinator abstraction.
3. **Durable setup transaction manifest.** `SetupFileTransaction` rolls back
   exceptions in the current process and retains backups on failure, but its
   ordered transaction entries live in memory. A process/power failure between
   settings/configuration commits needs a durable manifest and startup recovery
   policy that respects the account-write guard and remote wipe generation.
4. **Focused responsibility extraction.** The main mount coordinator still owns
   a large start/stop lifecycle; setup and update components are also large.
   Extract lifecycle decisions only alongside ownership/ordering tests. Keep
   security-specific persistence semantics explicit instead of replacing all
   stores with a generic serializer. Existing partials and presentation modules
   already provide useful boundaries; another broad file shuffle adds little.

Keep the existing queue backoff and rclone's live transfer retries. There is no
evidence here supporting forced remounts on network loss, deletion of dirty
caches, automatic mirror replay, a global shared rclone daemon, or removing
legacy installer compatibility paths. Those changes would weaken recovery or
broaden failure scope without solving the defects reproduced in this pass.

### Verification

- Version 0.3.50's full `build.ps1 -BuildMsi $true` passed: Core 206, Windows
  771 and App 441 — **1,418 passed**, with one optional installed-MSI locator
  skipped. MSI and Setup built without warnings. All 441 App tests also passed
  with one available CPU. Published-process startup/recovery and the rejected
  updater handoff smoke checks passed with disposable data.
- The owner approved the compiled drive-settings layout and requested plain
  choice labels, without "(saved)". Drive, setup and sync forms now use visible
  sections; obsolete editable-combo and expander templates were removed. The
  compiled forms were rendered at normal/minimum sizes and 100%, 150%, and 200%
  scaling. Save/Cancel remain outside the scroll surface. Coverage includes
  required selections, preserved custom/disabled values, rejected text input,
  actual save validation and keyboard focus on the invalid cache field.
- Settings and empty-state wording now describe user actions; removal dialogs
  explicitly preserve files. Release-writing guidance requires plain user-facing
  notes. Tagged hosted installer acceptance and desktop UAC checks remain the
  publication gates; local builds alone do not establish those results.
- Version 0.3.49's full `build.ps1 -BuildMsi $true` passed: Core 206, Windows
  771 and App 441 — **1,418 passed**, with the optional installed-MSI locator
  skipped. MSI and Setup built without warnings. The published helper process
  signaled readiness, then exited after a rejected handoff without changing the
  source, installer sentinel or parent receipt. Both workflows now run this check.
- Locked restores and `build.ps1 -BuildMsi $false` passed using the existing
  .NET 10.0.400 SDK. Release suite: Core 206, Windows 771, App 437 — **1,414 passed**.
  One optional installed-MSI locator test was skipped; rclone integrations ran
  against the existing 1.76.0 executable with isolated data.
- The App suite also passed all 437 tests with `DOTNET_PROCESSOR_COUNT=1`.
  The isolated process smoke passed with that constraint: populated drive rows,
  concurrent cold startup, tray/show acknowledgement, automatic host recovery
  and relaunch. Its disposable processes and data were cleaned up successfully.
- No live App Control policy block, signed installer, cross-elevation upgrade,
  power-loss upload or real Nextcloud wipe was tested. Existing wipe tests use
  simulated HTTP. Full signed MSI/Setup and affected-machine acceptance remain
  release gates; the generated portable package is a local unsigned candidate.

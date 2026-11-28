# Transfer and startup reliability review — 30 October 2026

This work addresses intermittent upload warnings, progress switching back to
preparation, drive starts stuck in a queue, and manual disconnection for normal
rclone updates. The observed code defects were reproduced with isolated tests;
they do not establish the exact cause of every incident on the reported laptop.

## Findings and changes

- Every VFS metadata event previously invalidated every mounted drive. A changing
  cache could also discard fresh control counters before publication. Invalidation
  now follows the affected cache path, including directory changes, renames and
  watcher overflow. Fresh measurements survive a cache race while the independent
  safe-stop verdict remains unknown.
- The UI hid progress on every checking transition and used an immediate red
  balloon for routine upload attention. Recent measured bytes now stay visible
  briefly with an updating-status label; stale speed/ETA stay hidden. Persistent
  problems get a delayed, episode-based amber warning. Recovery and power failures
  remain immediate, including escalation after an earlier ordinary warning.
- Empty/invalid JSON statistics became all-zero transfer measurements. The parser
  now rejects them as measurements. A sync that has already transferred data uses
  a waiting/retry phase when counters reset, and a new run resets that phase.
- Mount operations and indefinitely running syncs shared two slots. Two syncs
  could therefore leave a drive queued for their entire duration. Separate slot
  pools remove that starvation. Queue timeout, cancellation, rejected preflight
  and unexpected failures now publish a retryable drive state.
- Mount startup gets 45 seconds before reporting delayed readiness. The existing
  health monitor continues checking a surviving process. Verified idle observations
  avoid repeated cache-tree scans for five seconds; watcher invalidation bypasses
  that shortcut. Active/unconfirmed work remains conservative.
- The rclone updater already staged and verified an executable and used Windows
  atomic replacement. Its UI blocked all mounted drives anyway. Normal updates
  now leave their loaded images running. New launches use the updated runtime;
  loaded backups remain until they can be safely cleaned. Repair keeps its separate
  stop-work requirement. No zero-speed or idle heuristic can interrupt an upload.

## External review

[Dropbox's status guidance](https://help.dropbox.com/sync/check-sync-status) separates
indexing, syncing, paused work and individual sync issues. Its
[Windows icon guidance](https://help.dropbox.com/sync/sync-icons-windows) distinguishes
disconnection from account errors. [Google's troubleshooting guidance](https://support.google.com/drive/answer/2565956)
describes automatic retry for temporary sync problems. These informed the quieter
waiting/retry wording; this app still uses rclone VFS rather than either product's
sync engine.

[rclone's update documentation](https://rclone.org/commands/rclone_selfupdate/)
documents verified downloads and retention of the running Windows image under
another filename. Local Windows tests establish that this application's actual
replacement/rollback path preserves a running process's identity and cached data.

GitHub search found no matching "mount queued" issue in this application's
repository. [rclone issue 9482](https://github.com/rclone/rclone/issues/9482) reports
idle Windows/WebDAV disconnections, but does not establish that its cause matches
this report. The queue starvation fixed here is independently reproducible in
ResoDrive's host scheduler. The upstream
[VFS writeback source](https://github.com/rclone/rclone/blob/master/vfs/vfscache/writeback/writeback.go)
was also reviewed for queue/retry semantics.

## Verification scope

All process, cache and watcher tests use disposable local data. Update discovery,
download and version responses in the live-image regression are simulated; Windows
replacement, rollback, locks, launches, ownership verification and backup cleanup
are real. No production mount/cache or remote account was used for a destructive
test, and no production rclone update was installed.

Compiled flyout renders cover active, checking, retrying, unavailable, idle and
long-queue states at 100%, 150% and 200% rendering scales. These are WPF renders,
not an assertion that physical display scaling or multiple-monitor placement was
tested. A running isolated popup exposed its updated progress and accessible
controls; further desktop input/capture encountered Windows access denial.
Keyboard/dismissal acceptance on the interactive desktop remains a manual check.

The final local portable-build, suite and process-smoke results are recorded below.
Real interrupted WAN uploads, laptop sleep/resume, forced shutdown and installed
MSI upgrade acceptance remain separate manual/release checks. This change does
not publish a release or replace an installed application.

| Check | Result |
| --- | --- |
| `build.ps1 -BuildMsi $false` with the pinned .NET 10.0.400 SDK | Passed; portable package generated |
| Complete Release test suite | 1,377 passed: Core 206, Windows 748, App 423; three optional integrations skipped |
| App tests with `DOTNET_PROCESSOR_COUNT=1` | All 423 passed |
| Selected Windows tests with `DOTNET_PROCESSOR_COUNT=1` | All 91 passed: VFS reads, connection waits, update checks and upload recovery |
| `tests/process-smoke.ps1` against the compiled candidate with `DOTNET_PROCESSOR_COUNT=1` | Passed: populated drive row, concurrent cold start, show/tray acknowledgement, host recovery and relaunch |
| Real Windows running-image update and rollback | Both passed with preserved process identity and disposable cache hash |
| Compiled WPF render inspection | Nine states rendered at three scales; active, checking, queue, clean/pending connection waits and invalid sync telemetry inspected after refactor; keyboard/physical monitor acceptance remains manual |
| Compiled Settings layout | Default 920×720 fits without scrolling; 640×440 retains scrolling and accessible actions; rendered at 100%, 150%, 200% |
| Diagnostic collection/export | Rotation, byte/line/event limits, local-path checks, malformed/locked files, cancellation, runtime-history retention, unknown telemetry and hostile-data omission passed; synthetic report inspected |
| `git diff --check` | Passed |

The [engineering review](ENGINEERING-REVIEW-2026-10.md) records the subsequent
readability changes, reduced WPF notification work, invalid-telemetry handling,
shared SFTP validation, installed-crash investigation and final Settings/diagnostics
cleanup and measured performance changes. The 1,377-test total includes those regressions. A separate Host review found no command, cancellation,
queue-capacity or upload-protection drift from the pre-refactor working tree.

Pre-release local evidence: working-tree changes on base commit
`76ad96632e07ad600b67a3747ccecd79d86f154e`; the base commit alone does not identify
those then-uncommitted changes. The measurements and hashes below describe the
local 0.3.46 review build, before release versions were assigned. Public
0.3.48 packages are rebuilt from their annotated release tag and separately
verified through the release workflow and exact-asset acceptance checks.

- Executable SHA-256: `d44343c9bba6a61243bc997c05e11bd37c4c75b420b6cc75e977f34bee9a9675`
- Archive SHA-256: `8fe7cbc76640cd392583c1e998b37aca0ecc24ab00e1aaf3b0e221cfdd19b3ff`

## Simple connection handling adopted after the review

The follow-up intentionally keeps the same screens and controls. There is no
VPN selection, connection-rule editor, travel mode, extra dashboard or history
panel. A configured WebDAV/SFTP destination that cannot be reached is shown as
**Waiting for connection**, with the existing **Stop** action.

Checks use the actual managed endpoint/custom port, allow up to five seconds, and
back off through 5/10/20/30 seconds. Network address/availability changes are
coalesced with a two-second settling period; a long monitor gap also triggers a
retry hint after resume. Timed retries remain available without Windows hints.
The checks release mount queue capacity after each attempt and never hold the
shared launch gate or host reload gate while waiting for the network.

Desired connections and queued retries have generations. An accepted Stop makes
older candidates inert; refusal or cancellation preserves the original desired
connection and reconnect budget. A pending probe is cancelled before the host
waits on its upload-check gate. Verified empty no-process waits, even during a
retry probe, do not pulse sleep/shutdown protection or require an active-work exit
confirmation. Cached, incomplete or unverified work keeps its protection. Cache
identity is validated before network waiting, and retiring a clean recovery
journal preserves a still-desired waiting connection.

The normal runtime-update activation path now refreshes the engine identity
without reapplying startup automount policy. Bootstrap and repair explicitly opt
into that policy, so a normal update cannot reconnect a deliberately stopped
drive. A separate regression confirms that initial activation still works.

Follow-up regressions cover stale queued retries, cancellation during an active
probe and its transition toward startup, ordinary exit/sleep during a clean retry,
dirty/unknown recovery guards, reconnect-budget preservation after a refused stop,
parallel offline probes, clean-journal retirement while waiting, transport fallback,
and notification coalescing/resume. Network conditions use injected probes and
loopback TCP; no production VPN switching, route changes or server writes were used.

Proxy/custom SSH/bind/environment transports conservatively retain the existing
engine path; the destination check does not attempt to approximate their route.
Live sessions keep rclone's existing upload retry behaviour and are never killed
or remounted in response to network loss. Reachable-server authentication/TLS
failures remain on the engine's existing diagnostics/retry policy; this work does
not introduce a terminal authentication-error classifier or VPN enforcement.

[Syncthing supports restricting a device to selected networks](https://docs.syncthing.net/users/config.html#allowednetwork).
[Windows supports app- and DNS-triggered VPN profiles](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/vpn/vpn-auto-trigger-profile),
but those depend on the configured VPN provider/profile. ResoDrive instead reacts
to destination availability through its existing host. No VPN configuration,
network policy, service installation or credentials management was added.

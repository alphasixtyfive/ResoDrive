# November 2026 engineering review

This pass follows the October review and the component download work in 0.3.51.
The changes in 0.3.52 focus on prerequisite status ownership, deployment data
ownership, and installation completion. They preserve the existing host/process
model. This is a targeted refactor and review, not a replacement of the codebase.

## Correctness and ownership changes

WinFsp inspection previously shared the rclone operation generation. An unrelated
rclone operation could invalidate its pending result with no subsequent refresh.
Each window now owns a coalesced prerequisite refresh with a five-second cooldown,
explicit activation refresh, and lifetime cancellation. Settings and drive setup
refresh after an external installation. Refreshing setup does not reset its input
or the user's mount choice. Cancellation and exceptions release the pending gate.

Deployment catalog installation now belongs to the application. A noninteractive
command runs the existing migration preflight and calls one Windows service that
uses Core's full catalog validator. It preserves valid catalogs, backs up invalid
ones, and atomically publishes a staged file under the account-write guard. Size,
UTF-8, local path and reparse checks precede publication. No private deployment
catalog, server address or bootstrapper source is included in this repository.

This avoids external deployment tools duplicating validation and hardcoding the
retired data root. It also prevents their profile writes from racing account
cleanup. The existing wipe policy and profile retention policy are unchanged.

## Broader review

The source inventory contains 201 files. Reviewed boundaries include host command
and queue ownership, mount recovery and upload protection, sync process ownership,
setup validation/provisioning, component download/update handoff, data migration,
and the separate deployment bootstrapper's completion protocol.

The largest remaining coordinators are RcloneMountCoordinator (987 lines),
MainWindow (920), Worker (874), and SetupWindow (729), excluding partial files.
Line count alone is not a reason to split them. Useful future extractions need to
reduce the number of owners for actual state:

- Sync-run adoption should gain durable run ownership before any broader process
  lifetime rewrite. A host restart must not silently adopt an unrelated process
  or start duplicate work.
- Multi-file setup provisioning would benefit from a durable commit/recovery
  manifest. A new abstraction without interruption tests would not establish
  atomicity across configuration, secrets and settings.
- Continue separating prerequisite inspection from mutating component operations.
  A single shared generation cannot correctly represent independent work.

No changes were made to remote-wipe acknowledgement ordering, upload-protected
shutdown, process identity checks, or installer IPC access. Existing recovery
gates remain required for release acceptance. No TODO/FIXME/NotImplementedException
markers were found in production source; that search does not establish an absence
of defects.

## Verification

The full local Release build passed 1,447 tests: Core 206, Windows 796, App 445.
One optional installed-MSI locator test was skipped. All 445 App tests also passed
with one available CPU. Published process, updater-helper, and profile-import
smokes passed using isolated data.

New cases cover refresh coalescing, cooldown, cancellation, exceptions, an rclone
generation race, and preserving setup input across a missing-to-installed WinFsp
transition. Compiled WPF captures cover normal/minimum window sizes and 100%,
150%, 200% rendering scales. No system driver was installed or removed for these
tests; WinFsp transitions were injected.

Catalog cases cover new/preserved/invalid catalogs, backups, malformed and
oversized input, UNC rejection, a genuinely locked target, cancellation and a
pending wipe guard. Native deployment-to-managed import was also exercised with
isolated data. No production account or remote server was used.

Tagged workflow acceptance and desktop UAC evidence are retained separately with
the exact release artifacts, as required by RELEASING.md. Local builds alone do
not establish MSI upgrade acceptance or behavior on the reported remote PC.

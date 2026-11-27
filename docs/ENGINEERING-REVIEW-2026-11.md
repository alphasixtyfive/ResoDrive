# November 2026 engineering review

This pass follows the October review and the component download work in 0.3.51.
The changes in 0.3.53 focus on prerequisite status ownership, deployment data
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

The deeper review found the same temporary-lock problem in the existing-runtime
update path: verification and atomic replacement bypassed the shared retry
policy, and interrupted-update reconciliation could mistake an inspection lock
for an invalid executable. Those operations now use the lock-only retry policy
and keep lock failures distinct from a corrupt or rejected executable. A lock
must not authorize restoring a backup over the canonical runtime.

Drive setup now gates its editable form until initial catalog, drive and
prerequisite loading finishes. Cancel remains available. Previously an early
edit could be cleared by later profile initialization. Dedicated delayed-load
and close-during-load checks cover this lifecycle.
Save-time validation also checks actual profile selection and reserved letters;
an available profile drive preference is applied without replacing a valid choice.

## Broader review

The source inventory contains 201 files. Reviewed boundaries include host command
and queue ownership, mount recovery and upload protection, sync process ownership,
setup validation/provisioning, component download/update handoff, data migration,
and the separate deployment bootstrapper's completion protocol.

At the start of this pass, the largest coordinators were RcloneMountCoordinator (987 lines),
MainWindow (920), Worker (874), and SetupWindow (729), excluding partial files.
Line count alone is not a reason to split them. Useful future extractions need to
reduce the number of owners for actual state:

- Sync-run adoption should gain durable run ownership before any broader process
  lifetime rewrite. A host restart must not silently adopt an unrelated process
  or start duplicate work.
- Multi-file setup provisioning would benefit from a durable commit/recovery
  manifest. A new abstraction without interruption tests would not establish
  atomicity across configuration, secrets and settings.
- Plaintext imported rclone configurations currently use a whole-file recovery
  fingerprint. Editing an unrelated remote can conservatively block recovery of
  dirty cache. Encrypted configurations use selected-remote fingerprints. A safe
  improvement needs explicit migration of old `file:` markers and coverage of
  backends that refer to other remotes. Replacing the hash alone could associate
  pending uploads with a different destination; this behavior is retained here.
- Continue separating prerequisite inspection from mutating component operations.
  A single shared generation cannot correctly represent independent work.

No changes were made to remote-wipe acknowledgement ordering, upload-protected
shutdown, or public installer IPC access. Existing recovery
gates remain required for release acceptance. No TODO/FIXME/NotImplementedException
markers were found in production source; that search does not establish an absence
of defects.

## Verification

The final local Release build passed 1,457 tests: Core 206, Windows 806, App 445.
Two tests were skipped locally: the optional installed-MSI locator and a
file-link case requiring elevation (covered by the hosted release runner).
All 445 App tests also passed
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

Update lock regressions use real Windows file handles for staged inspection and
replacement locks, with simulated process launch/version output. They cover
temporary and persistent locks, cancellation, post-commit evidence retention,
and permission/application-policy errors that must not be retried as file locks.

Tagged workflow acceptance and desktop UAC evidence are retained separately with
the exact release artifacts, as required by RELEASING.md. Local builds alone do
not establish MSI upgrade acceptance or behavior on the reported remote PC.

The first tagged candidate, v0.3.52, was not published: a hosted run exposed
a cold PowerShell startup dependency in the inherited-output cancellation
fixture. The fixture now uses isolated copies of the native command shell,
confirms actual parent exit and a live child before cancellation, and retains
the same execution deadlines. The immutable tag was retained; 0.3.53 contains
the correction. No production ProcessRunner timeout was increased.

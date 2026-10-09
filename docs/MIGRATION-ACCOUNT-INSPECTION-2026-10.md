# Account-inspection failure during post-update migration

## Observed report

A Windows 10 Pro (build 19045) administrator using the same account reported
the migration startup window after updating to 0.3.42. The displayed account
message means process identity was unreadable, not that a different account
was established. Installation had already completed; preparation of the data
move failed before its rename.

The later unelevated diagnostic snapshot showed the exact public 0.3.42 binary,
two ResoDrive processes and two rclone processes with readable matching account
identities. The legacy data root was absent, the current root existed, no pending
migration journal remained, and rclone ran from the current root. This is
consistent with recovery before collection. The original failing PID, native
call and Windows error were not captured. Do not describe the particular user's
initial failure as conclusively reproduced.

## Established defects and correction

The migration account filter previously opened every same-name process with
`PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` before determining its image
or relevance. `SYNCHRONIZE` is not required to read a process token or image.
A disposable real-process fixture gives the current account query rights while
withholding synchronization. The old access mask returns Windows error 5 even
though the account and image are readable. The corrected filter verifies that
account and ignores the verified outside-installation image; it leaves the
process running. This establishes a cause of the displayed error, independently
of whether elevation or security software imposed it on the reported PC.

The preliminary reader now retains a query-only handle through account and
image inspection. `QueryFullProcessImageNameW` replaces module-memory reads,
which required unnecessary VM-read access and could encounter unloading modules.
Matching UI candidates still require synchronization and termination rights,
the expected account, installation, session and exact UI command line. The
retained native handles pin the process identities and supply exit checks.

If account inspection or acquiring a matched process's operational handle fails,
the code allows up to one second for that exact retained process to terminate.
It polls its exit status and honors cancellation. An unverified live process
still blocks; a confirmed different account retains its existing handling.
There is no token-read retry, host/rclone termination, permission grant or
unauthenticated shutdown fallback. Existing upload and ownership checks remain.

The token-error/exit boundary is exercised deterministically using a real child
that exits after inspection starts and a supplied account-read failure. A
separate ordinary graceful-exit probe ran 1,100 children without observing token
failure on the development Windows build. That experiment does not reproduce
the original Windows 10 error or prove an exit-time token failure caused it.

## Failure diagnostics

Handoff previously disabled the ordinary UI logger to avoid retaining files
inside a directory being renamed, then recorded only `Exception.Message`.
The new bounded, sanitized diagnostic log lives in the protected sibling
`%LOCALAPPDATA%\ResoDriveMigration\resodrive-migration.log`. It preserves exception
chains, native Windows errors, inspection stages, process IDs and stack metadata.
The startup window and `completion.json` share its error ID. Log failure never
authorizes migration or substitutes a successful outcome. Settings, credentials
and cache contents are not collected. The redaction regression includes a secret
in an outer exception and a native error in its inner exception.

## Validation of candidate 0.3.43

- Complete local Windows build, locked restores, MSI validation and Setup build:
  1,184 tests passed; three optional integrations skipped; zero warnings/errors.
- All 344 application tests passed with `DOTNET_PROCESSOR_COUNT=1`.
- Compiled process smoke passed populated rendering, concurrent startup,
  tray/show acknowledgement, host recovery and relaunch with disposable data.
- Desktop elevation smoke launched the exact public 0.3.42 host unelevated from
  an isolated copy, ran the new helper elevated under the same account, received
  exit code 0, verified old-host exit and preserved disposable settings/cache
  hashes. The production installation and its data were not upgraded.
- New native permission fixtures confirm a readable unrelated process is left
  alone, and a matched UI lacking termination rights still blocks while preserving
  a cache marker. Denial of even minimum identity access still produces the
  account error with its native code and leaves the process alive.
  Account-error exit and cancellation cases leave live children
  alone. Existing active-rclone, unknown-role, upload rejection and corrupt
  ownership checks continue to pass.

Local artifacts were built from the working changes, with the pre-commit revision
in their version metadata; they are development acceptance assets, not published
release assets. Build and process/elevation receipts are held locally under
`output/migration-account-diagnostic` in the containing workspace.

The corrected source still needs exact-commit hosted native installer/migration
acceptance, and any public release must follow `RELEASING.md` with immutable
accepted assets. No Windows 10 machine or actual affected user's update was
used in local acceptance. The post-recovery diagnostic cannot fill that gap.

The first hosted run of commit `9b66d65` exposed an assumption in the two new
permission fixtures: the hosted reader's enabled debug privilege bypassed the
disposable process DACL. The ordinary local reader correctly observed the denial.
The fixtures now use a private same-account impersonation token with only its
debug privilege disabled; the original runner token and production code are
unchanged. Native access assertions remain mandatory, with no elevated skip.
An additional local elevated fixture run was cancelled at UAC; it does not
invalidate the separate successful prior-version-host/elevated-helper check.

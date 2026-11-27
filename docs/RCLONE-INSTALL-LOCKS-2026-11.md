# rclone installation file locks — 27 November 2026

The reported 0.3.50 installation error said that a file was in use. Its shared
diagnostic report confirmed a missing managed runtime, but omitted the failing
file and installation exception. It does not establish which process held the
lock on the affected PC.

The pinned official 1.75.0 archive still downloads with HTTP 200 and matches the
recorded SHA-256. HTTP 206 resumption and complete-file HTTP 416 responses also
match the downloader's expectations. Fresh and resumed installations using the
actual 0.3.50 bootstrap code passed with isolated data. Holding its archive open
with FileShare.None reproduced an installation failure.

Version 0.3.51 retries only Windows sharing/lock errors 32 and 33. Each affected
operation gets five delays totaling five seconds (six attempts), with cancellation
between attempts. This applies to downloading, hashing, opening extraction files,
launching version inspection and committing the executable. The runtime mutation
lock and checksum/version checks remain in place. A locked installed executable
is left intact instead of being treated as corrupt and replaced.

The progress text explains that another program is holding a file. Persistent
locks return `rclone.file_locked` with a short explanation and recovery steps,
without exposing a user's full path. Download bytes are retained after an
exhausted lock retry. Access-denied and application-policy failures are not retried
as sharing violations. The code neither terminates other processes nor changes
security policy or permissions.

Regression coverage uses real Windows file handles for a temporary archive lock,
a persistent archive lock, cancellation, executable replacement after release,
preservation of an old executable after persistent replacement failure, and
preservation of a locked installed engine without downloading. Injected native
launch failures cover both lock codes and distinguish access/policy refusals.
Post-commit cases preserve a verified first-install executable or restore the
previous copy and keep the downloaded archive when execution stays locked.

An additional live check ran the modified bootstrap against the official HTTPS
download with an exclusive archive handle held for 700 ms. It completed checksum
and version verification and installed 1.75.0 into a disposable data root after
the handle was released. Production settings, caches and components were not used.

These tests establish the retry behavior, not the identity of the locking process
on the manager's PC. Release installer and UAC acceptance are recorded alongside
the accepted workflow artifacts before publication.

Local acceptance: the final `build.ps1 -BuildMsi $true` passed Core 206, Windows
785 and App 441 tests (1,432 total), with one optional installed-MSI locator
skipped. MSI and Setup built without warnings. All 441 App tests also passed with
one available CPU; the published-process startup/recovery and rejected updater
handoff smoke checks passed using disposable data.

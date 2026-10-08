# Migration acceptance records

These historical records describe the exact builds tested at the time. They include
withdrawn versions and deleted drafts. Their results do not establish acceptance
for a later build. Current behavior and publication requirements are in
[the migration guide](INSTALL-DIRECTORY-MIGRATION.md).

### Local implementation checks (2026-10-08)

- Windows build, MSI validation and Setup build: passed with zero warnings/errors.
- Core/Windows/application tests: 1,091 passed, three optional integration tests
  skipped (two real-rclone cases and the hosted-MSI catalog case).
- Application tests with `DOTNET_PROCESSOR_COUNT=1`: 303 passed.
- Isolated process smoke: passed window rendering, simultaneous launch, tray/show
  acknowledgement, host recovery and relaunch. It uses a custom disposable root.
- Real UAC preparation smoke against a copied public 0.3.30 unelevated host:
  passed, preserving settings/cache hashes. This checks the shutdown boundary;
  subsequent migration-only edits passed the complete build/test suite.
- Read-only native MSI AppSearch: found `C:\Program Files\rdrive\` correctly.
- Development MSI including diagnostics and connection improvements versus the
  accepted 0.3.30 MSI: 180,224 additional bytes (176 KiB, about 0.51%). Compression
  can vary this slightly between builds.

Disposable hosted Windows runs passed native fresh install, repair, removal,
public 0.3.30 direct-MSI upgrades and the older 0.3.20 Setup-owned transition.
Different-account preparation refused an active foreign host, then passed after
its owner stopped it, preserving settings/cache. A deliberate deployment-profile
conflict restored the old executable and disabled startup task through native
MSI rollback. That test exposed unfinished migration state blocking a subsequent
retry; the rollback now preserves the independent destination profile and
completes its journal, and acceptance explicitly checks both outcomes.

[Hosted acceptance run 37773217549](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37773217549)
passed the full old-updater first hop, post-rollback retry, default data rename,
byte-preserved settings/cache/managed copies/owner-protected credentials, disabled
startup preservation, temporary-helper deletion and old installation cleanup.
It ran the actual public 0.3.30 executable's update helper against native MSI,
then verified installed version `0.3.34+2634d7a` and the exact executable hash.
The second account migrated under its own loaded profile and decrypted its own
credential afterward. Fresh install, repair/removal and the older Setup-owned
transition passed in the same run.

[Tagged release acceptance 37775702295](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37775702295)
passed the same native checks with installed version `0.3.34+1b75bcb`.
All eight draft assets match their GitHub digests and SHA-256 sidecars. The MSI
hash is `CC79F131EF5A6BAD410EC10ACDFED0821344D8D4EEEF159E65765357D4CE00E8`;
the installed/portable executable hash is
`167F2C83711C5E75FDBE2B92A47E255CA3700073ADDFC2EF775E64BA0D696C14`.

[Visible installer acceptance 37777664209](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37777664209)
used those exact draft installers. Full fresh Setup and the older Setup-owned
upgrade each displayed a single `ResoDrive Setup` window with a real progress
page and exited successfully. No separate Windows Installer window was observed
by the 50 ms polling monitor. This does not establish every older Windows version
or exclude a shorter transient window. Repair/removal and user-data preservation
also passed. The accepted portable app passed process/recovery smoke and its
compiled window was inspected at the desktop's 150% scaling using disposable data.

The exact draft's final desktop UAC check remains pending: the elevation prompt
was cancelled before the helper started. The earlier UAC pass above used a
development binary, not this accepted executable. Publication remains on hold.
No production data migration was performed. Same-session disposable account tests do not
establish an interactive standard-user/separate-admin UAC prompt or the actual
sign-in trigger of the second user's task. The second user's migration entry
point was invoked under that account after verifying the registered task's
identity and least privilege. The old updater acceptance invokes its real
completion entry point; it does not automate the old UI's download/update click.
Cancellation and upload rejection have regression coverage; no live account or
pending production upload was used to provoke a shutdown block.

### Additional update reliability checks for 0.3.35

The next candidate adds a two-minute network inactivity limit to resumable
downloads, a 45-second checksum-request limit, reuse of an installer only after
matching a freshly fetched published checksum, and specific recovery guidance for
Windows Installer codes 1601, 1618, 1625 and 1632. Slow transfers that continue
receiving bytes remain supported. Disk writes and progress callbacks do not count
as network inactivity. User cancellation remains distinct from a stalled network.

The existing Update action already refreshes the latest-release redirect before
installing. The same refresh icon now remains accessible alongside an available
update. Regression coverage checks successive releases, partial-download
preservation, checksum timeout, verified cache reuse/replacement, installer
failure outcomes, and the compiled controls' busy/retry states.

Local Windows build and MSI/Setup validation passed with zero warnings/errors;
1,103 tests passed and three optional integration cases were skipped. All 307
application tests also passed with one available CPU. The portable executable
passed isolated process/recovery smoke. The compiled Settings window was rendered
and inspected with WPF DPI set to 100%, 150% and 200%, at normal and minimum
window sizes; these are WPF render checks, not separate physical-monitor tests.

This candidate supersedes the unpublished 0.3.34 draft. The 0.3.34 asset
acceptance above remains historical evidence.

[Tagged 0.3.35 acceptance 37784471088](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37784471088)
and [CI 37784454853](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37784454853)
passed. Native installation verified version `0.3.35+17419b5`, old-updater
completion, rollback/retry, owner credentials, data relocation, startup state,
temporary-helper cleanup, repair/removal and the older Setup-owned transition.
Both full Setup UI receipts show one Setup window, progress and successful exit;
the 50 ms monitor observed no separate Windows Installer window.

All eight downloaded draft assets match their GitHub digests and SHA-256
sidecars. The MSI hash is
`CE77B0FC9240B3D1AE45567A7B270F7888234255245C3662A4E25C8F2EB1AE66`;
the installed/portable executable hash is
`4A03ECAE7DC1DFAA8BD30939BD8CB61E52EC4FC26BD521A1D8DC9A65CC3D5C4B`.
The downloaded executable passed isolated process/recovery smoke and the final
desktop UAC preparation check against a copied, unelevated public 0.3.30 host.
The helper was elevated, exited successfully and preserved test settings/cache.
At that check the desktop installation was still 0.3.30; the owner subsequently
updated it manually. These checks retain the account/sign-in,
old-UI-click and older-Windows coverage limits documented above.

### Withdrawal and correction of 0.3.35

The real desktop upgrade installed the exact .35 executable in the new program
directory and removed the old program directory. Its user-data move failed:
the reopened app had already started drives before the second shutdown, and
the upload/cache guard rejected that shutdown. The failed completion receipt
was preserved, but the old implementation silently resumed against the old root.
The .35 release was withdrawn and its draft was later deleted with the other
unpublished releases. Its immutable tag remains. The exact baseline MSI and
checksum are preserved in the original tagged Actions artifact and a verified
local archive for recovery testing; Actions retention remains finite.
The latest public release is .30 while the correction is being accepted.

CI and the tagged Release workflow now run the full visible Setup checks directly
and preserve their receipts. The separate draft-dependent installer UI workflow
was removed because it duplicated those checks. The Release workflow prepares
all eight download files as an Actions artifact for acceptance, without creating
a GitHub draft. Only the accepted final release is published.

The previous hosted migration fixture had no startup drives and did not reproduce
this ordering. The full Setup window test also did not exercise the in-app path:
compiled older updaters download MSI and display its passive progress window.
The corrected updater downloads versioned Setup, hashes and locks that package,
and supplies its active data root explicitly. Older installed code cannot change
its download choice; use manual Setup for the first corrective upgrade.

New hosted gates observe the actual .30 helper with an enabled startup drive,
check that no new host appears before relocation, exercise the current helper's
passive branded Setup, and recover the partially migrated .35 layout using its
actual retained MSI. That recovery starts the baseline with its existing handoff
flag to leave the default data root in the observed old location. No production
accounts or uploads are used. An enabled drive with no live backend establishes
startup ordering; it does not establish behavior with real pending uploads.
These gates, exact-asset UAC acceptance and manual desktop recovery must pass
before publication. Build success alone is insufficient.

### Exact 0.3.36 package acceptance

[CI 37811911274](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37811911274)
and [tagged acceptance 37811912063](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37811912063)
passed against source `6bbe580ee6f3a811d455a95fe8b96bfcb33d4b1b`.
The build passed 1,114 tests with three optional integrations skipped and no
warnings or errors. All 311 application tests passed with one available CPU.
Native lifecycle, different-account preparation, actual .30 updater migration,
rollback/retry, startup ordering, owner credential preservation and cleanup passed.

The exact .35 recovery fixture first verified its running host through the
authenticated status pipe and checked the kernel process identity, installation
path and Windows account. Manual Setup then safely stopped that host. The new
app moved the pending default data, replaced the earlier failure receipt,
preserved settings/cache/credential bytes and acknowledged its actual reopened
window. The current in-app updater also passed with a custom data root. Its
50 ms window monitor observed one branded Setup window and no separate Windows
Installer window; this cannot exclude a shorter transient window.

The eight Actions artifact files were downloaded and their SHA-256 sidecars
verified. Stable and versioned Setup are byte-identical. Installed executable
hashes from directory migration, .35 recovery and branded update all match the
downloaded portable executable. Setup hash:
`3546D01E5087E53ED6AB1EB96E9275FDFB957E4047507ADF77E70E603799F66B`.
MSI hash: `A94B95D71BFB6760ED8357E0BA4DF2FBCFB5DABA7E00710ED9F442BF2BEBCAD4`.
Executable hash: `0DB62167A9A1A6332115741EE3CED6132EFA3A73029523247632F9A8EE532EBD`.
That downloaded executable also passed isolated desktop process smoke: populated
drive rendering, simultaneous startup, tray/show acknowledgement, host recovery
and relaunch.

The desktop UAC preparation test used a copied .35 host and disposable data,
but elevation was cancelled before its helper started. It is not a passing UAC
check. The copied host was cleaned up, and the production .35 executable still
matches its recorded hash. No production installation or data move was performed.
Exact-asset UAC acceptance and the owner's manual recovery remain pending, so
the candidate is not published. All eight GitHub draft releases were deleted;
the 19 published releases and immutable tags were retained. Only final releases
will appear on GitHub; candidates remain Actions artifacts.

### Unpublished 0.3.37 review candidate

Source `431482c8570bacfa85c9df0a492de50f87f9bd4d` passed the local package
build, 1,140 tests and the one-CPU application suite. Its
[tagged build](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37838837419)
stopped on the native-window drag-preview regression before installer acceptance.
The fixture now explicitly completes queued WPF attachment and checks three DPI
scales with diagnostic assertions. The old log did not identify which assertion
failed, so the new source still requires hosted verification.

The tag remains immutable. Candidate 0.3.38 also adds explicit start intent,
cancellation-safe recovery pauses and setup outcomes that finish restoration
before presentation. No 0.3.37 release or draft was created.

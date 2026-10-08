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

### Unpublished 0.3.38 review candidate

[Tagged build 37840496110](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37840496110)
stopped before native acceptance. Core and Windows tests passed; the three
recycling-preview DPI cases found transparent pixels at the sampled coordinates.
The snapshot itself was subsequently verified opaque red. Reproducing the larger
native host's logical allocation shifted the list to `(150,140)` and reproduced
the failure locally. Fixing the test scene's size and origin retains the pixel,
container recycling and input assertions without changing product rendering.

The 0.3.38 tag remains immutable and unaccepted. No release or draft was created.

### Exact 0.3.39 package verification

[CI 37842307801](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37842307801)
and [tagged build 37842311460](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37842311460)
passed against immutable tag `v0.3.39`, source
`ef42566f0293dfb2eb71d45418b3423411f36143`. The full package build passed
1,157 tests with three optional integrations skipped and no warnings or errors.
All 331 application tests also passed with one available CPU.

Native receipts confirm fresh installation, repair/removal, direct MSI upgrade,
the older Setup-owned transition and one removable MSI with no retained bundle
entry. Different-account preparation, actual 0.3.30 updater migration, deliberate
upgrade failure with rollback/retry, enabled-drive startup ordering, disabled
startup preservation, signed-out-user migration and old-directory cleanup passed.
The new host did not start before migration completed, and owner credential
bytes were preserved.

The signed-out-account fixture invokes migration under the disposable account's
identity. It verifies that entry point and credential ownership, rather than an
actual interactive sign-in triggering the scheduled task.

The incomplete 0.3.35 recovery used the exact retained MSI from run `37784471088`
with SHA-256 `CE77B0FC9240B3D1AE45567A7B270F7888234255245C3662A4E25C8F2EB1AE66`.
Its prior host was authenticated before Setup preparation. Recovery moved the
pending default data, replaced the previous failure receipt, preserved settings,
cache and credential bytes, and acknowledged the reopened application window.
The actual current updater also passed with a custom data root. Fresh Setup,
the legacy Setup transition, manual recovery and the current updater's window
receipts observed branded Setup without a separate Windows Installer window.
Their 50 ms sampling cannot exclude a shorter transient window.

The eight files from artifact `resodrive-v0.3.39` were downloaded and all checksum
sidecars verified. Stable and versioned Setup are byte-identical. SHA-256 values:

| Package | SHA-256 |
| --- | --- |
| Setup (both filenames) | `EAEB5D909D140E32CE7FC4B61916C893C9B1E96ACBAEE7189555F452EBF15FBD` |
| MSI | `1FF14DFC1B0D23AAC30A69A866FD96C6CB3497C71CA75D65AE258C4608B6253B` |
| ZIP | `A20953CB6D975BCE3A3D3D4A693BBAFA5F274A80AF86745257E95CFBA20D2C3A` |
| Portable executable | `253AD760BF2CE7A326F526F587279FA5229FB3D8D735E93BB3BECEC475A4E18E` |

ProductVersion is `0.3.39+ef42566f0293dfb2eb71d45418b3423411f36143`.
Installed executable hashes in directory-migration, incomplete-recovery and
branded-updater receipts match the downloaded portable executable. The downloaded
executable also passed desktop process smoke with isolated data: populated drive
rows, simultaneous startup, tray/show acknowledgement, host recovery and relaunch.
Thirty compiled WPF captures inspected during the code review covered the main
window, Settings and recovery messages at 100%, 150% and 200% rendering with
fictional data; these are not separate physical-monitor checks.

At this initial verification stage, no desktop elevation attempt was repeated
after the cancelled 0.3.36 check.
Exact-package desktop UAC acceptance and the owner's manual production recovery
remain pending. The production 0.3.35 app and data were not changed. No 0.3.39
release or draft was created; all 19 published releases remain, with 0.3.30 latest.

### Desktop UAC and 0.3.39 publication

After the owner requested testing on this PC, the exact accepted 0.3.39 executable
passed `tests/elevation-smoke.ps1` against a copy of the installed 0.3.35 binary.
The old host ran unelevated; the new helper ran with UAC elevation under the same
Windows account, whose elevated token owner was Administrators. Helper exit code
was zero, the old host exited, and disposable settings/cache hashes were
unchanged. The receipt's helper hash and ProductVersion match the exact downloaded
portable executable recorded above. This is a helper test, not a production MSI
upgrade or a separate-administrator credential-prompt test.

The owner retained manual control of their production upgrade and explicitly
requested publication so they could run the update from GitHub. That request
overrode the procedure's requirement to finish their manual recovery before
publication. No production installation or data move was performed by the agent;
the installed 0.3.35 binary still matched its recorded hash, with the old default
data root present and the new default root absent before the manual upgrade.
Production recovery remains pending and must not be reported as verified.

[ResoDrive 0.3.39](https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.39)
was published as one final stable release using the accepted tag's plain-language
notes and all eight exact package/checksum files. A fresh download of all published
files matched the accepted files byte for byte. The anonymous public latest
redirect resolves to 0.3.39; the actual application update service also discovers
0.3.39 from 0.3.35 with the correct Setup and checksum URLs. No draft releases
remain. Immutable tags and previously published releases were retained.

### Production 0.3.35 in-app update to 0.3.39

The owner clicked Update in the installed 0.3.35 app. Its immutable updater
downloads MSI and launches `msiexec /passive`; the resulting Windows Installer
window belongs to that old updater's first hop. The installed executable now
matches the accepted 0.3.39 ProductVersion and SHA-256 above. Its finalized update
receipt reports installer exit zero and acknowledged relaunch.

Data relocation completed at `2026-10-08T22:44:01.1598868Z`. The old default root
was absent afterward, the new root present, and the migration completion receipt
reported success. The new host was requested after that receipt; the application
reached `startup.ready` at `22:44:01.7693619Z`. Read-only checks found unchanged
hashes for settings, their backup, encrypted rclone configuration, protected
credentials and profiles. Of 122 recorded non-log files, 100 hashes still matched,
15 live/recovery files changed, six hashes were unavailable, and one older MSI
download was removed. This does not establish byte-identical live cache; no
production data or process was modified by the verification.

The installer log also exposed unnecessary waiting. Preparation treated the
registered directory and MSI's equivalent directory ending in `\.` as separate
entries. Each absent-host pipe attempt waited 15 seconds. New-package preparation
and the old product's removal each took about 30 seconds. The initial updater
preparation and post-install migration also probed the absent host. Candidate
0.3.40 canonicalizes directory spelling before deduplication and skips only the
absent-mutex pipe wait; all existing account, process, upload and mutex-protected
fallback checks remain. Frozen older packages retain their own preparation code
and cannot receive this optimization retroactively.

### Unpublished 0.3.40 verification and recovery-test diagnosis

Candidate source `85ceb42749adc161f2e7c79a6b6cd779420d6494` passed the local
full build, 1,165 tests with three optional skips, all 331 application tests with
one CPU, and isolated process smoke. Its tagged build passed the same test suite,
but [CI 37856028354](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37856028354)
reported `Failed` instead of `Stopped` in the clean interrupted-drive fixture.
No 0.3.40 release was published and the tag remains immutable.

A disposable standalone reproduction completed the clean inspection in roughly
32 ms normally. Holding both worker threads for 4.2 seconds exhausted the real
three-second deadline before the queued inspection ran. The coordinator then
preserved the recovery journal and attempted protected cache recovery; the
fixture's deliberately missing rclone produced the observed `Failed` lifecycle.
The CI log's five-second duration is consistent with that mechanism, but its
limited diagnostics cannot exclude a transient filesystem error.

Candidate 0.3.41 keeps the production deadline and unknown-cache behavior
unchanged. It isolates this class of real filesystem/deadline tests from competing
parallel workload, while retaining clean/dirty recovery assertions and adding
the actual status to failures. The new source still requires its own complete
build and native package acceptance; a passing 0.3.40 tagged test run cannot
substitute for the failed CI result.

The tagged 0.3.40 native run also installed successfully and acknowledged its
reopened migration window, but relocation then failed with Windows error 299
(partial process-memory read). The earlier profile-conflict receipt belonged to
the deliberate rollback check, not this failure. The new preparation action took
under one second; the frozen 0.3.30 removal still took 15 seconds. Removing the
empty waits exposed process inspection racing with the old updater's exit.

Candidate 0.3.41 retained that boundary and remains unpublished even though its
native checks subsequently passed. Candidate 0.3.42 retains the same process
handle while inspecting a helper, ignores inspection errors only after confirmed
exit, and permits one kernel exit wait of up to one second for error 299. A live
or unverifiable helper still blocks relocation; access denial gets no wait.
Twelve real-process regression cases cover these boundaries, including a freshly
discovered process and exclusion of other sessions before handle acquisition. No image-read retry,
process termination or weaker account check was added.

### Final 0.3.42 local checks and unattended acceptance scope

The final source passed the complete Windows build, MSI validation and Setup
build with zero warnings/errors: 1,177 tests passed and three optional integration
tests were skipped. All 343 application tests also passed with one available CPU.
The disposable process check passed populated window rendering, simultaneous
launch, tray/show acknowledgement, host recovery and relaunch.

The owner explicitly requested unattended testing and publication, followed by
withdrawal of the previous download. Their production installation remains
untouched. Windows requires interactive consent on the secure desktop, so a
fresh 0.3.42 desktop UAC check is not included. The recorded 0.3.39 desktop UAC
result is historical evidence, not acceptance of the new helper. This owner
instruction overrides the fresh desktop participation/publication ordering in
the release procedure; all automated native gates still apply to the exact new
tagged assets. No UAC policy or upload/account protection was bypassed.

### Exact 0.3.42 native acceptance and publication (2026-10-09)

Annotated tag `v0.3.42` points to
`08b19ea8076c3d2e9c7a5bcce6a5a576c52e8f99`. Both exact-source
[CI 37858790934](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37858790934)
and [Release 37858790689](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37858790689)
passed their complete build and native acceptance checks without reruns. The
tagged run passed 1,177 tests with three optional skips and all 343 application
tests with one available CPU. The local Windows suite also passed with one CPU:
648 passed and three optional skips.

The exact tagged downloads were verified before publication. Stable and
versioned Setup are identical; all checksum sidecars name and match their files.
The portable executable reports ProductVersion
`0.3.42+08b19ea8076c3d2e9c7a5bcce6a5a576c52e8f99` and passed the isolated
populated-window/process-recovery check on this desktop.

| Accepted package | SHA-256 |
| --- | --- |
| Setup (stable and versioned) | `43D66F69C6AB4489472589529B87F71BD8D086CD52CCC808F0B392DCA691E546` |
| MSI | `D561A4F5A00AB6077DDE985979F7B9C0FB2F86058C332FF04EC2F2F3E12BD293` |
| Portable ZIP | `B1EC76A7888D6570DA3990A6A2980AA4D4D8C2AC57BFF635D02E48EE0FC285A5` |
| Installed/portable executable | `456E58FFA9352055A4605FD80C3D8E997F54816446963F66CBEE903C4ACD6B2B` |

Native receipts confirm fresh install, direct upgrade from public 0.3.39, older
Setup-owned migration, repair/removal and preserved user data, with one removable
MSI entry and no retained Setup entry. Different-account preparation blocked an
active 0.3.39 host, then succeeded after its owner shut it down, preserving
settings/cache. The actual frozen 0.3.30 updater passed default data relocation,
native rollback/retry, owner-protected credential preservation, disabled startup,
signed-out user migration and old-installation cleanup. Its enabled startup drive
did not start a host before migration. Recovery from the exact 0.3.35 baseline
authenticated its running host and preserved settings, cache and credentials
byte for byte while completing the pending move.

The current helper's real passive Setup handoff exited zero, acknowledged its
reopened window and preserved its custom data root. All three installed
executable receipts match the accepted portable hash and full source version;
their MSI/Setup hashes also match the accepted packages. Four native UI receipts
each observed one branded Setup window, progress and successful exit, with no
separate Windows Installer window. Sampling was every 50 ms and cannot exclude
shorter transient windows. New preparation took approximately zero to one second;
frozen older removal actions still took 15 to 31 seconds. Those old binaries cannot
receive the optimization retroactively. Fresh desktop UAC and a production
0.3.42 upgrade were not performed, as recorded in the unattended scope above.

[ResoDrive 0.3.42](https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.42)
was published as one final stable release using all eight accepted files and the
tag's plain-language notes. A fresh download of all eight public files matched
the accepted bytes. GitHub's anonymous latest redirect initially still returned
0.3.39 while its release metadata already marked 0.3.42 latest; after propagation,
the actual application update service discovered 0.3.42 from 0.3.39 with the
correct Setup/checksum links. Only then was the 0.3.39 download withdrawn at the
owner's request. No drafts remain; immutable 0.3.39/0.3.42 tags and other published
releases were retained. The owner's production installation remains untouched.

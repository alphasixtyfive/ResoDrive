# Installation directory migration

The 0.3.37 candidate installs to `%ProgramFiles%\ResoDrive` and uses
`%LOCALAPPDATA%\ResoDrive`. It is an increasing-version upgrade from the
accepted 0.3.30 release; published tags and accepted assets remain unchanged.

## Fresh installations

MSI schedules the four migration actions only for a major upgrade whose detected
directory-layout marker is older than `ResoDrive-v1`. Fresh installs, repair,
uninstall and later current-layout upgrades skip them. Normal application startup
checks only for the old data directory or an unfinished migration journal before
entering migration code. Fresh installs create no migration helper, journal or
scheduled migration task and show no migration prompt.

Migration is isolated in `src/ResoDrive.Windows/Migration` and
`src/ResoDrive.App/Infrastructure/Migration`. It uses the same executable already
embedded for installer preparation; there is no additional installer binary or
resident supervisor. Legacy upgrades temporarily copy that executable to the old
program path to survive the old updater's handoff. The corrected startup no
longer copies a second user-data helper or restarts a mounted app to move data.

## One-update sequence

1. Current package preparation checks the registered prior installation and new
   target. It asks the normal user's host to finish safely and rejects blocked
   uploads; no force-kill fallback exists. MSI AppSearch records the old directory
   from the published sample-profile component before native product removal.
2. Windows Installer removes the old product within its rollback transaction.
   Elevated deferred staging snapshots surviving owned startup tasks, an optional
   deployment `profiles.json`, and Windows profiles with an old default data root.
   Machine migration state is restricted to Administrators and SYSTEM.
3. MSI installs the new files. Migration retargets verified owned startup tasks
   without changing their enabled preference. It preserves deployment profiles
   and creates a temporary old-path launcher for the prior updater, which has
   the old executable path compiled into it. It also registers least-privilege
   user migration tasks for affected Windows accounts.
4. For an ordinary launch, user migration completes before normal application or
   host startup. During an old in-app update, the launcher first forwards the old
   activation request to a real visible migration startup window. That window
   holds normal host, drive and sync startup until the old update helper exits
   and data migration completes. Readiness requires a real window acknowledgement.
   Failure leaves that window open with recovery guidance; it never starts a host
   against the old root as a silent fallback.
5. A temporary SYSTEM cleanup task waits for old updater/launcher processes to
   exit, verifies the registered new MSI and removes the hash-matched old launcher
   and deployment profile. It removes the old program directory only when empty.
   Signed-out accounts migrate with their own identity at next sign-in; cleanup
   retains its retry task until those roots are gone, then removes owned tasks.

User data moves through a sibling-directory rename on the same local volume.
There is no second account/cache copy. An exclusive lease, durable journal and
directory marker allow restart after a crash between rename and path rewriting.
Settings, their backup, local recovery paths and ownership records are adjusted
only in known application-owned fields. DPAPI credentials are validated under
the account that owns them, and their encrypted bytes remain unchanged. External
job folders and remote names are preserved. The completed journal becomes a
receipt in `%LOCALAPPDATA%\ResoDriveMigration`. Migration journals and receipts
remain outside either data root; ordinary app, host and preparation logs use the
active data root. SYSTEM does not decrypt or recursively delete account data.

## Boundaries and recovery

- Independent old and new user-data roots are never merged or overwritten. Both
  remain available for investigation; startup reports the conflict.
- An unreadable or pending remote-wipe state blocks relocation. The recovery host
  can continue using the old root until cleanup is complete. No deletion HTTP
  request is introduced by migration.
- Custom `RDRIVE_DATA_DIR` roots retain their owner-selected location. This change
  targets the application-owned default `rdrive` roots, not arbitrary folders
  elsewhere whose names happen to contain that string.
- Network/device paths, junctions and symbolic links are rejected. Unknown files
  left in the old installation directory are preserved and prevent final cleanup.
- Native MSI rollback restores the previous product. Migration rollback restores
  owned task snapshots and removes only verified temporary artifacts; user-data
  relocation occurs after successful installation.
- Pipe/mutex names, compatibility environment variables, MSI/Burn UpgradeCodes
  and existing credential identities stay compatible with prior clients.

## Acceptance before release

Build and unit tests cover credential-byte preservation, cached/managed files,
owned path rewriting, conflicts, rejected shutdown, junctions and interrupted
renames. Process smoke uses isolated data. Read-only MSI AppSearch verifies legacy
directory discovery on the development machine. None replaces an actual upgrade.

Before publication, use a disposable Windows installation and an increasing
candidate version. Run exact prior-public 0.3.30 in-app updater and manual Setup
upgrades, preserving settings/cache/managed-copy hashes. Verify UAC from an
unelevated host, rollback after a deferred action failure, disabled/enabled startup
tasks, signed-out-user migration, old program/data root cleanup, repair/uninstall
and one visible removable product. Check a fresh installation and a subsequent
current-layout update for absent migration tasks and absent migration actions.
Also cover the older Setup-owned installation's required Setup transition.

Keep those exact asset hashes and MSI/helper logs. Do not publish same-version
development artifacts or bypass upload protection to obtain a passing result.

## Current candidate

The directory migration mechanism was corrected in 0.3.36. Candidate 0.3.37
adds settings, rollback and reconnect reliability fixes without changing the
migration sequence. The complete package and native gates must run against its
exact source; results for another version do not establish acceptance.

Local verification passed the full Setup/MSI/portable build with no warnings or
errors: 1,140 tests passed and three optional integrations were skipped. All 319
application tests also passed with one available CPU. Twenty-four compiled WPF
captures covered the main window, Settings and recovery messages at normal and
minimum sizes with 100%, 150% and 200% rendering. These use disposable fictional
data; they do not establish physical-monitor or production-upgrade acceptance.

[Historical acceptance records](INSTALL-DIRECTORY-ACCEPTANCE.md) retain the exact
0.3.36 source, package hashes, native results and cancelled desktop UAC check.
The owner still performs the production recovery manually. Publish only after
exact-package UAC acceptance and that recovery are confirmed, following
[the release procedure](RELEASING.md).

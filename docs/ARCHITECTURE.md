# Architecture

## Where to find a change

| Project | Owns | Project dependencies |
| --- | --- | --- |
| `ResoDrive.Core` | Domain records, validation, settings and platform contracts | None |
| `ResoDrive.Windows` | Persistence, credentials, process coordination, downloads and Windows integration | Core |
| `ResoDrive.Host` | Command handling, scheduling, status snapshots and background service lifetime | Core, Windows |
| `ResoDrive.App` | WPF windows, presentation, activation, tray and update handoff | Core, Windows, Host |

Dependencies point inward. Core does not know about WPF, Windows processes or
HTTP clients. The App references Host to run its library in `--host` mode; it does
not need a separate executable. Feature folders organize implementation while
keeping the existing namespaces stable.

Start with these files rather than the largest class:

| Question | Entry point |
| --- | --- |
| What runs on startup? | App `Program.cs`, `App.xaml.cs`, `Infrastructure/Migration/DirectoryMigrationStartup.cs` |
| What happens when the UI changes settings? | App `MainWindow.Settings.cs` and the settings-edit helpers |
| How do drive and job states reach the screen? | App `Presentation` and `MainWindow.Host.cs` |
| Who starts, reconnects or stops a drive? | Windows `Mounting/RcloneMountCoordinator` |
| Who schedules work and accepts commands? | Host `Worker` and `HostApplication` |
| How is data recovered after interruption? | Windows `Recovery`, `Configuration/AtomicSettingsStore` and `Profiles/SetupFileTransaction` |
| How does an update survive replacement? | App `Infrastructure/ApplicationUpdateHandoff`, Windows `Updates`, and `installer` |

MainWindow partials group settings, setup, components, transfers and host
interaction. Presentation types describe rows and status without owning process
lifetimes. A partial file is an organizational boundary, not an independent
service; related state still belongs to the window.

## Process model

`resodrive.exe` runs either as the WPF management/tray process or in the internal
`--host` mode that owns mount and transfer lifecycles. The two per-user processes
use a local, user-scoped named-pipe protocol. Hiding the window to the tray leaves
active work running. Explicit Exit, or closing with Minimize to tray disabled,
performs guarded shutdown. There is no separate host executable to deploy.

The host owns long-running mounts and transfers. It serializes operations per
mount, arbitrates drive targets and publishes status snapshots. The UI requests
changes through the authenticated pipe; it never infers process ownership from a
visible drive. Setup and metadata inspection can run short-lived rclone commands
through the shared process runner without taking ownership of a mounted drive.

Exit observers belong to the coordinator that starts them. Its lifetime token
cancels delayed work, and disposal drains observers before disposing shared
gates. Reconnect decisions use the current definition both after a process exit
and immediately before a delayed launch, so editing the policy takes effect.
Manual starts, delayed reconnects and automatic cache recovery have explicit
intents. Only an accepted manual start clears an explicit recovery pause; a
cancelled or rejected preflight preserves it. Scheduler interval anchors track
the initial baseline and later attempts, including failures, rather than claiming
a completed sync.

## Configuration model

- A remote is an entry in the managed, per-user rclone configuration.
- An optional `profiles.json` contains editable deployment connection metadata only.
  Without one, setup is manual; `profiles.sample.json` is documentation, not an
  active catalog. ResoDrive uses only
  its private per-user rclone runtime; it never discovers or updates system copies.
- A mount definition selects a remote, optional subpath, target, cache profile,
  restart policy, and startup policy.
- A sync job belongs to a mount definition for organization but sends its own
  folder path to the remote endpoint. The drive's subpath is never added, and the
  drive need not be mounted. Jobs on a drive with a subpath must start with `/`.
- Copy is the default transfer behavior. Mirror is destructive and requires a
  explicit confirmation before a mirror run.

Stable GUIDs identify mounts and jobs. Names, paths, and drive letters are never
used as ownership identifiers.

## Persistence

User settings, ownership state, and terminal sync outcomes are separate documents.
Writes are atomic and serialized. UI settings edits are intents evaluated against
the latest settings while holding the mutation gate; callers do not stamp an old
snapshot with a newer revision. Mount and job edits use stable IDs and preserve
fields outside the editor's scope. Removed entities are rejected rather than
silently recreated. Imported settings are validated before use, and
the active configuration is preserved before replacement. Live transfer state
remains transient, while the most recent success, failure, or cancellation for
each sync job survives a host restart. rclone structured output is normalized as
bounded newline-delimited JSON in the logs directory. Transient process state
never contaminates user configuration.

Settings can be recovered from a valid backup when the primary file is corrupt or
missing. The first repair write keeps that verified backup instead of replacing
it with a corrupt primary. Provisioning rollback checks the complete set of
required backups before restoring files; an incomplete rollback keeps the
remaining data and reports that recovery is needed.
Setup completes that recovery before presenting one combined outcome. Saving a
connection and accepting its mount request are distinct results; a missing mount
acknowledgement keeps the connection saved and directs the user to check status
before retrying.

The WPF process writes a separate rolling diagnostic log for startup, activation
and unhandled failures. Diagnostic log redaction removes common secrets, hosts
and paths. In-app activity can contain operational names, paths and cleaned
backend error text; it is not an anonymized report. Exported diagnostics use an
allowlist and omit raw activity and exception messages. Review logs before sharing
them; see [the security design](SECURITY.md).

Startup milestones are timestamped in
`%LOCALAPPDATA%\ResoDrive\logs\resodrive-ui.log`. The interval from `startup.begin`
to `startup.ready` measures normal ResoDrive initialization. A delay before
`startup.begin` can include Windows sign-in, task launch and synchronous migration
preparation. Check the migration diagnostics as well when investigating an older
installation; there is no artificial delay in the startup task.

Sync progress comes directly from rclone's structured stats events. Parsing,
bounded log storage, lifecycle coordination, and UI presentation are separate so
malformed log output can neither fail a transfer nor leak presentation concerns
into the host.

The application directory is read for binaries, assets, an optional `profiles.json`,
and the inert `profiles.sample.json` template;
Windows Installer owns the application location. Mutable per-user state and the managed rclone
runtime are kept in `%LOCALAPPDATA%\ResoDrive`. Optional startup is a current-user,
interactive Windows Task Scheduler task that launches the installed executable
with `--background`. It runs with the user's normal privileges and has no artificial
delay or network-availability gate. A normal second launch restores the existing
window; a background second launch exits silently.

## Startup and updates

During ordinary startup, default data migration finishes before the app binds
its logger, starts a host or opens drives. Pending remote wipe deliberately keeps
the old root available to its recovery host until cleanup completes. The frozen
older updater needs a visible window before its
helper exits, so a migration window acknowledges that real handoff while keeping
background work stopped. The folder move then runs under the owning account.
Fresh installations and custom data roots bypass this legacy path.

Application updates use a copied per-user helper so the coordination process
survives MSI replacement. It persists installer outcomes, launches the installed
executable after success and returns to the source executable after failure or
cancellation. The source can be a portable installation. It accepts completion only
after the normal per-installation activation pipe acknowledges a ready window.
Current updates download the versioned branded Setup and its checksum. Setup
runs MSI quietly, while legacy clients retain their existing MSI handoff until
they have been updated. Package hashes, commit, installer outcome and the reopened
window are acceptance evidence; a version label alone is insufficient.

## Rules that must stay true

- Validate complete settings before provisioning, persistence or closing an editor.
- An unavailable upload status is unknown, never evidence that uploads are finished.
- A shutdown refusal stops an update. Do not force-kill a host or broaden pipe access.
- PID, creation time, executable and Windows account establish process ownership.
- Separate account data roots are preserved; migration does not merge them.
- Background tasks have an owner, cancellation path and observed completion.
- A recovery failure preserves evidence and reports what still needs attention.

The [engineering instructions](../AGENTS.md), [input guide](UI-INPUT-VALIDATION.md),
[installer incident](INSTALLER-INCIDENT-2026-09.md) and
[remote-wipe guide](REMOTE-WIPE.md) explain the relevant test boundaries.

## Windows compatibility

The application has a technical target of Windows 10 version 1809. Supported
deployments follow Microsoft's current .NET 10 operating-system policy: Windows
11 and supported Windows 10 LTSC or Enterprise releases. Windows 11 visual
features are enabled only after runtime capability detection; Windows 10 uses a
solid WPF surface with the same layout and controls. Release packages use the
shared .NET 10 Desktop Runtime. The setup bundle downloads the pinned runtime only
when it is not already installed.

## Compatibility identities

The host pipe and mutex names, compatibility environment variable and MSI upgrade
identities retain the internal `rdrive` identity so existing clients can coordinate
safe shutdown. Program and default user-data folders use `ResoDrive`; separate
[migration modules](INSTALL-DIRECTORY-MIGRATION.md) handle the legacy paths,
encrypted configuration, startup tasks and the prior updater's old relaunch path.

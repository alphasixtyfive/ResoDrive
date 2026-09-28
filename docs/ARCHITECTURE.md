# Architecture

## Source layout

- `ResoDrive.Core` contains domain records, validation, and platform contracts.
- `ResoDrive.Windows` contains Windows integration grouped by purpose: startup,
  configuration, mounting, rclone, transfers, recovery, and updates.
- `ResoDrive.Host` owns the background command loop.
- `ResoDrive.App` contains the WPF shell, presentation models, and desktop
  infrastructure.

The projects keep one-way dependencies toward Core. Folder organization does not
change namespaces, which keeps the boundary visible without producing verbose
type names.

## Process model

`resodrive.exe` runs either as the WPF management/tray process or in the internal
`--host` mode that owns mount and transfer lifecycles. The two per-user processes
use a local, user-scoped named-pipe protocol. Closing the management window does
not interrupt active work; there is no separate host executable to deploy.

The host is the only component allowed to start or stop rclone. It serializes
operations per mount, arbitrates drive targets globally, and publishes immutable
status snapshots. The WPF process never infers ownership from a visible drive.

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
Writes are atomic and serialized. Imported settings are validated before use, and
the active configuration is preserved before replacement. Live transfer state
remains transient, while the most recent success, failure, or cancellation for
each sync job survives a host restart. rclone structured output is normalized as
bounded newline-delimited JSON in the logs directory. Transient process state
never contaminates user configuration.

The WPF process writes a separate rolling event log for startup, activation, and
unhandled failures. User-facing logs redact common secrets, credential-bearing
URLs, host names, and absolute paths before display.

Startup milestones are timestamped in
`%LOCALAPPDATA%\rdrive\logs\resodrive-ui.log`. The interval from `startup.begin`
to `startup.ready` measures ResoDrive initialization; a delay before
`startup.begin` belongs to Windows sign-in and task launch rather than drive
mounting. This distinction makes slow-start reports diagnosable without adding a
delay to the startup task itself.

Sync progress comes directly from rclone's structured stats events. Parsing,
bounded log storage, lifecycle coordination, and UI presentation are separate so
malformed log output can neither fail a transfer nor leak presentation concerns
into the host.

The application directory is read for binaries, assets, an optional `profiles.json`,
and the inert `profiles.sample.json` template;
ResoDrive never relocates itself. Mutable per-user state and the managed rclone
runtime are kept in `%LOCALAPPDATA%\rdrive`. Optional startup is a current-user,
interactive Windows Task Scheduler task that launches the installed executable
with `--background`. It runs with the user's normal privileges and has no artificial
delay or network-availability gate. A normal second launch restores the existing
window; a background second launch exits silently.

Application updates use a copied per-user helper so the coordination process
survives MSI replacement. It persists installer outcomes, relaunches the installed
executable after success, failure, or cancellation, and accepts completion only
after the normal per-installation activation pipe acknowledges a ready window.

## Windows compatibility

The application has a technical target of Windows 10 version 1809. Supported
deployments follow Microsoft's current .NET 10 operating-system policy: Windows
11 and supported Windows 10 LTSC or Enterprise releases. Windows 11 visual
features are enabled only after runtime capability detection; Windows 10 uses a
solid WPF surface with the same layout and controls. Release packages use the
shared .NET 10 Desktop Runtime. The setup bundle downloads the pinned runtime only
when it is not already installed.

## Compatibility identities

The user-data directory, host pipe and mutex names, and MSI installation directory
use the internal `rdrive` identity. They are stable machine-facing identifiers,
not user-facing product copy. Renaming them would orphan encrypted configuration,
allow competing host instances, or break in-place MSI upgrades.

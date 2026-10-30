# Uploads and shutdown protection

Explorer can finish copying to a mounted drive before rclone sends the cached
file to its server. Reducing rclone's write-back delay does not make Explorer's
progress bar reliably measure server acceptance on Windows. ResoDrive keeps
cached writes responsive and exposes their separate upload status.

Left-click the tray icon to open the compact ResoDrive popup above the taskbar on
that display, including when uploads have finished. Right-click and choose
**Open ResoDrive**, or use the popup's footer, to open the main window or Settings.
The popup opens on request, with a
consistent narrow width and a height that follows the queue. A short queue has
no scrollbar or reserved scrollbar gap. Escape, the dismiss button, or clicking
elsewhere hides the flyout while transfers continue. It prioritizes active files,
shows progress and speed, and
keeps long queues summarized. Routine completion restores the normal icon without
a notification. The **Transfers** entry also remains available from the tray menu.

The Transfers flyout combines the VFS queue, active rclone transfers, and dirty
cache metadata. Modified files held open are shown as waiting for close. Reaching
100% transferred bytes still requires server confirmation. Details are bounded;
the total pending-work check includes entries beyond the displayed list. An
incomplete inspection remains unknown and blocks a normal disconnect or shutdown.
An ordinary cache refresh can retain a measured upload's byte progress for up to
ten seconds, labelled **Updating status**, while hiding its old speed. This never
authorizes disconnect or shutdown. A lost host/control connection or an expired
measurement hides old progress and speeds. Changes on one drive do not reset
every other drive's observations. When the server does not
report a file size, transferred bytes and speed remain visible without a percentage bar.

Brief connection problems update the popup and tray without an error balloon.
After thirty seconds of continued trouble, one amber notification opens Transfers.
Five minutes of confirmed healthy observations rearms it; suspension and missing
observations do not count as recovery. Cache-recovery and Windows power-protection
failures bypass the delay and can escalate a previously reported connection issue.

Active sync jobs share the same list, identified by drive and job name. Their
copy or mirror direction, file counts, progress, speed and remaining time appear
when available. Queued and checking jobs remain visible without an invented
percentage. Finished jobs disappear; their results remain in the main Sync page.
Sync-only work also animates the tray icon. Unavailable observations hide old
counters, speeds and progress, and a short summary covers active jobs omitted
from a bounded host response.
After a running sync has transferred data, a retry or missing counters says
**Waiting for transfer progress** rather than returning to **Preparing files**.
Empty or malformed rclone statistics cannot reset genuine progress to zero.

Mount/disconnect operations and long sync jobs have separate bounded concurrency,
so two long sync jobs cannot indefinitely occupy the drive-start queue. A drive
that cannot leave that queue within two minutes reports a retryable failure.
Cancellation and failed preflight also leave the queued state. Initial mount
readiness has a forty-five-second allowance; a surviving process that is still
not ready continues to be checked without being killed. Verified idle caches
are inspected every five seconds, with immediate invalidation on metadata changes;
active or uncertain uploads continue to be checked on each health tick.

## rclone component updates

Normal updates stage and verify the official stable executable, then replace the
managed executable atomically. Existing drives and syncs keep running their loaded
engine, and their next connection/run uses the new one. Windows may retain the old
image's backup until those processes exit; a later component check removes it.
The updater verifies the committed executable and retains its rollback behavior.
It does not disconnect a stalled upload or treat zero speed as a safe-stop verdict.
Repair still requires stopped work because it removes the damaged runtime.
Refreshing rclone after a normal update preserves deliberately disconnected drives.
Initial installation and repair explicitly activate eligible startup drives.

## Waiting for a connection

An unreachable configured WebDAV/SFTP server is an expected waiting condition.
The drive shows **Waiting for connection** with its existing **Stop** action;
there are no VPN profiles, extra switches or new screens. ResoDrive checks the
configured destination and port with a bounded five-second connection check, then
backs off through 5, 10, 20 and 30 seconds. Network changes, including a VPN
connecting, prompt another check after a short settling period. The existing
monitor also rechecks after a long polling gap such as laptop sleep/resume.

A deliberate Stop cancels that desired connection, including retries already
queued behind other drives. A clean drive that never started does not keep the
computer awake or prevent an ordinary exit while it waits. Pending or unverified
cached uploads retain their normal protection. Cache identity conflicts remain
actionable; they are not hidden as ordinary network waits.

The connection check is used only where it can follow the managed server's
transport. Proxy, custom SSH/bind/environment settings and unsupported metadata
keep the engine's existing connection behaviour. A successful TCP check does not
validate credentials or certificates: those errors continue through rclone's
existing diagnostics and retry policy. Live mounted drives retain their existing
upload retries through network loss; they are not disconnected or replaced to
change their status label. ResoDrive does not start or configure a VPN.

See the [reliability review and verification record](TRANSFER-RELIABILITY.md).

For a deliberate exit or disconnect, the warning offers **Exit anyway** or
**Disconnect anyway**. **Keep running** or **Keep connected** is the default;
Escape and closing the warning cancel the action. Continuing pauses uploads and
keeps cached files and recovery records for reconnection. Open documents should
be closed first; work held only in another application's memory is not cached.
This choice applies to that action only. Installer, update, restart, and takeover
checks still require a verified safe state. A disconnected drive stays
disconnected during settings reload and can resume when connected again.

The host owns a hidden Windows session window, so shutdown protection continues
when the main window is hidden. Its shutdown reason and automatic-sleep request
are cleared when observed work finishes. This does not prevent manual sleep,
closing a laptop lid, power loss, or Windows' **Shut down anyway** action. A client
cannot establish a remote server's physical disk durability from an HTTP response.

Mount launch intents are stored before starting rclone and retained until a
verified clean disconnect. Restart uses the same remote and cache identity.
Changed, disabled, or deleted drive definitions need attention instead of silently
discarding old cached files. Do not delete recovery records or cached files to
clear a warning. Unverifiable surviving processes are reported rather than killed.
Overlapping paths on the same remote cannot be connected together because rclone
shares their cache. Disjoint folders can connect separately. Recovery verifies
the selected protected remote configuration, so adding an unrelated connection
does not invalidate an existing drive's recovery record.

## Desktop acceptance before release

Use disposable local files and an isolated ResoDrive data directory. Record the
build hash, Windows version, rclone version, and WinFsp version.

1. Copy a large file through Explorer on a slow connection. Confirm that Explorer
   can finish first while the Transfers popup and tray still show pending work.
2. Keep a modified document open. Confirm the waiting-for-close state and normal
   exit rejection; close it and wait for server confirmation.
3. Test disconnect/reconnect and retry behavior. An unavailable status must never
   become a clean status merely because a request timed out. Verify both keeping
   the connection and deliberately continuing, then compare recovered contents.
4. Interrupt the disposable host while a file is pending, restart it, and compare
   the uploaded contents with the original. Repeat with a manually mounted drive.
5. Reorder drives by dragging their handles, Move up/down, and Alt+Up/Down. Confirm
   the saved order survives reopening without disconnecting the drives.
6. Inspect the tray animation with Windows animations enabled and disabled, and
   the windows at 100%, 150%, and 200% display scaling. Open the flyout on each
   monitor, let its queue grow and shrink, and check its position above the taskbar.
   Confirm that Escape, dismiss and clicking elsewhere hide it without stopping
   transfers; reopening should show current progress.
   Run a sync alongside an upload, then by itself, and check queued, checking,
   downloading and completed states in the same popup.
7. Test a real shutdown/sign-out manually on the test PC with pending uploads and
   after they finish. Automated tests send session messages only to disposable
   guard windows; they do not shut down the machine.
8. Complete the existing installer/UAC and prior-version upgrade acceptance checks
   before creating or publishing a release. Local unit tests and a portable build
   do not establish that an installed upgrade works.

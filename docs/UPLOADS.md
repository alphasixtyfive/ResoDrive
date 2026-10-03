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
elsewhere hides the flyout while uploads continue. It prioritizes active files,
shows progress and speed, and
keeps long queues summarized. Routine completion restores the normal icon without
a notification. The Uploads entry also remains available from the tray menu.

The Uploads flyout combines the VFS queue, active rclone transfers, and dirty
cache metadata. Modified files held open are shown as waiting for close. Reaching
100% transferred bytes still requires server confirmation. Details are bounded;
the total pending-work check includes entries beyond the displayed list. An
incomplete inspection remains unknown and blocks a normal disconnect or shutdown.
Unavailable observations hide old progress and speeds. When the server does not
report a file size, transferred bytes and speed remain visible without a percentage bar.

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
   can finish first while the Uploads window and tray still show pending work.
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
7. Test a real shutdown/sign-out manually on the test PC with pending uploads and
   after they finish. Automated tests send session messages only to disposable
   guard windows; they do not shut down the machine.
8. Complete the existing installer/UAC and prior-version upgrade acceptance checks
   before creating or publishing a release. Local unit tests and a portable build
   do not establish that an installed upgrade works.

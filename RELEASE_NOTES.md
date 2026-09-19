ResoDrive 0.3.7 introduces a compact native Windows setup with the ResoDrive icon and logo, clear progress, repair and removal screens, and an Open ResoDrive button.

- Setup provides a direct link to its log when an installation fails.
- Uninstall now asks the background host to stop before removing the application. Process shutdown is scoped to the installed copy and has a bounded fallback wait.
- Application updates require the exact versioned installer URL and a checksum naming that installer. Downloads remain resumable and are verified again before installation.
- Cancel a ResoDrive update download and resume it later from Settings.
- Fix rclone's Cancel button during active component operations, align both download progress bars, and keep cache selectors aligned when labels wrap.
- Mounted drives show queued uploads, active uploads and cache problems. Disconnect, reconnect, exit and update preparation check for reported pending uploads and cache errors before stopping work.
- Drive readiness is rechecked after slow starts and interruptions. A recovered live mount is preserved while its readiness is checked.
- Drive settings explain how local and network mode affect Windows and temporary files.
- Settings can export a diagnostic report containing versions, numeric performance settings, states and recent UI error references. Connection names, addresses, paths, credentials and raw logs are omitted.
- Release checks now install, launch, repair, upgrade and remove the app on disposable Windows runners, including checks that settings and cached data survive.

Download `ResoDrive-Setup.exe` for normal installation. The MSI remains available for administrators and the in-app updater. Existing drive settings and credentials are preserved; no manual settings changes are needed for this release. Close documents opened from mounted drives before installing or removing ResoDrive.

Upload statistics are snapshots of rclone's disk cache, not a guarantee that open documents have been saved. If statistics cannot be read (including recovered processes or cache-off mounts), ResoDrive displays an unavailable status. A responsive mount does not prove that its server is reachable. Windows shutdown, process crashes and older installed versions cannot provide the new upload guard. Cached data remains on disk for recovery; this release does not implement remote wipe.

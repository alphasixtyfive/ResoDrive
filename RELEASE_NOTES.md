ResoDrive 0.3.7 is the consolidated release containing the installer, update, safety and remote-wipe fixes.

- Nextcloud accounts configured with a dedicated app password are checked only after a 401/403 response. Local account state is wiped only after Nextcloud explicitly returns `{"wipe":true}`.
- Remote-wipe probes and acknowledgements accept only same-host HTTPS endpoints recorded by setup.
- The client stops mounts and syncs, removes settings, encrypted configuration, cache, scheduler state, ownership state and logs, then acknowledges `/index.php/core/wipe/success`.
- Setup explains that a dedicated Nextcloud app password is required. Existing accounts must be reconnected through setup to register remote wipe.
- Update preparation now reports upload/cache shutdown blocks before Windows Installer starts.
- Fix the confirmed immediate 1603/1722 failure when an elevated installer contacts an unelevated 0.3.6 host. The MSI now carries its own preparation helper and authenticates the host's actual Windows account across UAC elevation.
- Show preparation stages and specific shutdown failures. Keep pending-upload protection and preserve settings and cache if preparation fails.
- Keep idle drive rows compact. Show uploads beside the domain only while files are queued or uploading; retain cache errors when attention is needed.
- Pass custom data directories explicitly to Windows Installer, and repair the app without repairing the shared .NET runtime.
- The setup includes the ResoDrive icon and logo, clear progress, repair/removal screens, a launch action and a direct failure-log link.

- Setup provides a direct link to its log when an installation fails.
- Uninstall now asks the background host to stop before removing the application. Process shutdown is scoped to the installed copy and session; the host is allowed to finish cleanly within a bounded wait.
- Application updates require the exact versioned installer URL and a checksum naming that installer. Downloads remain resumable and are verified again before installation.
- Cancel a ResoDrive update download and resume it later from Settings.
- Fix rclone's Cancel button during active component operations, align both download progress bars, and keep cache selectors aligned when labels wrap.
- Mounted drives show queued uploads, active uploads and cache problems. Disconnect, reconnect, exit and update preparation check for reported pending uploads and cache errors before stopping work.
- Drive readiness is rechecked after slow starts and interruptions. A recovered live mount is preserved while its readiness is checked.
- Drive settings explain how local and network mode affect Windows and temporary files.
- Settings can export a diagnostic report containing versions, numeric performance settings, states and recent UI error references. Connection names, addresses, paths, credentials and raw logs are omitted.
- Release checks now install, launch, repair, upgrade and remove the app on disposable Windows runners, including checks that settings and cached data survive.

Download `ResoDrive-Setup.exe` for normal installation. The MSI remains available for administrators and the in-app updater. Existing drive settings and credentials are preserved; no manual settings changes are needed for this release. Close documents opened from mounted drives before installing or removing ResoDrive.

Upload statistics are snapshots of rclone's disk cache, not a guarantee that open documents have been saved. If statistics cannot be read (including recovered processes or cache-off mounts), ResoDrive displays an unavailable status. A responsive mount does not prove that its server is reachable. Windows shutdown, process crashes and older installed versions cannot provide the new upload guard.

This replaces the withdrawn 0.3.7 packages. If an earlier 0.3.7 build is already installed, run the new setup manually: the update checker cannot treat the same version number as newer. Normal upgrades from 0.3.6 remain supported.

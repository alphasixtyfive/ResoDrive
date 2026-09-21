# Changelog

## Unreleased

- Refresh the reported rclone version after the managed engine is installed, repaired or updated, including during first-time setup.

## [0.3.12] - 2026-09-21

- Include the verified bundled rclone version in ResoDrive's storage User-Agent so administrators can distinguish the client and storage engine versions.
- Use the same client identity for mounts, sync jobs, setup checks and Nextcloud remote-wipe requests.

## [0.3.11] - 2026-09-21

- Enable remote wipe for existing Nextcloud app-password connections after updating, without asking users to reconnect.
- Reuse the saved connection details and check for pending wipes before automatic drives start.
- Show remote-wipe setup status beside each drive.
- Replace the 0.3.10 downloads with this version, which also covers existing connections.

## [0.3.10] - 2026-09-21

- Add managed download folders for enrolled Nextcloud accounts. Remote wipe covers these folders even after their sync jobs are removed.
- Use managed folders by default for new enrolled download jobs. Existing external folders and upload originals remain outside wipe coverage.
- Prevent managed folders from being used for uploads, shared between jobs or redirected to another location.
- Keep a wipe pending until all managed files and account data can be removed, including after a crash or restart.
- Wait for a leftover ResoDrive transfer process to exit before completing a wipe after a crash.

## [0.3.9] - 2026-09-21

- Include the ResoDrive version, Windows edition, release, build and architecture in storage requests' User-Agent header.
- Reject unclear Nextcloud wipe responses and keep browser session cookies out of wipe requests.
- Fix wipe recovery for large account lists and protect newly connected accounts from an older wipe recovery attempt.
- Include scheduler data and its backups in remote wipe.
- Show when local cleanup is complete but confirmation to Nextcloud is still pending. Resume cleanup of locked files after a restart.
- Add an administrator guide for enabling, requesting and recovering from Nextcloud remote wipe.

## [0.3.8] - 2026-09-19

- Make the About page easier to read, with a larger icon, clearer version information and keyboard-accessible links.
- Resume interrupted Nextcloud wipes before reconnecting accounts.
- Include settings backups and setup recovery files in cleanup. Keep wipes pending while files are locked or the cache folder points elsewhere.
- Prevent an open app window or setup recovery from restoring account data after a wipe, and remove the stored wipe token once Nextcloud confirms completion.
- Handle slow or oversized wipe responses, check the server's HTTPS port, preserve Nextcloud subdirectory addresses and support larger account lists.

## [0.3.7] - 2026-09-19

- Keep idle drive rows compact and show active or queued uploads beside the domain.
- Pass custom data directories explicitly through the installer and avoid repairing the shared .NET runtime when repairing the app.
- Fix elevated-installer communication with an unelevated legacy host by authenticating its actual Windows account SID. Embed current preparation code in the MSI instead of invoking the old executable.
- Show installer preparation progress and explain shutdown failures.
- Add guarded Nextcloud remote wipe using a DPAPI-protected dedicated app password, same-host HTTPS validation, and the official wipe-check and wipe-success protocol.
- Remove local ResoDrive settings, encrypted configuration, shared cache, scheduler state, ownership state and logs only after the server explicitly returns `wipe: true` following a 401/403 account response.
- Run coordinated application shutdown before Windows Installer checks for files in use, while keeping the upload-safety block when managed work cannot be drained.
- Refresh the latest-release redirect before every update action and send no-cache headers so failed handoffs cannot keep an older version stuck in the UI.
- Add a branded native setup with progress, repair/removal, launch and failure-log actions.
- Stop the installed application during uninstall as well as upgrades, with bounded process waits and preserved user data.
- Bind update downloads and checksum filenames to the exact selected version.
- Show queued and active uploads, cache errors, and unavailable upload status on mounted drives; check again before stopping, reconnecting, exiting, or updating.
- Recheck drive readiness after slow starts and interruptions without killing a recovered live process after a single failed probe.
- Add cancellation to ResoDrive update downloads and clarify local/network mode beside the drive selector.
- Export a privacy-filtered diagnostic report with versions, performance options, mount states and recent UI error references.
- Keep upload warnings when control communication fails, fix rclone cancellation during active updates, and align download progress and cache selectors.

## [0.3.6] - 2026-09-10

- Use the same cache mode, size target, and retention controls when adding and editing drives.
- Preserve existing cache modes and inherited defaults when upgrading or editing unrelated settings; new drives explicitly enable read and write caching.
- Support custom cache values and show the configured performance options before saving.
- Validate cache and performance option values before starting rclone, and support advanced read-ahead, chunk streams, chunk limits, and minimum free cache space.
- Keep deployment cache options in the dedicated controls instead of duplicating them in advanced text.

## [0.3.5] - 2026-08-28

- Use the same settings gear for drives and sync jobs, with clearer tooltips and screen-reader labels.

## [0.3.4] - 2026-08-28

- Allow settings for an unmounted drive to be saved while unrelated drives remain mounted.
- Ask before briefly reconnecting a mounted drive whose connection settings changed.
- Keep unrelated mounts running and never interrupt an active or queued sync to apply settings.
- Preserve the real drive states after a rejected settings change instead of briefly showing every drive as unmounted.
- Remove a race that could prevent a mounted drive from being deleted cleanly.

## [0.3.3] - 2026-08-28

- Do not abort an upgrade when optional graceful-shutdown preparation fails.
- Wait for path-matched installed processes to exit before replacing files.
- Treat an already-stopped application as a successful installer no-op.
- Include the MSI log path in failed application-update diagnostics.
- Keep fixed header and footer actions aligned with scrollable content whenever
  a vertical scrollbar appears across the main pages and editor windows.

## [0.3.2] - 2026-08-28

- Keep the per-user startup task registered when Windows normalizes its saved
  user identity and default privilege fields.
- Avoid visible PowerShell windows while an installer upgrade stops ResoDrive.

## [0.3.1] - 2026-08-28

- Prevent a crash when restoring a maximized window after background startup.
- Contain notification-area callback failures so a single UI action cannot
  terminate the application.
- Log fatal early-startup failures with an error ID and show a useful error
  message instead of exiting silently.

## [0.3.0] - 2026-08-28

First public preview of ResoDrive.

- Mount Nextcloud, WebDAV, and SFTP storage as Windows drives.
- Run copy and mirror jobs independently of mounted drive availability.
- Keep active work in a per-user background host when the window is closed.
- Start promptly at sign-in through Windows Task Scheduler when enabled.
- Retry interrupted mounts with bounded backoff for unreliable links.
- Resume application and rclone downloads after transient connection failures.
- Encrypt managed rclone credentials with Windows CurrentUser DPAPI.
- Install through a small setup bundle that downloads the .NET Desktop Runtime
  only when it is missing.

[0.3.12]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.12
[0.3.11]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.11
[0.3.10]: https://github.com/alphasixtyfive/ResoDrive/tree/v0.3.10
[0.3.9]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.9
[0.3.8]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.8
[0.3.7]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.7
[0.3.6]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.6
[0.3.5]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.5
[0.3.4]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.4
[0.3.3]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.3
[0.3.2]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.2
[0.3.1]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.1
[0.3.0]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.0

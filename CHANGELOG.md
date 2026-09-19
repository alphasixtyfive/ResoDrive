# Changelog

## [0.3.10] - 2026-09-19

- Validate remote-wipe registrations as same-host HTTPS endpoints before any probe or acknowledgement request.

## [0.3.9] - 2026-09-19

- Add guarded Nextcloud remote wipe support using a DPAPI-protected dedicated app password and the official wipe-check and wipe-success protocol.
- Remove local ResoDrive settings, encrypted configuration, shared cache, scheduler state, ownership state and logs only after the server explicitly returns `wipe: true` following a 401/403 account response.
- Explain the dedicated app-password requirement in setup and report update shutdown blocks before starting Windows Installer.

## [0.3.8] - 2026-09-19

- Run the coordinated application shutdown before Windows Installer checks for files in use, so a stale background process no longer causes an avoidable upgrade failure with MSI error 1603.
- Keep the upload-safety check blocking when managed work cannot be drained.

## [0.3.7] - 2026-09-19

- Add a branded native setup with progress, repair/removal, launch and failure-log actions.
- Stop the installed application during uninstall as well as upgrades, with bounded process waits and preserved user data.
- Bind update downloads and checksum filenames to the exact selected version.
- Verify installation, repair, previous-version upgrade, running-app removal and settings/cache preservation in Windows CI before release.
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

First public preview of the cleaned ResoDrive codebase.

- Mount Nextcloud, WebDAV, and SFTP storage as Windows drives.
- Run copy and mirror jobs independently of mounted drive availability.
- Keep active work in a per-user background host when the window is closed.
- Start promptly at sign-in through Windows Task Scheduler when enabled.
- Retry interrupted mounts with bounded backoff for unreliable links.
- Resume application and rclone downloads after transient connection failures.
- Encrypt managed rclone credentials with Windows CurrentUser DPAPI.
- Install through a small setup bundle that downloads the .NET Desktop Runtime
  only when it is missing.

[0.3.7]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.7
[0.3.8]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.8
[0.3.9]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.9
[0.3.10]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.10
[0.3.6]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.6
[0.3.5]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.5
[0.3.4]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.4
[0.3.3]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.3
[0.3.2]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.2
[0.3.1]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.1
[0.3.0]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.0

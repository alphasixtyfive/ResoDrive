# Changelog

## [0.3.31] - 2026-10-06 (unpublished acceptance build)

- Show a standard Windows error dialog for unexpected startup and process failures, with Copy details and local diagnostic evidence. Normal startup has no extra window.
- Configure bounded Windows Error Reporting full dumps for installed copies, retain matching build symbols separately, and record guarded UI and host diagnostics.
- Distinguish activation timeouts, handled startup failures and normal shutdown; make fatal host background-service failures observable.
- Install fresh copies in `%ProgramFiles%\ResoDrive`; retain legacy updater compatibility and use Windows Installer for staged migration from `rdrive`.
- Preserve account settings, credentials, pending-upload cache and process ownership across installation-folder changes.
- Keep error details compact on screen while copying the complete available report; reject redirected diagnostic and installed-executable paths before access.
- Refresh the missing-prerequisite Setup payload to .NET Desktop Runtime 10.0.12; retain normal same-major runtime selection and CET protection.
- Provide a separately verified CET compatibility package using Microsoft's documented opt-out; preserve its selection in later updates and explain the reduced protection in Setup.

## [0.3.30] - 2026-10-05

- Combine the reviewed drive settings, quieter status and compact log changes with the input-validation and single-progress-window fixes.
- Make the MSI the sole removable Windows app entry. Setup installs prerequisites and the app without retaining a second entry; migrate older Setup installations through one native Setup upgrade.
- Direct older Setup-owned installations to Setup before an MSI-only update can create duplicate entries. Keep repair, removal and upload protection intact.
- Update the test coverage collector to 10.1.0 and refresh all affected lock files.
- Align sidebar icons consistently with their labels, including Settings.

## [0.3.29] - 2026-10-05 (unpublished acceptance build)

- Pass the full native installer gate and desktop upgrade checks. Native Setup removes the older duplicate entry while preserving the accepted MSI and account files.

## [0.3.28] - 2026-10-05 (unpublished acceptance build)

- Validate native installer ownership and older Setup migration. The custom-root repair check exposed that Windows ignores property arguments with `/f`; correct the gate to use reinstall maintenance before publication.

## [0.3.27] - 2026-10-05 (unpublished acceptance build)

- Bring together the drive settings, status and log improvements from withdrawn 0.3.25 and the input-validation and installer fixes reviewed in local 0.3.26.
- Publish with a newer version so both earlier public installations and the local test build can upgrade normally.

## [0.3.26] - 2026-10-05 (local review build)

- Replace text-based Unlimited attempts with an explicit checkbox and a validated numeric limit; keep drafts when toggling and show inline errors.
- Validate setup before connecting and all editor candidates before closing; reject missing modes, duplicate names, malformed paths, ports and control characters.
- Preserve disabled sync intervals and custom cache values; validate sizes, durations and bandwidth options without silently clamping input.
- Bound inline errors, retain them when Advanced changes, and keep controls and buttons visible at small window sizes and larger display scales.
- Let the installer own routine update/install progress, removing the duplicate preparation window while retaining upload blocking and its specific failure message.
- Add input regression tests and an engineering guide requiring validation and compiled UI review before publication. Correct the withdrawn 0.3.25 release.

## [0.3.25] - 2026-10-05 (withdrawn)

- Keep routine upload checks quiet while showing detected transfers, pending files and errors.
- Show the configured server address and port in a clearly disabled drive setting.
- Simplify caching labels and hints, add space around form fields, and align Advanced headings.
- Avoid conflicting drive-letter choices and folder edits during setup; improve focus and truncated-text tooltips.
- Open drive settings immediately with a compact loading message; keep field positions stable and Cancel available.
- Use Fluent-style expanders and consistent disabled controls; preserve custom cache values while typing.
- Give drives a separate activity row that stays hidden during idle checks and retains meaningful warnings; keep drag insertion markers aligned when rows change height.
- Show informative log entries directly, with timestamps, severity and wrapped event text; record observed drive outcomes and component updates, suppress repeated polling failures and sort sync results by their actual time.
- Remove the duplicate performance summary, oversized log tooltips and event popups; keep the interface compact and straightforward.

## [0.3.20] - 2026-10-03

- Show active sync jobs alongside uploads in the compact tray popup, with direction, file counts, progress, speed and remaining time. Keep queued jobs visible and remove completed jobs.
- Animate the tray icon for sync jobs as well as uploads. Rename the tray menu entry to Transfers and keep the same small footer.
- Hide old sync counters when the host disconnects, and keep status accurate after a sync starts, stops or settings reload. Handle incomplete host responses without reporting unfinished work as complete.
- Use transfer wording in the Windows shutdown warning so it covers both uploads and downloads.

## [0.3.19] - 2026-10-03

- Click the tray icon to see a compact upload progress popup with Open ResoDrive and Settings in its footer. It sizes to the queue, keeps scrolling out of the way, and shows when a file is waiting to close or retry.
- Keep pending uploads visible after Explorer finishes copying. Preserve cached files when a drive disconnects or ResoDrive exits, and recover interrupted uploads with their original connection.
- Warn before exit, disconnect, sleep or shutdown interrupts pending work. Exit and disconnect offer an explicit choice to pause uploads; updates still require a verified clear queue.
- Reorder drives with a subtle drag handle, a faint moving row, keyboard shortcuts or a small context menu, and keep the chosen order after restarting. Keep status strips inside their rounded drive and sync cards at different display scales.
- Prevent background recovery from restarting the host during exit or update installation; finish accepted shutdown even if its client disconnects.
- Let the updater finish recording its result while the reopened app reads it, and recover a finalized receipt left by an earlier helper.
- Improve large-queue handling, cancelled process launches, atomic settings saves and temporary-file cleanup. Show unavailable sync status when the host disconnects.
- Reject nested junctions in download destinations before starting a sync, and make destination validation cancellable.
- Allow the revised .19 Setup to replace the earlier .19 installation while preserving user data. Existing .19 users need to run Setup manually because the version number is unchanged.

## [0.3.18] - 2026-09-28

- Let sync jobs address storage connection paths independently of the mounted drive folder, and show those paths on the Sync page.
- Require full remote paths for jobs on drives mounted inside a folder, preventing unintended transfers after an upgrade.
- Clarify managed local copies and Nextcloud remote-wipe boundaries in the sync editor.

## [0.3.17] - 2026-09-25

- For updates started from 0.3.17, finish closing the signed-in user's ResoDrive background work before asking for administrator credentials.
- Let Setup wait for a previous update's ResoDrive process to exit when Windows runs it under a different administrator account; keep installation blocked if work remains or the account cannot be verified.
- Explain the administrator-password requirement and make cross-account setup errors more specific.

## [0.3.16] - 2026-09-25

- Show a pending drive action while automatic mounts are being prepared at startup, then use the background host's reported state.
- Remove the misleading “detected” label from the right side of the rclone row in Settings. Host version checks remain in diagnostics.

## [0.3.15] - 2026-09-25

- Let Setup close a verified leftover ResoDrive window when the background host is absent, and retry a slow host before stopping an upgrade.
- Keep installation blocked when a running host, rclone work, or process identity cannot be verified.
- Allow an unrelated rclone process to keep running during an upgrade only when its own live ResoDrive host can be verified; other unverified work still blocks setup.
- Retry the background host's rclone version check after a startup failure so later storage requests can report the engine without restarting ResoDrive.
- Keep the rclone status on one line in Settings, and record the host's detected version and inspection error code in diagnostic exports alongside the UI component version.
- Recognize safe development and beta rclone version suffixes in the storage identity.

## [0.3.14] - 2026-09-23

- Remove remote-wipe setup labels from drive cards and clarify managed-copy guidance.
- Keep the upload-status warning visible when statistics are unavailable, including when previous counts were zero or unknown.
- Apply the same sync action availability rules in the tray and main window.

## [0.3.13] - 2026-09-21

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

[0.3.16]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.16
[0.3.15]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.15
[0.3.14]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.14
[0.3.13]: https://github.com/alphasixtyfive/ResoDrive/releases/tag/v0.3.13
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

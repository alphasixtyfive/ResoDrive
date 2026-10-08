This update fixes an incomplete move of existing app data after an upgrade.

- Existing installations move to the current ResoDrive folders automatically, preserving accounts, settings, cached files and local copies.
- Drives and sync jobs wait until the move finishes. If a file cannot be moved, ResoDrive explains the problem and keeps your data for a retry.
- Future in-app updates use the branded Setup window, with Windows Installer running quietly behind it.
- Update downloads detect a stalled connection sooner and keep partial downloads so you can resume. Retrying an installation reuses the verified download, and failures give clearer steps to recover.
- The refresh button stays available when an update is shown, so you can check for a newer release.
- A drive that stays offline reports the problem once. Brief reconnects and repeated retries no longer fill the log or repeat the same warning.
- Failed scheduled syncs stay quiet until the job has recovered. Transfer progress and new problems still appear as they happen.
- Changes to reconnect settings take effect even while a drive is waiting to retry.
- Saving drive or sync settings preserves other changes made while the editor was open. Failed saves explain when startup or background settings need attention.
- Settings recovery keeps the last working backup. Interrupted setup preserves the remaining files when automatic recovery cannot finish.
- Setup finishes recovery before showing an error and explains when a saved connection could not be mounted.
- The Transfers window refreshes more efficiently and shows when sync status is unavailable.
- Diagnostic reports include the app version and more useful details when background work stops unexpectedly.

Version 0.3.35 was withdrawn after an upgrade could leave existing app data in its previous location. Download **ResoDrive-Setup.exe** below and run it manually for this update. Let uploads finish and close documents opened from ResoDrive first. Your settings and files are preserved.

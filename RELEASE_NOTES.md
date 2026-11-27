WinFsp status now updates automatically after installation, and rclone updates handle temporary file locks more patiently.

- Settings and drive setup check again when you return to the app. Open forms also refresh in the background without changing what you have entered.
- Fixed a race where an rclone operation could leave the WinFsp check unfinished.
- Drive setup waits until its controls are ready before accepting input, so loading cannot overwrite what you type.
- Connection profiles use their preferred drive letter when it is free, and keep a letter you have already chosen.
- Deployment tools can now add connection templates through ResoDrive, using the correct data folder and preserving existing settings.

Update from Settings, or download **ResoDrive-Setup.exe** below. A portable ZIP is also available.

This release is unsigned. Windows App Control may still require your organization's approval.

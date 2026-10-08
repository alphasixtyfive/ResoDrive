This update removes unnecessary waits when preparing ResoDrive for an update.

It replaces the previous download with faster update preparation and a fix for interrupted migration.

- Setup checks each installation once, even when Windows supplies the same folder in different forms.
- Preparation continues promptly when the background host has already stopped. Account verification and protection for pending uploads remain in place.
- Existing installations can finish moving app data when the previous updater exits during the handoff.
- In-app updates from 0.3.39 onward use the branded ResoDrive Setup window.

Older installed updaters can still display Windows Installer progress or take longer to close. To open branded Setup directly for this update, download **ResoDrive-Setup.exe** below and run it manually. Let uploads finish and close documents opened from ResoDrive first. Your accounts, settings and files are preserved.

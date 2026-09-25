ResoDrive 0.3.15 makes updates more reliable when an old ResoDrive window is still open after its background host has stopped. Setup checks for active storage work before closing that window. If uploads are still running or the process cannot be verified, it asks you to try again once the work is finished. Your settings and cache stay in place.

ResoDrive also retries the rclone version check after a temporary startup failure. Settings shows the version detected by the background host, and the rclone status now fits on one line. A drive that was already mounted before detection recovered may need a safe reconnect before its server sees the version.

Download **ResoDrive-Setup.exe** below, or check for updates in ResoDrive Settings.

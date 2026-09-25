ResoDrive 0.3.17 improves updates on PCs where the person using ResoDrive needs a separate administrator password to install software. Once this version is installed, the app waits for its drives and background work to stop before asking Windows to start Setup. During an upgrade from an older version, Setup gives the old process time to finish closing.

If work is still active or Setup cannot verify who owns a process, the update stops and leaves settings and cached files in place. The messages now explain what to close and why Windows needs an administrator account.

An update started from an older version may still report that ResoDrive is running under a different Windows account. If that happens, let uploads finish, choose **Exit** from ResoDrive's tray icon while signed in as the usual user, then run **ResoDrive-Setup.exe** below and enter the administrator password. Do not end ResoDrive or rclone in Task Manager while transfers may be active.

Download **ResoDrive-Setup.exe** below, or check for updates in ResoDrive Settings.

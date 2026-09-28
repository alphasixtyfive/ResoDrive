ResoDrive 0.3.18 lets sync jobs use paths from the storage connection independently of the mounted drive folder. For example, a drive mounted at `/Armeria` can copy `/Fleet Reference` to a local folder. The Sync page now shows the connection path used by each job.

**Check existing sync jobs before upgrading:** Jobs on drives mounted inside a folder may now point somewhere different. Set **Remote folder** to the full path beginning with `/`. Jobs with an empty or relative path on these drives will stop until you update them, preventing an unintended transfer or mirror.

The sync editor now explains absolute paths and managed local storage more clearly. A confirmed Nextcloud remote wipe deletes files in ResoDrive's current managed folder; external local folders stay outside it.

On a standard-user PC, an update started from an older version may still report that ResoDrive is running under another Windows account. Let uploads finish, choose **Exit** from ResoDrive's tray icon while signed in as the usual user, then run **ResoDrive-Setup.exe** below and enter the administrator password.

Download **ResoDrive-Setup.exe** below, or check for updates in ResoDrive Settings.

This update fixes automatic startup after upgrading an older ResoDrive installation when another ResoDrive process is briefly inaccessible while exiting.

- ResoDrive waits briefly for confirmed process exit before completing user-data migration and opening the app.
- A process that remains running or cannot be safely verified still blocks migration. Account checks, upload protection, settings, and cached files remain protected.
- The optional CET compatibility setting is retained through repairs and future in-app updates.

If an earlier update installed successfully but the app did not reopen, launching ResoDrive from Start can complete migration. Install this update normally from Settings or with the standard Setup download.

For machines that require CET compatibility mode, run:

```powershell
.\ResoDrive-Setup.exe ResoDriveDisableCet=1
```

The choice is saved for repairs and future in-app updates. A fresh installation without this option keeps CET enabled. If this version is already installed, add `/repair /passive` to change the option. To restore the previous Windows setting later, run `ResoDrive-Setup.exe /repair /passive /norestart ResoDriveDisableCet=0`.

An older installer's cached helper is outside the CET option's control. See [the compatibility notes](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.45/docs/CET-COMPATIBILITY.md).

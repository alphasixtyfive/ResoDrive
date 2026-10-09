This update fixes account-check failures that could prevent ResoDrive from finishing an update, even when the app was running under the same Windows account.

- Settings and cached files remain protected while the app closes and finishes the update.
- Migration errors now include a reference for the diagnostic log, making failures easier to investigate.
- The standard installer now offers an optional compatibility mode for older Windows machines affected by CET startup crashes. One installer serves both modes.

If ResoDrive cannot launch because of the CET compatibility issue, open PowerShell in the folder containing the downloaded installer and run:

```powershell
.\ResoDrive-Setup.exe ResoDriveDisableCet=1
```

The choice is saved for repairs and future in-app updates. A fresh installation without this option keeps CET enabled. To restore the previous Windows setting later, run the installer with `/repair ResoDriveDisableCet=0`.

An older installer's cached helper is outside this option's control, so the first upgrade from an affected older version can still require recovery. See [the compatibility notes](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.43/docs/CET-COMPATIBILITY.md).

This release replaces 0.3.42; its previous downloads have been withdrawn.

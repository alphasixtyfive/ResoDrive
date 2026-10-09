This update keeps progress visible while ResoDrive stops its background work, avoiding an unnecessary 15-second pause before Setup opens.

- ResoDrive waits for the acknowledged background host to finish before closing its window and opening Setup.
- Upload checks, settings, and cached files remain protected.
- The same installer retains the optional CET compatibility mode and remembers your choice through future in-app updates.

If ResoDrive cannot launch because of the CET compatibility issue, open PowerShell in the folder containing the downloaded installer and run:

```powershell
.\ResoDrive-Setup.exe ResoDriveDisableCet=1
```

The choice is saved for repairs and future in-app updates. A fresh installation without this option keeps CET enabled. If this version is already installed, add `/repair /passive` to change the option. To restore the previous Windows setting later, run `ResoDrive-Setup.exe /repair /passive /norestart ResoDriveDisableCet=0`.

The first update from 0.3.43 still uses its older updater, so the delay improvement takes effect on updates started from 0.3.44. An older installer's cached helper is also outside the CET option's control. See [the compatibility notes](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.44/docs/CET-COMPATIBILITY.md).

This release replaces 0.3.43; its previous downloads have been withdrawn.

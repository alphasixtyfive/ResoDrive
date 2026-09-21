ResoDrive 0.3.11 adds remote wipe to existing Nextcloud app-password connections after updating. Users do not need to sign in again or re-add their drives.

- Reuses the URL, username and app password already saved in ResoDrive.
- Checks for a pending wipe before automatic drives start.
- Shows whether remote wipe is configured or needs attention beside each drive.

This replaces the 0.3.10 downloads, which did not automatically include older connections.

Nextcloud must explicitly request a wipe. It removes local ResoDrive accounts, cache and managed download folders; external folders, upload originals and server files are left alone. ResoDrive must be running and able to reach Nextcloud. This is not a whole-PC reset.

See the [remote-wipe guide](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.11/docs/REMOTE-WIPE.md) for setup, scope and the live-server acceptance steps to complete before relying on it.

Known limitation: the full desktop update from the previous public version, including cancellation and blocked shutdown, still needs checking on a disposable desktop.

ResoDrive 0.3.9 strengthens Nextcloud remote-wipe recovery and identifies storage requests with the app and Windows versions. It also includes the About improvements from the unpublished 0.3.8 draft.

- Include the ResoDrive version, available Windows edition/release/build, and client/OS architectures in the HTTP User-Agent sent to configured storage services. No hostname, username or device identifier is added. See [storage client identification](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.9/docs/SECURITY.md#storage-client-identification).
- Persist accepted wipe requests before stopping work. Resume interrupted cleanup before loading accounts or mounting drives.
- Clean up settings backups, setup recovery files and scheduler state alongside managed cache and account secrets. Locked files and redirected cache paths prevent completion.
- Protect newly connected accounts from stale recovery attempts and prevent stale app windows or setup rollback from restoring revoked data.
- Separate completed local cleanup from server-confirmed acknowledgement; retry failed acknowledgements without repeating cleanup.
- Isolate checks from session cookies, reject ambiguous responses, and validate the HTTPS origin and port while preserving Nextcloud subdirectory installations.
- Improve About with a larger icon, version beneath the name, wrapping text and keyboard-accessible links.
- Document device-specific and administrator-wide Nextcloud wipe commands, enrollment, scope and recovery.

Existing connections are not automatically enrolled. Reconnect through setup with a dedicated Nextcloud app password for this client. Disabling an account alone does not trigger deletion: Nextcloud must explicitly request a device/token wipe.

The current shared-cache design wipes all local ResoDrive accounts and managed cache, not just one connection. It does not delete server files or copies outside ResoDrive's data directory, and ordinary deletion is not forensic SSD erasure.

Automated tests cover the real Windows worker, protected storage, filesystem cleanup and failure recovery with simulated HTTP. A disposable live Nextcloud acceptance test is still required before relying on this feature operationally; see the [remote-wipe administrator guide](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.9/docs/REMOTE-WIPE.md).

ResoDrive 0.3.8 improves About and makes Nextcloud remote-wipe recovery durable.

- Refine About with a larger icon, version beneath the name, wrapping text and keyboard-accessible links.
- Persist accepted wipe requests before stopping work. Resume interrupted cleanup before loading accounts or mounting drives.
- Remove settings backups and setup recovery files as well as managed cache and account secrets. Locked files and redirected cache paths prevent completion.
- Keep stale app windows and setup rollback from restoring revoked account data.
- Retry failed acknowledgements without repeating cleanup; handle a retired token after a lost acknowledgement response.
- Validate the HTTPS origin and port, preserve Nextcloud subdirectory installations, and bound response size and body-read time.
- Keep an idle wipe-recovery host stoppable by the installer.

Existing connections are not automatically enrolled. Reconnect through setup with a dedicated Nextcloud app password for this client. Disabling an account alone does not trigger deletion: Nextcloud must explicitly request a device/token wipe.

The current shared-cache design wipes all local ResoDrive accounts and managed cache, not just one connection. It does not delete server files or copies outside ResoDrive's data directory, and ordinary deletion is not forensic SSD erasure.

Automated tests cover the real Windows worker, protected storage, filesystem cleanup and failure recovery with simulated HTTP. A disposable live Nextcloud acceptance test is still required before relying on this feature operationally; see the [remote-wipe administrator guide](docs/REMOTE-WIPE.md).

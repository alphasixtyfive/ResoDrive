ResoDrive 0.3.10 adds managed local download folders to Nextcloud remote wipe. It also strengthens wipe recovery and identifies storage requests with the app and Windows versions.

- New download jobs on enrolled Nextcloud accounts default to dedicated managed copies. A confirmed wipe removes these copies even if their sync jobs or settings have since been removed.
- Existing external folders and local upload originals remain outside wipe coverage. Choosing managed storage downloads into a separate folder; it does not move or erase old external copies.
- Reject managed paths outside each job's assigned folder, uploads from managed storage, overlapping jobs and redirected folders. Locked or inaccessible managed files keep wipe pending across restarts without acknowledging completion.
- After a host crash, wait for unverified processes using the private rclone runtime to exit before cleanup can be acknowledged.
- Include the ResoDrive version, available Windows edition/release/build, and client/OS architectures in the HTTP User-Agent sent to configured storage services. No hostname, username or device identifier is added. See [storage client identification](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.10/docs/SECURITY.md#storage-client-identification).
- Persist accepted wipe requests before stopping work. Resume interrupted cleanup before loading accounts or mounting drives.
- Clean up settings backups, setup recovery files and scheduler state alongside managed cache and account secrets. Locked files and redirected cache paths prevent completion.
- Protect newly connected accounts from stale recovery attempts and prevent stale app windows or setup rollback from restoring revoked data.
- Separate completed local cleanup from server-confirmed acknowledgement; retry failed acknowledgements without repeating cleanup.
- Isolate checks from session cookies, reject ambiguous responses, and validate the HTTPS origin and port while preserving Nextcloud subdirectory installations.
- Improve About with a larger icon, version beneath the name, wrapping text and keyboard-accessible links.
- Document device-specific and administrator-wide Nextcloud wipe commands, enrollment, scope and recovery.

Existing connections are not automatically enrolled. Reconnect through setup with a dedicated Nextcloud app password for this client. Disabling an account alone does not trigger deletion: Nextcloud must explicitly request a device/token wipe.

The current shared-storage design wipes all local ResoDrive accounts, managed cache and managed download copies, not just one connection. It does not delete server files or copies outside ResoDrive's data directory. Remote wipe requires the client to run and reach Nextcloud; it is not a whole-device reset or a guarantee against an offline or tampered-with device. Use disk encryption for protection when a PC is lost or stolen.

Automated tests cover the real Windows worker, protected storage, filesystem cleanup and failure recovery with simulated HTTP. A disposable live Nextcloud acceptance test is still required before relying on this feature operationally; see the [remote-wipe administrator guide](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.10/docs/REMOTE-WIPE.md).

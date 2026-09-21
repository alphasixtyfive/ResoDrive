ResoDrive 0.3.10 adds download folders that can be cleared by a Nextcloud remote wipe.

- New download jobs on enrolled Nextcloud accounts use managed folders by default. These copies stay covered by remote wipe even after you remove the sync job.
- Interrupted wipes resume when ResoDrive starts again. Locked files keep the wipe pending until they can be removed.
- Storage requests now include the ResoDrive version and available Windows edition, release, build and architecture in the User-Agent. No username, computer name or device ID is added.
- The About page is easier to read and supports keyboard navigation.

To enable remote wipe for an existing connection, reconnect it through setup using a dedicated Nextcloud app password. Disabling an account alone does not erase local data; Nextcloud must request a wipe.

A wipe removes all local ResoDrive accounts, cache and managed download folders. Existing external folders, upload originals and server files are left alone. Switching an existing job to managed storage downloads a separate copy; it does not remove the old folder.

ResoDrive must be running and able to reach Nextcloud for the request to arrive. This does not reset the PC or protect an offline or tampered-with device. Use disk encryption to protect a lost or stolen PC, and complete the live Nextcloud acceptance steps in the [remote-wipe guide](https://github.com/alphasixtyfive/ResoDrive/blob/v0.3.10/docs/REMOTE-WIPE.md) before relying on remote wipe.

Known limitation: the full desktop update from the previous public version, including cancellation and blocked shutdown, has not yet been checked on a disposable desktop. This release was published with that check outstanding.

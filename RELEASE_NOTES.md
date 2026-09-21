ResoDrive 0.3.12 reports the bundled rclone version alongside the ResoDrive and Windows versions in storage requests. Nextcloud administrators can now see which storage engine a connected ResoDrive client is using without installing a separate reporting service.

The same client identity is used by mounted drives, sync jobs, setup checks and remote-wipe traffic. ResoDrive reads the version from its verified private rclone installation once when the host starts; it does not run another version check for every request.

No hostname, account name, device identifier, file path or software inventory is added. Servers only see the header when the client makes an authenticated storage or wipe request.

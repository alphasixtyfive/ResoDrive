This release improves recovery and makes failed updates easier to diagnose.

- Keep a verified recovery backup intact when repairing damaged drive records.
- Handle damaged cache metadata safely and correctly recognize idle rclone drives with caching turned off.
- Stop a running rclone directly through its authenticated local control connection, including after its executable has been updated.
- Keep ResoDrive open until the update helper confirms it is ready. Failed helper launches no longer leave an update marked as pending.
- Explain Windows signature, application-control and reputation failures with their error codes. A successful installation followed by a blocked restart is reported accurately.

Update from Settings, or download **ResoDrive-Setup.exe** below. The portable ZIP is also available.

This release is unsigned. An application for free open-source signing through SignPath Foundation has been submitted; approval and integration are pending. Windows App Control may still block this release. If the existing update helper is blocked, try the manual Setup download; signing and acceptance on the affected machine remain separate work.

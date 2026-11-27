This update helps rclone installation recover when another program briefly locks a file.

- ResoDrive now waits and retries temporary file locks during the download and installation.
- If the file stays locked, the message explains what happened and what to try next.
- You can cancel the wait. Existing rclone files stay intact if replacement cannot finish.

Update from Settings, or download **ResoDrive-Setup.exe** below. A portable ZIP is also available.

This release is unsigned. Windows App Control may still require your organization's approval.

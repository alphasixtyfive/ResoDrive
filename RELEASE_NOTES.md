This release improves connection recovery and cuts down on background work, especially on slower PCs.

- Drives retry automatically when their server becomes reachable, including after a VPN connects. Starts that could stay stuck in the queue are fixed.
- Upload progress stays visible during brief status checks, and temporary connection problems produce fewer repeated warnings.
- Normal rclone updates no longer require unmounting drives or stopping sync jobs. Repair still requires stopping active work.
- Startup avoids repeated component checks, status reads use smaller buffers, and the transfer popup stops rebuilding while hidden.
- The main window opens a little taller so the normal Settings page fits without scrolling.
- Diagnostic reports now include transfer and recovery details, recent UI and host events, and runtime history. Names, addresses, paths and credentials stay out of the report.
- Fixed an error-reporting failure that could hide the original crash, and made SFTP host-key validation consistent throughout setup.

Update from Settings, or download **ResoDrive-Setup.exe** below. The portable ZIP is also available.

ResoDrive 0.3.31 makes startup failures easier to diagnose and improves installation and updates.

- Unexpected startup failures and crashes show a standard Windows error dialog. Use Copy details to share the diagnostic summary, or open the folder containing the saved reports. Normal startup has no additional splash screen.
- Local diagnostics include an error reference, application version, runtime and Windows details. Windows Error Reporting can retain up to three full memory dumps for deeper investigation.
- Fresh installations use Program Files\ResoDrive. The new updater can move an existing installation there and reopen it after the update.
- Settings, credentials and queued files stay in their existing data folder. Updates still stop safely when uploads prevent shutdown.
- A separate compatibility installer is available for Windows systems missing capabilities needed for CET hardware stack protection. It explains the reduced protection, leaves Windows security settings unchanged and preserves the choice for future updates.

If you update through version 0.3.30's Update button, this first update keeps the application in rdrive so the old updater can reopen it. The following update can move it to ResoDrive automatically. Running the new Setup directly can migrate it during this update.

Updating Windows is the preferred fix for missing CET support. A compatibility build does not guarantee that an older cached installer will run; some existing installations still need Windows updates before they can be upgraded.

Reports and memory dumps stay on the PC and are never sent automatically. Full memory dumps may contain sensitive data; Copy details shares only the diagnostic summary.

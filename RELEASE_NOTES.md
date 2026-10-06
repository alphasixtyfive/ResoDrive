ResoDrive 0.3.31 is an unpublished acceptance build for crash diagnostics and installation-folder compatibility.

- Unexpected startup and process failures show a standard Windows error dialog with Copy details and access to local diagnostics. Normal startup has no splash screen.
- Installed copies use Windows Error Reporting to collect up to three full dumps. Startup and host logs retain error references and build/runtime details. Matching symbols are retained separately for analysis.
- Fresh installations use Program Files\ResoDrive. Older in-app updaters retain their registered folder for a compatibility update; the newer updater can then migrate through Windows Installer and reopen the installed app correctly.
- Settings, credentials, queued-upload cache and other account data remain in their existing location. Upload protection continues to block unsafe installation.

Memory dumps may contain sensitive data. They stay local and are not sent automatically. Copy details provides a small diagnostic summary; dumps are separate, optional support attachments.

These changes improve diagnosis. They do not yet establish or fix the cause of the reported vessel runtime crash. Publication requires the installer lifecycle, UAC and actual prior-version updater acceptance checks in docs/RELEASING.md.

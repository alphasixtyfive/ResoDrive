# ResoDrive engineering checks

Before changing installer, updater, host shutdown, or named-pipe security code,
read [the installer incident report](docs/INSTALLER-INCIDENT-2026-09.md) and
[the release procedure](docs/RELEASING.md).

- Diagnose MSI failures from the failing custom action and its underlying error.
  Error 1603 alone does not identify a cause.
- Test an elevated installer/helper against an unelevated prior-version host.
  A same-elevation CI run is insufficient for Windows upgrade changes.
- Never bypass upload protection, broaden IPC access, or force-kill the host to
  make an installer check pass.
- Verify the installed version and preserve settings/cache during real upgrade
  checks. Use isolated data for process and destructive-operation tests.
- Record exactly which checks ran and any remaining limits. Follow the release
  acceptance checklist before publishing a corrected installer.

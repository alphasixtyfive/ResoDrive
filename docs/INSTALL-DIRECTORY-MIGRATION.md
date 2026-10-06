# Installation folder compatibility

Fresh installations use `%ProgramFiles%\ResoDrive`. Account settings, credentials,
cache and managed upload records continue using their existing data directory,
normally `%LOCALAPPDATA%\rdrive`. This migration changes program files only.

Every future MSI keeps the same UpgradeCode and discovers the previous registered
installation. An update without `RDRIVE_MIGRATE_INSTALL=1` preserves that folder.
This is required for old update-completion helpers, which are copied from the
old app before MSI runs. Published helpers cannot discover a moved executable
or acknowledge a window under the new directory's activation scope.

Current Setup and current in-app update helpers supply
`RDRIVE_MIGRATE_INSTALL=1`. MSI then installs into the canonical `ResoDrive`
folder using its normal major-upgrade transaction. A user skipping compatibility
versions can update an old client directly to any future MSI: that first legacy
in-app hop stays in `rdrive`; its next update uses the new helper and migrates.
Launching current Setup for a major upgrade performs the migration directly.
Repairing the already installed product retains its actual folder; relocation
belongs to a major upgrade, not same-product maintenance. No old executable
copy, directory junction, or forwarding alias is installed.

The app resolves its installed location through Windows Installer's known
UpgradeCode and per-machine product registration. It checks product code,
product name, publisher, version, location, and the executable's version
identity. `ARPINSTALLLOCATION` supplies the current location. Legacy packages
without that property use the recorded path of their frozen sample-profile
component, not a search for similarly named folders. Ambiguous, inaccessible,
redirected, or mismatched installation evidence stops resolution.

After a successful MSI operation, the current helper resolves the newly
registered executable and acknowledges its actual directory's instance scope.
It does not reopen an old portable source as proof of an installed update.
Canceled or failed installation still reopens the original source. Existing
host/upload authorization and exact installation process checks remain required.

Per-user startup tasks may outlive the old MSI. The new app can migrate a task
from the installer-recorded previous directory only after validating the HKLM
64-bit installation receipt against the current registered MSI location and
UpgradeCode. The task must still have the exact recorded executable or adjacent
launcher, current user's SID, one supported action, and least privilege. Other
installations and portable copies cannot claim this receipt. Disabled tasks stay
disabled when their path alone is migrated; task verification failure restores
the prior definition and enabled state.

On a PC used by several Windows accounts, each account must open ResoDrive once
from the current Start menu shortcut after folder migration. An enabled startup
task pointing to the removed `rdrive` executable or launcher cannot start the app
to repair itself. That manual launch reconciles only the current account's owned
task using its saved startup preference. The installer does not rewrite other
accounts' tasks. User-created shortcuts or scripts with the old program path
must also be updated.

An old Unimor bootstrapper also hardcodes `rdrive` and verifies the visible
window's exact executable path. Use an updated bootstrapper for installation or
activation after folder migration. Updating the app cannot rewrite a previously
distributed bootstrapper executable.

Release acceptance must cover fresh Setup, previous direct MSI and Setup-owned
installations, an old helper's preserved-folder first hop, the current helper's
canonical migration, skipped versions, canceled UAC, blocked uploads, repair,
rollback, removal, autostart migration, and separate-admin UAC. Verify one visible
installed product, registered location, actual executable version, and preserved
settings/credential/cache hashes using disposable account data.

Microsoft documents [related-product discovery](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumrelatedproductsw),
[registered product information](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msigetproductinfoexw),
[component path discovery](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msigetcomponentpathw),
and [major-upgrade removal sequencing](https://learn.microsoft.com/en-us/windows/win32/msi/removeexistingproducts-action).

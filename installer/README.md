# ResoDrive setup

The installer projects consume the framework-dependent release staged by
`build.ps1`; they do not publish the application independently. WiX Toolset
5.0.2 is pinned as an MSBuild SDK, so no machine-wide WiX installation is
required.

`ResoDrive-Setup.exe` is the normal user download. It embeds the MSI and downloads
the pinned .NET 10 Desktop Runtime only when a compatible runtime is absent. The
MSI remains available for managed deployment and in-app updates, where the runtime
prerequisite is already satisfied.

The MSI is a 64-bit, per-machine package that installs to
`%ProgramFiles%\rdrive`, creates a common Start menu shortcut, registers with
Windows Installed apps, and preserves all per-user data in
`%LOCALAPPDATA%\rdrive` during upgrades and uninstall.

The setup EXE uses a branded native WiX theme with the ResoDrive logo, installation
progress, a setup-log link on failure, and an **Open ResoDrive** button after
installation. It is a temporary prerequisite launcher: the visible MSI owns the
Installed apps entry whether installed directly or through Setup. Both chained
packages are permanent from Burn's perspective, so Burn removes its own
registration after completion. An interrupted installation or initiated restart
can retain temporary registration for recovery. Do not hide a retained bundle
registration with `DisableModify` or `DisableRemove`.

Repair and remove ResoDrive through Windows or the MSI. Burn's `Permanent="yes"`
does not make the MSI itself permanent or prevent Windows Installer from removing
it. Keep the application MSI last and vital: its own transaction still rolls back
on failure, but Burn will not remove a successfully installed permanent package
if a later chain step fails. Direct MSI deployments retain their standard Windows
Installer UI and launch checkbox. Silent deployments never launch the application.

Setup rejects `/uninstall` with instructions to remove ResoDrive through Windows
Installed apps. Its full-UI `/repair` can open the installation page because the
completed bundle is no longer registered; use Windows or the MSI for interactive
repair. An automated Setup repair needs `/repair /passive /norestart` or quiet
mode so WixStdBA retains the requested repair action.

For a nondefault data root, repair with the original MSI and
`/i package.msi REINSTALL=ALL REINSTALLMODE=amus RDRIVE_DATA_ROOT="D:\path\to\data"`.
Windows ignores command-line properties with `/f`; adding the root to that switch
does not pass it to preparation. Keep the guarded shutdown and fail-closed behavior.

A legacy installation with a hidden MSI and a visible setup bundle needs one
upgrade through the newer Setup to transfer maintenance to the visible MSI and
remove the older bundle through Burn. Direct MSI upgrades of those installations
are blocked with an instruction to use Setup. Merely carrying the old MSI's
hidden state forward would leave the old bundle unable to remove the upgraded app.

These ownership rules follow the pinned WiX 5.0.2 engine's
[package registration flags](https://github.com/wixtoolset/wix/blob/v5.0.2/src/burn/engine/package.cpp)
and [final registration calculation](https://github.com/wixtoolset/wix/blob/v5.0.2/src/burn/engine/apply.cpp).

MSI or the setup bundle owns routine installation progress. Preparation runs in
the background under the existing MSI action text and must not open a second
progress window. An interactive blocked preparation shows its specific reason
in the shared message dialog and fails installation; quiet deployments write
the diagnostic result and return failure without UI. This does not combine UAC
or Windows Installer's own failure messages into the application window.

The MSI and bundle `UpgradeCode` values are permanent product-family identities.
Never change them after publication. Normal releases increase `VersionPrefix` in
`Directory.Build.props`; Windows Installer versions use the `major.minor.build`
format. The owner-approved 0.3.7 and 0.3.19 replacements are narrow exceptions:
their MSI permits equal-version replacement without retaining a second product.
WiX 5's bundle also replaces related bundles of the same version. These replacements
require a manual Setup download because the application's update comparison still
requires a newer version.

During an upgrade, maintenance reinstall, or uninstall, ResoDrive first asks the background
host to stop its managed work. Preparation has a 60-second deadline and its failure
blocks maintenance. Version 0.3.7 and later reject this request when rclone reports
pending uploads or cache errors. Close documents and resolve the displayed condition
before retrying. Earlier installed versions cannot provide this check. A host belonging
to a separate portable installation is left alone. The installer then closes only the
`resodrive.exe` process running directly from this product's install directory.
Portable copies and unrelated processes elsewhere are left running. A confirmed
different-account installed process gets up to 42 seconds to exit naturally; a
remaining or unverifiable process blocks maintenance. Close documents before starting
maintenance; cached data is preserved, and setup does not guarantee that pending
uploads have reached the server. Launching ResoDrive afterward restores drives configured
to mount when the application starts; interrupted sync jobs are not resumed.

`tests/installer-smoke.ps1` runs only on disposable GitHub-hosted Windows runners.
CI and the release workflow verify fresh installation, application startup, repair
and removal while running, upgrade from the latest earlier public version, and
preservation of settings, a local-cache marker and a managed-copy marker. Installer
logs are retained even when these checks fail.

For a same-version recovery, additionally pass `-SameVersionBaselineMsiPath`,
`-SameVersionBaselineSetupPath` and `-CandidateMsiPath`, with `-SetupPath` pointing
to the candidate setup. Each installer must have its original matching `.sha256`
sidecar. The optional gate verifies the exact frozen baseline assets before any
installation, then exercises both MSI-to-MSI and setup-to-setup replacement. It
checks exact installed executable hashes, a single registered MSI product and
visible Installed apps entry, native removal of older bundle registrations, no
retained completed candidate bundle, and unchanged disposable data.
`same-version-result.json` records the
asset hashes and product identities. The runner restriction still applies; this
does not replace desktop UAC and prior-version updater acceptance.

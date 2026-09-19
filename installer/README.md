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
progress, repair/removal pages, a setup-log link on failure, and an **Open ResoDrive**
button after installation. The bundle owns the Installed apps entry; its embedded
MSI is hidden. Direct MSI deployments retain their standard Windows Installer UI
and launch checkbox. Silent deployments never launch the application.

The MSI and bundle `UpgradeCode` values are permanent product-family identities.
Never change them after publication. Every release must increase
`VersionPrefix` in `Directory.Build.props`; Windows Installer versions use the
`major.minor.build` format.

During an upgrade, maintenance reinstall, or uninstall, ResoDrive first asks the background
host to stop its managed work. Preparation has a 30-second deadline and its failure
blocks maintenance. Version 0.3.7 and later reject this request when rclone reports
pending uploads or cache errors. Close documents and resolve the displayed condition
before retrying. Earlier installed versions cannot provide this check. A host belonging
to a separate portable installation is left alone. The installer then closes only the
`resodrive.exe` process running directly from this product's install directory.
Portable copies and unrelated processes elsewhere are left running. The fallback
wait for each process is bounded to ten seconds. Close documents before starting
maintenance; cached data is preserved, and setup does not guarantee that pending
uploads have reached the server. Launching ResoDrive afterward restores drives configured
to mount when the application starts; interrupted sync jobs are not resumed.

`tests/installer-smoke.ps1` runs only on disposable GitHub-hosted Windows runners.
CI and the release workflow verify fresh installation, application startup, repair
and removal while running, upgrade from 0.3.6, and preservation of settings and a
local-cache marker. Installer logs are retained even when these checks fail.

<p align="center">
  <img src="src/ResoDrive.App/Assets/resodrive-mark.png" width="128" alt="ResoDrive app icon">
</p>

<h1 align="center">ResoDrive</h1>

<p align="center">
  Mount Nextcloud, other WebDAV storage, and SFTP servers as drives in Windows.
</p>

<p align="center">
  <a href="https://github.com/alphasixtyfive/ResoDrive/releases/latest">Download</a>
  · <a href="https://github.com/alphasixtyfive/ResoDrive/issues">Report an issue</a>
  · <a href="CHANGELOG.md">Changelog</a>
</p>

<p align="center">
  <a href="https://github.com/alphasixtyfive/ResoDrive/actions/workflows/ci.yml"><img src="https://github.com/alphasixtyfive/ResoDrive/actions/workflows/ci.yml/badge.svg" alt="CI status"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT license"></a>
</p>

ResoDrive is a small Windows tray app for storage that normally lives in a browser
or command line. Connect Nextcloud, WebDAV, or SFTP, choose a drive letter, and use
the storage from Explorer. By default, closing the window keeps active mounts and
transfers running in the tray. With **Minimize to tray** turned off, closing the
window uses the same checks and confirmation as **Exit**.

## What it does

- Mount Nextcloud, WebDAV, and SFTP storage in Explorer.
- Use passwords or SFTP private keys without putting credentials in profiles.
- Choose which drives mount at sign-in and how they reconnect.
- Run one-way copy or mirror jobs with readable progress and recent results.
- Keep credentials encrypted for your Windows account and rclone isolated from any
  system installation.
- Recover from background interruptions and unreliable connections automatically.

## See it in action

<table>
  <tr>
    <td colspan="2">
      <img src="docs/images/resodrive-drives.png" alt="ResoDrive Drives page showing fictional WebDAV and SFTP drives ready to mount">
      <br><strong>Drives at a glance</strong><br>Mount storage, open mounted locations, and reach each drive's settings from one place.
    </td>
  </tr>
  <tr>
    <td width="50%">
      <img src="docs/images/resodrive-connection-setup.png" alt="ResoDrive guided Nextcloud connection setup using fictional demo details">
      <br><strong>Guided setup</strong><br>Add Nextcloud, WebDAV, or SFTP storage without hand-editing configuration files.
    </td>
    <td width="50%">
      <img src="docs/images/resodrive-sync.png" alt="ResoDrive Sync page showing completed copy and mirror jobs with transfer statistics">
      <br><strong>Copy and mirror jobs</strong><br>See direction, completion details, transferred data, checks, and one-click controls.
    </td>
  </tr>
  <tr>
    <td width="50%">
      <img src="docs/images/resodrive-settings.png" alt="ResoDrive Settings page showing healthy components and settings export and import controls">
      <br><strong>Components and settings</strong><br>Keep dependencies current, export your settings, or import a configuration you saved earlier.
    </td>
    <td width="50%">
      <img src="docs/images/resodrive-activity-log.png" alt="ResoDrive activity log showing successful fictional copy and mirror jobs">
      <br><strong>Readable activity</strong><br>Review recent mount and transfer results without digging through raw logs.
    </td>
  </tr>
</table>

All names, hosts, and paths shown above are fictional demonstration data.

It runs on 64-bit Windows 11 and [supported Windows 10 LTSC or Enterprise releases](https://learn.microsoft.com/dotnet/core/install/windows#supported-versions)
as old as version 1809. The setup program installs the .NET 10 Desktop Runtime
only when it is missing. Mounting a drive requires
[WinFsp](https://winfsp.dev/); copy and mirror jobs do not.

## Install

Download `ResoDrive-Setup.exe` from the latest
[release](https://github.com/alphasixtyfive/ResoDrive/releases/latest), run it,
and open ResoDrive. The first time through:

1. Let ResoDrive download and verify rclone.
2. Add your storage connection and choose a free drive letter.
3. If you want a mounted drive, install WinFsp when prompted.

Your settings, encrypted credentials, logs, cache, and managed rclone copy live in
`%LOCALAPPDATA%\rdrive`. An upgrade leaves that folder alone. After the first
install, you can check for and install ResoDrive updates from Settings.
Application and rclone downloads can continue from a partial file after a dropped
connection, which avoids starting large transfers again on metered or satellite links.
Downloads can be cancelled from Settings and resumed later.

Mounted drives show queued uploads and cache errors when rclone can report them.
ResoDrive checks these again before disconnecting, reconnecting or preparing an
update. Close open documents first: no queued uploads does not mean that another
application has saved its changes. Recovered processes and unavailable statistics
are shown explicitly; Windows shutdown and crashes cannot provide this safeguard.
Readiness checks run periodically, including after resume, but do not prove server
connectivity when files can be served from cache.

Use **Settings → Export diagnostics** to create a report with component versions,
numeric performance options, mount states and recent UI error references. Names,
addresses, paths, credentials and raw log messages are omitted. Review the report
before sharing it.

Adding and editing a drive use the same caching controls under **Advanced**:
cache mode, cache size target, and retention since last access. Presets and custom
values are supported. New drives use read and write caching; existing drives keep
their previous mode until you change it. An older drive that now shows **Writes
only** was previously shown as **Standard** even though read caching was not enabled.
Save and accept **Apply and reconnect** to apply changes to a mounted drive.
The configured-options summary describes the settings to apply, not a running mount.

<details>
<summary><strong>Deployment profiles</strong></summary>

You do not need a profile file for personal use. ResoDrive opens with manual setup
when no `profiles.json` is present.

For a team deployment, start with [`profiles.sample.json`](profiles.sample.json),
add the connection presets you want to offer, and save the result as:

```text
%LOCALAPPDATA%\rdrive\profiles.json
```

Profiles are for connection details and defaults. Do not put passwords, tokens, or
private keys in them.

Profile cache flags populate the dedicated controls. Other approved arguments
remain in Additional rclone options, with one `--option=value` or standalone
switch per line. Profiles are copied when a connection is created; updating a
profile file or upgrading the app does not rewrite existing drives. Sync jobs
have their own options and do not inherit mount cache or timeout flags.

</details>

<details>
<summary><strong>Build from source</strong></summary>

Install the .NET 10 SDK, then run:

```powershell
dotnet test resodrive.slnx --configuration Release
.\build.ps1
```

The verified setup executable, MSI, and portable package are written to
`artifacts\win-x64`. The app is framework-dependent to keep downloads small; the
setup executable handles the runtime prerequisite for new installations.

</details>

## Project

- [Contributing](CONTRIBUTING.md)
- [Security](docs/SECURITY.md)
- [Nextcloud remote wipe: enrollment, commands and recovery](docs/REMOTE-WIPE.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Release process](docs/RELEASING.md)

ResoDrive is built by [@alphasixtyfive](https://github.com/alphasixtyfive) and
released under the [MIT license](LICENSE).

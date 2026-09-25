# September 2026: immediate update failure (1603 / 1722)

## Confirmed cause

The installed application was still **0.3.6**, even after attempts to install
0.3.7 and the withdrawn recovery releases. The MSI log on 19 September showed
`PrepareInstalledResoDriveForUpgrade` launching the old installed executable:

```text
C:\Program Files\rdrive\resodrive.exe --prepare-update
CustomAction PrepareInstalledResoDriveForUpgrade returned actual error code 1
Error 1722
MainEngineThread is returning 1603
```

The custom action failed in approximately 80 milliseconds, before file replacement.
1603 was a summary, not the cause. The failure was **not established to be pending
uploads**. Earlier conclusions based on the exit code alone were incorrect.

A read-only host-status probe on the affected machine reproduced the distinction:

| Caller | Windows token User | Token default Owner | Legacy CurrentUserOnly pipe client |
| --- | --- | --- | --- |
| Ordinary application | User account SID | Same user SID | Connected |
| Elevated installer | Same user account SID | BUILTIN\Administrators | Rejected immediately |

The exception was: “Could not connect to the pipe because it was not owned by the
current user.” .NET's Windows `PipeOptions.CurrentUserOnly` client implementation
compared the pipe owner with `WindowsIdentity.Owner`, which changes under UAC,
instead of authenticating the account represented by `WindowsIdentity.User`.
See [dotnet/runtime issue 123903](https://github.com/dotnet/runtime/issues/123903).

## Why previous fixes and checks missed it

- Moving the custom action earlier did not change the identity check in 0.3.6.
- Updating preparation code in the new app did not help: MSI invoked the old app.
- CI installed and launched everything from one elevated runner session. That
  did not reproduce a normal desktop app communicating with an elevated installer.
- Tests of XML ordering and mocked installer exit codes could not establish that
  an actual upgrade worked across UAC elevation.
- Republishing a version introduced additional ambiguity between the source,
  downloaded package, installed version, and cached installer. Version labels
  alone are insufficient evidence; record commit and artifact hashes.

## Required implementation rules

1. Carry upgrade preparation code inside the new MSI. Never depend on a legacy
   installed executable to implement a newly corrected upgrade protocol.
2. Authenticate the named-pipe server's actual Windows account SID using its
   kernel-reported process ID and process token. Do not treat Administrators as a
   user identity, remove authentication, or grant Everyone pipe access.
3. Create new host pipes with an explicit ACL for the account SID, including when
   elevated. Keep compatibility with older pipes by checking the server process
   identity rather than assuming their owner SID.
4. Keep upload protection. A rejected or unverified shutdown must stop installation
   with a useful reason, never fall through to unconditional termination.
5. After accepted shutdown, close only this installation's UI processes in the
   current session, then wait for the identified host to exit. Do not kill its
   process tree or other portable installations.
6. Show preparation progress, retain the specific preparation error, and preserve
   settings, credentials and cache on failure.

## Release acceptance checklist

- Run unit tests for real pipe ownership/identity and shutdown rejection/order.
- Run installer lifecycle checks: fresh install, running-app repair, removal,
  and upgrade from the actual previous public MSI. Verify data hashes afterward.
- On a Windows desktop with UAC, run `tests/elevation-smoke.ps1` from a normal
  PowerShell session. It must launch an isolated old app without elevation and
  run the new preparation helper elevated. Preserve its result and logs.
- Verify the actual in-app upgrade from the old public version, including its
  existing updater helper. Test cancellation and a blocked shutdown as well.
- Record the installed executable's ProductVersion/commit and MSI exit code;
  do not infer success from a completed download or a passing build.
- Publish only after the relevant reproduction passes. Do not call an untested
  production upgrade “verified.” Separate unit, integration and live results.
- Prefer a new monotonically increasing release version. If the owner explicitly
  requires replacing a release, disclose that same-version installs cannot detect
  it as newer, verify downloaded hashes, and provide a manual recovery installer.

Remote wipe is a separate destructive operation. Never test it against the real
user's accounts or cache as part of installer validation.

## Additional repair regression found during validation

The installer service does not reliably inherit a caller's process-local
`RDRIVE_DATA_DIR`. The isolated CI host used a custom data directory, while the
MSI helper looked in the default directory. The new fail-closed helper correctly
refused to terminate a running process whose host it could not contact. Previous
force-stop logic had masked this mismatch.

Custom roots now travel explicitly through `RDRIVE_DATA_ROOT` (MSI) or
`ResoDriveDataRoot` (setup bundle). The in-app updater supplies its active root.
The smoke test supplies its isolated root through that same supported interface.
Never fix this test by ignoring a failed handshake. Also, repairing ResoDrive must
not repair the shared .NET runtime while applications are using it; the bundle
installs a missing prerequisite but leaves an existing runtime alone on repair.

Because the owner explicitly requested replacement under 0.3.7, that version alone
allows MSI same-version replacement. ICE61 is suppressed only for that version's
intentional inclusive upgrade range; the remaining MSI validation stays enabled.
Future versions revert to normal increasing-version upgrades. This prevents two
separately registered 0.3.7 products during recovery; it does not make the app's
version comparison treat 0.3.7 as newer than 0.3.7.

## Observed desktop results on 19 September

- The isolated elevation reproduction passed against the actual 0.3.6 binary:
  helper exit 0, legacy host exited, settings and cache-marker hashes unchanged.
- The legacy 0.3.6 updater handoff successfully installed the corrected MSI on the
  affected machine at 16:55 local time. MSI returned 0 and the app reopened with
  ProductVersion `0.3.7+37183b46d7ffe281ce8f97405e1beb887821dc89`.
- Settings, encrypted rclone configuration, protected credentials, and profile
  hashes matched their pre-upgrade values. Cache remained present; its file count
  changed during normal rclone operation, so no claim of byte-identical live cache
  is made.
- A canceled UAC attempt left 0.3.6 installed and reactivated the original app.
- These observations establish the original UAC fix. They do not replace the
  release gates for later source changes, including custom-root repair support.

The compact upload-indicator change also exposed a test gap: WPF `Run.Text`
defaults to two-way binding, which cannot target read-only view-model properties.
Display runs must use `Mode=OneWay`. The process smoke test now includes a disabled
drive fixture and fails on dispatcher/startup exceptions, rather than testing
only an empty window. Both CI and the release workflow run that populated test.

## Later unavailable-host upgrade report

An installer dialog reported that a running ResoDrive process was not responding.
The displayed eight-character error ID is a random log correlation ID, not an
installer failure code. The message comes from the new package's preparation
helper when its pipe request returns `host.unavailable` and it finds a
`resodrive.exe` at the installed path. The process may be the tray UI alone;
the screenshot and ID do not establish why the remote machine's host was absent.

The helper now retries the authenticated pipe request while the legacy host
mutex exists, since older hosts can hold that mutex before opening the pipe.
With no host mutex, it can close a verified tray UI only after checking readable
mount ownership records and remaining managed rclone processes. It verifies the
process path, session, Windows account SID and command line, and checks the full
process set before closing any UI. It never force-stops a host or rclone child.
Unverifiable identity, active background work, a foreign installation or a
different Windows account still stops the upgrade with an actionable message.

The orphaned-UI check scans managed rclone processes across Windows sessions.
It permits an outside-root process only when its live parent is a ResoDrive host
at another executable path and the authenticated status pipe confirms that
host's process ID and data root. Process ancestry, account, command line,
config path and physical executable identity must all agree. An orphaned or
unverifiable process still blocks the upgrade.

On 25 September, an isolated 0.3.14 window-only UAC test first stopped safely
because a live production rclone process was present under another data root.
After adding the verified live-host check, the same isolated elevated test
passed: the old UI exited and disposable settings and cache hashes were
unchanged. Production rclone processes were left running. The separate
unelevated-host-to-elevated-helper test also passed with disposable data.
These helper tests do not replace actual MSI and in-app updater acceptance.

## Standard-user update with separate administrator credentials

An in-app update on a standard Windows account reported that ResoDrive was
running under another account after the user entered an administrator password.
That account difference is expected for over-the-shoulder UAC: Windows runs the
elevated installer as the supplied administrator while the old app and host still
belong to the signed-in user. The 0.3.16 handoff waited for its window to exit,
but did not wait for the accepted host shutdown to finish before starting MSI.
The new MSI then looked for the signed-in user's host under its own administrator
account-scoped pipe name and could encounter the old process before it exited.

The next handoff prepares the installation as the signed-in user before invoking
UAC, including upload checks, verified UI closure and waiting for the host to
exit. For the first upgrade from an older release, the new MSI waits up to 42
seconds for a confirmed different-account installed process to exit naturally.
It never terminates that process; a remaining process still blocks installation.
Unreadable process identity is reported separately from a confirmed account
mismatch. The update confirmation now says that Windows may require an
administrator password.

Unit and process tests cover ordering and fail-closed behavior. The exact
standard-user / separate-admin UAC path still needs a live acceptance test;
same-account elevation cannot establish it.

For v0.3.17, [CI run 36145684243](https://github.com/alphasixtyfive/ResoDrive/actions/runs/36145684243)
and [tagged release run 36146421965](https://github.com/alphasixtyfive/ResoDrive/actions/runs/36146421965)
passed the installer lifecycle and disposable different-account smoke checks.
The latter started the public 0.3.16 host as a separate standard user in the
same session, confirmed that the new helper left it running while active,
stopped it under its own account, and then confirmed preparation success with
unchanged settings and cache. The exact draft executable hash was
`2C6946B3EE9B5365213514D02788E4A169ACD3BF3D5896E0DFF59FF533B4DF5E`;
its ProductVersion includes commit `202233e`. Local same-account UAC host and
orphaned-UI tests passed against that downloaded draft executable and preserved
disposable settings and cache. These checks do not simulate the old user's
in-app Update click and credential prompt. The owner requested public GitHub
publication to run that final check on the affected standard-user desktop;
the first live result is recorded below.

The first live attempt on a standard-user desktop still reported a different
Windows account after the administrator password was accepted. The user could
not provide the preparation files, so the exact remaining process is unknown.
The public 0.3.16 updater, which controls that first hop, can proceed after
`host.unavailable` without proving that an installed host exited. It also
starts MSI as soon as its parent window exits after an accepted shutdown;
in-flight host recovery can start another host before that window closes.
The 0.3.17 installer can only wait for a different-account process to exit;
it correctly cannot terminate a host that may still own uploads. The release
notes now give an explicit tray Exit and manual Setup recovery for this case.
Do not count the first live attempt as successful in-app acceptance.

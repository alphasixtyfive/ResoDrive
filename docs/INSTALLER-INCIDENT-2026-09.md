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

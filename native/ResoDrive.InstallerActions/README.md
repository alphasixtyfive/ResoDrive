# Native installer actions

The DLL embeds the exact published `resodrive.exe`, without republishing or
changing its CET marking. Windows Installer calls its exported actions in the
same execution context and sequence as the former executable actions. A private,
unique extraction directory under the system Windows Temp folder permits only SYSTEM and the invoking Windows account
to write. The image stays open without write/delete sharing until the child exits.
All base-directory ancestors are pinned without delete sharing and rejected if
they are reparse points. After closing the writer, the DLL rejects replaced leaves
and compares the retained file's exact length and every byte with its resource
before launch. It does not claim isolation from the same Windows account.
An impersonated MSI account is duplicated into the child's primary token with
that user's environment; the installer-service account is never substituted.
Preparation with MSI UI levels 3, 4 or 5 selects that account's existing
`winsta0\\default` desktop so failure dialogs remain visible. Silent preparation
and deferred actions keep their existing desktop context; no desktop ACL changes
are made.

`PrepareRegisteredInstallation` reads `INSTALLFOLDER`, `UILevel`,
`RDRIVE_DATA_ROOT`, and the effective `RDRIVE_HELPER_DISABLE_CET` property (`0` or `1`).
`UnregisterAutostart` reads the installation folder and CET property and invokes
the installed executable. Deferred migration actions receive CustomActionData:

| Export | CustomActionData |
| --- | --- |
| StageMigration | `0` or `1`, newline, previous absolute installation directory |
| ApplyMigration | `0` or `1` |
| RollbackMigration | `0` or `1` |
| CommitMigration | `0` or `1` |
| ConfigureCetPolicy | `0` or `1`, newline, installed absolute executable path |
| RollbackCetPolicy | same as ConfigureCetPolicy |
| CommitCetPolicy | same as ConfigureCetPolicy |
| RemoveCetPolicy | same as ConfigureCetPolicy |

Preparation, autostart cleanup, and migration use the helper setting: opt-out
when either the previous installation or the requested installation opted out.
This keeps transitional helpers executable while changing back to standard mode.
Policy actions receive the requested `RDRIVE_DISABLE_CET` setting so they can
restore the Windows policy independently of helper startup.

Opt-out applies `CET_USER_SHADOW_STACKS_ALWAYS_OFF` when creating the helper,
before CoreCLR starts. The default uses normal process creation. The managed
helper retains account authentication, upload protection and shutdown deadlines.
The native wrapper waits for the result and never terminates any process.

The policy actions run the embedded `installer/CetPolicy.ps1` through the fixed
System32 Windows PowerShell executable, with no profiles, noninteractive mode,
and a process-only execution-policy bypass. The script owns persistent policy
transactions. It cannot be replaced by a caller-selected script at runtime.
On failure, the script can write its bounded UTF-8 diagnostic alongside its
private extraction. The DLL reads at most 4096 bytes into the MSI log and removes
the diagnostic during cleanup; it does not inherit output pipes across accounts.

Build with `build.ps1 -ApplicationPath <published-exe> -OutputDirectory <artifacts>`.
`-PolicyScriptPath` can override the script input at build time.
The build checks both embedded SHA-256 hashes against their shipped inputs and
requires the application's actual PE extended DLL characteristics to retain CET.
Resource verification maps the DLL as data without invoking executable code.
MSVC x64 tools and the Windows SDK are required. This component does not modify
persistent Windows mitigation bitfields itself; the embedded script uses Windows'
policy cmdlets. It cannot fix an older MSI's cached helper or prove acceptance on
an affected Windows version. Windows before build 19041 has no CET process
mitigation and uses normal creation. A supported mitigation request failure is
reported instead of retrying without the requested protection setting.

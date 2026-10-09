# CET compatibility

ResoDrive 0.3.43 keeps one application build and one standard
installer. The application remains CET-compatible. Administrators can request
the hidden compatibility option when installing:

```powershell
.\ResoDrive-Setup.exe ResoDriveDisableCet=1
```

The versioned Setup download accepts the same argument. Direct MSI installations
accept `RDRIVE_DISABLE_CET=1`. Only the exact strings `0` and `1` are accepted.
Omitting the option preserves the installed choice; a fresh install defaults to
`0`. Repairing with `/repair ResoDriveDisableCet=0` restores the Windows setting that existed
before ResoDrive applied its override.

Setup saves the choice in HKLM64 `SOFTWARE\ResoDrive\Installation\DisableCet`.
Repair and upgrades load it before preparation, including unattended in-app
updates. The installed executable receives a path-specific Windows
`UserShadowStack` override through Windows' per-image mitigation API.
The script saves the original field, verifies readback, and journals changes
for MSI rollback. Removal restores only the installer-owned setting; it leaves
other mitigations and a later administrator change alone. A conflicting
administrator policy stops the compatibility change before mutation. This includes
an existing basename-wide exploit-protection rule: creating an exact-path filter
can change how Windows inherits that rule. ResoDrive reports the conflict instead
of copying or replacing the administrator's policies.
When restoring the default, it removes an empty path filter only if its durable
receipt proves that filter did not exist before installation. Existing filters
and filters with other administrator settings remain intact.

Copied update helpers and temporary MSI helpers cannot inherit an installed-path
override. They are created through the native Windows process API with only the
CET shadow-stack mitigation disabled, before CoreCLR starts. MSI's small native
DLL embeds the exact published application and policy script. It preserves the
invoking account, protects temporary payloads against replacement, waits for the
existing managed preparation result, and never bypasses upload protection.
Windows before build 19041 uses ordinary creation because CET is unavailable.
A requested mitigation failure on newer Windows fails with its native error;
an unsupported persistent Windows policy also fails with a specific diagnostic.

An application command-line flag would be too late: the observed CoreCLR failure
occurs before managed argument parsing. No executable bytes are patched and no
second application variant is produced. The build explicitly enables CET and
checks the published PE marking and embedded payload identity.

## Earlier installation limitation

The previous investigation tied the Windows 19043 startup failure to
CoreCLR's shadow-stack initialization guard. A separate CET-off application
launched on that machine, according to the reported test. The old archived probes
also established that the same .NET 10 binary can run with shadow stacks on or
off when the creation policy changes.

This release cannot rewrite an older MSI's already cached preparation helper.
The first manual transition from an older affected installation still runs that
old MSI during major-upgrade removal, and needs acceptance on the affected
machine. An older app's copied updater also predates the native launcher; use
manual Setup for that first transition. Once this release is installed with
the option, its subsequent helpers and upgrades preserve the choice.

## Verification

Local tests cover strict saved-choice parsing, native mitigation creation,
arguments, Unicode paths, environment and current-account preservation.
Policy transaction tests cover defaults, repair, rollback, removal and later
administrator edits. Elevated policy and complete update tests run only on
disposable hosted Windows runners. The native policy transactions passed at
commit `3e7a049` in [the isolated Windows check](https://github.com/alphasixtyfive/ResoDrive/actions/runs/37923324138),
including CFG, nonzero sibling and audit fields, empty-filter cleanup, rollback,
future basename-policy inheritance and rejection before mutation. Full installer
and update acceptance is recorded with the final release assets.

The actual Windows 10 vessel and its cached old MSI have not been tested with
this release. Publication follows the acceptance steps in
[RELEASING.md](RELEASING.md), including the desktop UAC transition.

References: [Microsoft .NET CET support](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/9.0/cet-support),
[Windows process creation attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute),
and [Windows exploit-protection policy](https://learn.microsoft.com/defender-endpoint/enable-exploit-protection).
The installed-path override uses [RtlSetImageMitigationPolicy](https://learn.microsoft.com/en-us/windows/win32/debug/rtlsetimagemitigationpolicy-function).

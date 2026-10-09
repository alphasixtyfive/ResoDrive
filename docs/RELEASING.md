# Releasing ResoDrive

Use increasing release versions and retain published tags. Withdraw known-broken
downloads when necessary, with an explanation in the replacement release notes.
Replacing a version is an exceptional owner-approved recovery operation; an
existing installation of that version cannot discover it as a newer update.
For the owner-requested .19 replacement, the MSI permits equal-version upgrades.
Keep this exception limited to .19 and the historical .7 recovery. Verify both
the earlier .19 MSI and setup bundle can be replaced without duplicate products,
using exact accepted assets and preserved data hashes. Provide the manual Setup
download in the release notes; do not change the application's version comparison.
On the local development drive, keep only the current public build artifacts
rather than archiving local copies.

## Repository visibility

The built-in update checker follows GitHub's public `releases/latest` redirect;
it does not consume the anonymous REST API quota. Therefore the production
repository and its published releases must be public. A private repository returns
`404` to installed clients unless every client is given a GitHub credential, which
ResoDrive intentionally does not request or store.

Private development can still happen in a separate private repository, but the
release source/tag and assets consumed by users must be mirrored to the configured
public repository before publishing.

## Release procedure

1. Set `VersionPrefix` in `Directory.Build.props`.
2. Rewrite `RELEASE_NOTES.md` in short, plain language for the current release and
   update other release-facing documentation as needed.
3. Run `./build.ps1` on Windows and smoke-test the setup executable, portable
   package, and MSI. Complete the UAC upgrade checks below before publishing.
   Verify that Setup leaves one visible, removable MSI entry and no retained
   bundle entry. Cover both a previous direct-MSI installation and an older
   Setup-owned installation: reject the latter's MSI-only upgrade before
   preparation, then migrate it through Setup and verify repair/removal.
   Run `tests/installer-smoke.ps1 -FullSetupUi` on the disposable hosted runner:
   fresh Setup and the older Setup-owned transition must show their own progress
   without a separate Windows Installer window. Preserve the window receipts.
   Also run the current helper's actual passive Setup handoff and the old public
   helper's migration with a startup drive enabled. Verify that the new host
   starts only after the old helper exits and the data move completes. Exercise
   recovery from .35's new program path with its old default data root; an empty
   settings fixture cannot establish this migration behavior. Preserve failure
   receipts as well as successful data hashes.
4. Commit the release and create an annotated tag matching the version exactly,
   for example `git tag -a v0.3.0 -m "ResoDrive 0.3.0"`.
5. Push the commit and tag. The Release workflow rebuilds and tests from the tag,
   then uploads an Actions artifact named `resodrive-<tag>` containing the
   versioned ZIP, MSI, setup executable, SHA-256 files, and stable
   `ResoDrive-Setup.exe` download. It creates no GitHub release or draft.
6. Download that successful tagged run's artifact and verify all eight files and
   their SHA-256 sidecars. Record the run, tag, commit and package hashes. Complete the
   desktop UAC and actual prior-version updater checks using those exact assets.
   Record installer outcome, installed commit and preservation of account data.
   Do not rebuild or replace the accepted assets.
   Use the exact Setup as well when a legacy bundle remains on the test PC.
   Confirm its native migration removes the old bundle without removing the
   current MSI, changing user data, or retaining its own bundle registration.
   The owner performs their production upgrade manually. For the .35 correction,
   confirm their existing data has moved and the app opens correctly before
   publication; isolated fixtures alone do not establish that recovery.
7. Create one final public GitHub release from the accepted eight files, using
   `gh release create <tag> <files> --verify-tag --latest --notes-file RELEASE_NOTES.md`.
   Use the notes from the accepted tag. Do not create intermediate draft releases.
   Verify uploaded asset hashes against the accepted files, then confirm that
   Settings > Components > ResoDrive discovers the published version.

GitHub Actions are pinned to immutable commit hashes. Dependabot proposes action
and NuGet updates for review.

## Installer regression gate

Activation tests must not synchronously block xUnit worker threads while waiting
for pipe callbacks on the same pool. Run their mutex-owning test bodies on dedicated
threads and await completion from xUnit. A September 2026 release run exposed
starvation that was reproduced with `DOTNET_PROCESSOR_COUNT=1`; both workflows now
repeat the application tests under that constraint. Do not mask it with longer
timeouts or repeated release attempts.

Process-ownership fixtures must wait for a child readiness signal before reading
its executable identity. `Process.Start` returning does not guarantee that Windows
has populated `MainModule`; immediate reads caused an intermittent null reference
in the remote-wipe boundary test. Keep the actual ownership checks unchanged.

Read the [September 2026 installer incident](INSTALLER-INCIDENT-2026-09.md)
before changing installer shutdown, IPC security, or updater handoff code.

The .35 recovery check uses its exact withdrawn MSI from a pinned successful
Actions run rather than a draft release. Actions artifacts expire according to
repository retention settings; a pinned run ID does not preserve its files
indefinitely. Retain the baseline MSI and checksum in a trusted archive before
expiry. If the pinned artifact becomes unavailable, fail the check and restore
those exact hash-verified files to a new Actions artifact, updating the pinned
run and expected hashes through review. A rebuild from the immutable .35 tag is
a different test baseline and needs explicit revalidation; do not silently
substitute it for the installer users received.

An elevated CI runner alone cannot prove that a normal user's app can be upgraded.
From an **unelevated** Windows PowerShell session, run the isolated reproduction:

```powershell
./tests/elevation-smoke.ps1 -PreviousAppPath 'C:\Program Files\ResoDrive\resodrive.exe' -NewAppPath '<accepted artifact executable>'
```

The test copies the old single-file app, creates disposable settings and cache,
launches the old host normally, and requests UAC elevation for the new package's
preparation helper. It checks process exit and unchanged settings/cache hashes.
It never uses production accounts. Keep its `result.json` and preparation logs.
Also verify the real in-app update from the prior public version and inspect the
installed ProductVersion/commit. A build, mocked exit code, or XML assertion is
not a substitute for this check. Never skip an upload block to make a test pass.

For a nondefault data directory, use `ResoDriveDataRoot="D:\path\to\data"` with
the setup executable or `RDRIVE_DATA_ROOT="D:\path\to\data"` with msiexec. A
process-local environment variable alone is not propagated through the Windows
Installer service. The built-in updater passes the active data root explicitly.
For a repair using that root, use `/i package.msi REINSTALL=ALL REINSTALLMODE=amus`
with `RDRIVE_DATA_ROOT`; `/f` ignores property arguments. Inspect the helper's
actual root in the MSI log rather than trusting the shell command.

For CET compatibility, use the standard Setup with `ResoDriveDisableCet=1`.
Omitting it preserves the saved choice. To restore the prior Windows setting,
repair using `/repair ResoDriveDisableCet=0`. Both CI and release acceptance
verify the native helpers, Windows policy rollback, and a real next-version
in-app update with the flag omitted. Review [the CET compatibility notes](CET-COMPATIBILITY.md)
and test the first transition on an affected Windows machine before publication:
an older cached MSI helper is outside the new wrapper's control.

## Signing

Current local packages include SHA-256 sidecars but are not Authenticode signed.
Before broad distribution, configure a protected CI signing identity and sign the
executables, MSI, and setup bundle before release publication. Never place a signing certificate
or password in the repository; use GitHub environment secrets or an external
key-signing service.

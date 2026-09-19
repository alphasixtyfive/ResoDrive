# Releasing ResoDrive

Use increasing release versions and retain published tags. Withdraw known-broken
downloads when necessary, with an explanation in the replacement release notes.
Replacing a version is an exceptional owner-approved recovery operation; an
existing installation of that version cannot discover it as a newer update.
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
4. Commit the release and create an annotated tag matching the version exactly,
   for example `git tag -a v0.3.0 -m "ResoDrive 0.3.0"`.
5. Push the commit and tag. The Release workflow rebuilds and tests from the tag,
   uploads the versioned ZIP, MSI, setup executable, SHA-256 files, and stable
   `ResoDrive-Setup.exe` download, then creates the GitHub release.
6. Confirm that Settings > Components > ResoDrive discovers the published version.

GitHub Actions are pinned to immutable commit hashes. Dependabot proposes action
and NuGet updates for review.

## Installer regression gate

Read the [September 2026 installer incident](INSTALLER-INCIDENT-2026-09.md)
before changing installer shutdown, IPC security, or updater handoff code.

An elevated CI runner alone cannot prove that a normal user's app can be upgraded.
From an **unelevated** Windows PowerShell session, run the isolated reproduction:

```powershell
./tests/elevation-smoke.ps1 -PreviousAppPath 'C:\Program Files\rdrive\resodrive.exe' -NewAppPath './artifacts/win-x64/resodrive/resodrive.exe'
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

## Signing

Current local packages include SHA-256 sidecars but are not Authenticode signed.
Before broad distribution, configure a protected CI signing identity and sign the
executables, MSI, and setup bundle before release publication. Never place a signing certificate
or password in the repository; use GitHub environment secrets or an external
key-signing service.

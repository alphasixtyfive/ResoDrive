# Contributing

ResoDrive welcomes focused bug fixes and improvements. Open an issue before a
large behavioral or architectural change so the design can be discussed first.

## Development

Requirements:

- x64 Windows 11, or a supported Windows 10 LTSC/Enterprise release
- The .NET SDK version pinned in `global.json`
- WinFsp for manual mount testing

Read [AGENTS.md](AGENTS.md) before changing code. It points to the additional
checks for editable inputs, installers, shutdown and remote wipe. Use the
[architecture source map](docs/ARCHITECTURE.md) to find the component that owns
the behavior.

Restore the pinned dependencies and run the complete test suite before opening a
pull request:

```powershell
dotnet restore resodrive.slnx --locked-mode
dotnet test resodrive.slnx --configuration Release --no-restore
```

Use `./build.ps1 -BuildMsi $false` to verify the framework-dependent portable
package, or `./build.ps1` for the full setup, MSI, and ZIP release pipeline.
Never commit generated files from `artifacts`, `bin`, `obj`, or local application
data.

## Design and review

Keep a change centered on one behavior and its owner. Prefer an explicit state
transition or a small policy function over another framework, service layer or
background process. Keep platform-independent rules in Core, Windows mechanisms
in Windows, background coordination in Host and presentation in App.

When fixing a defect, add a regression that would fail with the old behavior.
Exercise the real save boundary, process lifetime or recovery order when that is
where the defect occurs. Avoid tests that only repeat implementation details.
Use disposable files and processes; never use a production account or cache to
test destructive operations.

Explain the trigger, resulting behavior, validation and remaining limits in the
change description. Comments should explain an invariant or a reason that is
not evident from the code. Keep user-facing messages short and actionable; keep
protocol details and internal identifiers in diagnostics.

UI changes need the [compiled input and visual checks](docs/UI-INPUT-VALIDATION.md).
Installer and update changes need the [release acceptance checks](docs/RELEASING.md).
A passing unit suite does not establish a real desktop upgrade. Preserve published
tags and identify accepted packages by their hashes and source commit.

Do not commit a deployment-specific `profiles.json`. Update the generic
`profiles.sample.json` only with reserved example domains and non-sensitive values.

## Security and privacy

Do not attach credentials, private keys, unredacted rclone configuration, or logs
containing private service URLs. Follow [docs/SECURITY.md](docs/SECURITY.md) for
security reports.

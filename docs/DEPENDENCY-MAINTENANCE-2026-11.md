# Dependency maintenance, 28 November 2026

## Main/release reconciliation

The old `main` tip was `3b558ed921cc09dcaf0307706079319a753b24d4`,
with version 0.3.19. The latest public release was 0.3.53 at
`a8dff81eb9c17bbe7a8374308fbbc7126608ce91`. The histories had diverged:
70 commits on the release side and seven on main.

The main upload/tray/reordering work, update receipts and shutdown-disconnect
regression were incorporated and extended by release commit `82862f2`. Main's
Coverlet update and solution locks were incorporated by `104f30d`. The retained
release also contains the subsequent recovery and validation fixes.

Merge `8858b7f` preserves both parents and has exactly the 0.3.53 release tree.
Conflict resolution retains the current transfer window instead of resurrecting
the removed upload-only window, and avoids duplicating existing receipt tests.
No main history was reset or force-pushed.

## Tooling updates

The following original PR heads are preserved in merge commits and tested
together against the current application:

- #6: actions/upload-artifact 7.0.1 to 7.0.2.
- #7: actions/setup-dotnet 6.0.0 to 6.1.0.
- #9: Microsoft.NET.Test.Sdk 18.9.0 to 18.12.0.
- #11: xunit.runner.visualstudio 4.0.0 to 4.0.1.
- #12: coverlet.collector 10.1.0 to 10.1.1.

Action pins were verified against the upstream release tags. NuGet locks were
regenerated for the complete solution. Application runtime package versions
remain unchanged; project-reference lock metadata now records 0.3.53.

These changes maintain builds and tests. They do not publish a new application
version or replace any existing release package. Validation receipts are kept
outside the repository under the maintenance output directory.

## Runtime proposals

PRs #8 (Microsoft.Extensions.Hosting 11.0.0) and #10 (System.Management 11.0.0)
failed during locked-mode restore with NU1004. Their central package edits and
partial lock updates disagree with downstream application/test lock graphs.
Those failures do not establish API or runtime incompatibility. The proposals
need complete lock regeneration and compatibility testing on current source,
separately from the tooling maintenance.

Both proposals were refreshed in separate checkouts by merging the current
application source and regenerating all solution locks. Locked restore and the
application build then passed with zero warnings/errors on the existing .NET 10
target. Their package metadata includes a net10.0 dependency group; a .NET 11 SDK
or target-framework change is not required merely to resolve these packages.

Keep these major runtime changes separate from the tooling merge. They change
the shipped library graph and should be adopted deliberately for a planned
application release after their full compatibility CI passes. They are not
needed to repair the test/build dependency maintenance.

## Local validation

The combined tooling build passed 1,457 tests (Core 206, Windows 806, App 445),
with two optional/elevation-dependent cases skipped locally. MSI and Setup builds
passed without warnings. All 445 App tests also passed with one available CPU;
published process, updater-helper and profile-import smokes used disposable data.

A NuGet vulnerability audit of all seven solution projects, including transitive
packages, reported no known vulnerable packages on 28 November 2026. This does
not cover undisclosed vulnerabilities or replace operating-system/runtime patch
management. No new public version, installer asset or tag is needed for this
tooling maintenance. Hosted CI remains the gate for the reconciled main branch
and the separately refreshed runtime proposals.

## Hosted results and disposition

- Reconciled main source `706472db81bb6249cbd5a3377e7faa925217763c` passed
  [the full CI pipeline](https://github.com/alphasixtyfive/ResoDrive/actions/runs/46130035649),
  including native/CET, constrained-CPU, process, installer, migration, actual
  previous-public-updater and compatibility-choice upgrade checks.
- System.Management 11 in PR #10 passed
  [the same full pipeline](https://github.com/alphasixtyfive/ResoDrive/actions/runs/46130092758).
  Its original lock-file failure is repaired. Keep this optional major runtime
  change open for a planned application release; main retains 10.0.11.
- Microsoft.Extensions.Hosting 11 in PR #8 passed locked restore and compilation,
  then [failed two diagnostic/shutdown cases](https://github.com/alphasixtyfive/ResoDrive/actions/runs/46130089348).
  `RealGenericHostBackgroundServiceFailureIsReportedAsFatal` fails with both
  normal and externally disabled logging: the original worker exception now
  escapes through `Host.StopAsync` / `WaitForShutdownAsync` before the assertions.
  An isolated local reproduction failed both cases with version 11; the same
  two cases passed with version 10. This is a changed host exception contract,
  not a remaining NuGet restore error. The production outer fatal catch already
  exists; these results alone do not establish lost diagnostics or account data.
  Keep the proposal unmerged until that contract and its diagnostics are reviewed.

The original shutdown/diagnostic assertions remain intact. The final acceptance
record is documentation only. No runtime upgrade or new public release was made.

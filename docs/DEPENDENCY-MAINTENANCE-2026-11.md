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
  Its original lock-file failure was repaired. It was initially kept open as an
  optional major runtime change, with main still on 10.0.11.
- Microsoft.Extensions.Hosting 11 in PR #8 passed locked restore and compilation,
  then [failed two diagnostic/shutdown cases](https://github.com/alphasixtyfive/ResoDrive/actions/runs/46130089348).
  `RealGenericHostBackgroundServiceFailureIsReportedAsFatal` fails with both
  normal and externally disabled logging: the original worker exception now
  escapes through `Host.StopAsync` / `WaitForShutdownAsync` before the assertions.
  An isolated local reproduction failed both cases with version 11; the same
  two cases passed with version 10. This is a changed host exception contract,
  not a remaining NuGet restore error. The production outer fatal catch already
  exists; these results alone do not establish lost diagnostics or account data.
  The proposal was initially held pending review of that contract and its diagnostics.

The initial shutdown/diagnostic assertions remained intact. The initial acceptance
record was documentation only; no runtime upgrade or new public release was made
in that maintenance pass.

## Subsequent runtime adoption

The maintainer then authorized merging #10 and completing the Hosting 11
compatibility work in #8, without publishing a release just for the updates.
System.Management #10 was merged after its full compatibility CI passed.

The package's source-linked
[Hosting 11 implementation](https://github.com/dotnet/dotnet/blob/297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f/src/runtime/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs)
retains worker failures, waits for monitoring/service shutdown, then propagates
the original failure or an aggregate from StopAsync. ResoDrive now uses this
contract directly. Its outer fatal handler still records and rethrows failures;
the old logger-owned exception cache and duplicate post-RunAsync throw were removed.
The logger remains synchronous, bounded and sanitized, and no longer owns host
lifecycle decisions.

The regression cases require exact original-exception propagation after cleanup
with both normal and externally disabled logging, redacted diagnostic content,
multiple observable worker failures, normal cancellation, and successful shutdown
after recoverable operation errors. Logged errors alone cannot stop a healthy
host. The 13 focused cases passed locally. The target framework and SDK remain
.NET 10; adopting the library does not require a .NET 11 installation.

Installer IPC authentication, upload protection, account-data guards and wipe
ordering were not changed. Further full-build, constrained-CPU, process and
hosted installer/update results are retained with this PR's validation evidence.
Published 0.3.53 artifacts remain immutable; these source changes are for a future
planned application release.

The first compatibility CI run passed the new Hosting cases but timed out in
three existing startup fixtures together. The migration fixture synchronously
blocked on an asynchronous child-process pipe read. It now awaits readiness,
leaving workers available to the parallel host/activation fixtures. Its five-second
readiness deadline, process-identity assertions and fail-closed checks are intact.
All 448 App tests passed locally with two available CPUs after this fixture change;
the fresh full hosted pipeline remains the merge gate.

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

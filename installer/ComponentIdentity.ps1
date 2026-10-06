function Get-ResoDriveComponentSeed([string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-part MSI version is required.' }
    # A major upgrade removes the old product transactionally before installing
    # new files. Its version-specific component identities support relocation
    # even when a legacy updater initially keeps the old installation directory.
    $bytes = [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes("ResoDrive/install-files/v1/$Version"))
    $guidBytes = [byte[]]$bytes[0..15]
    $guidBytes[7] = ($guidBytes[7] -band 15) -bor 80
    $guidBytes[8] = ($guidBytes[8] -band 63) -bor 128
    return ([guid]::new($guidBytes)).ToString().ToUpperInvariant()
}

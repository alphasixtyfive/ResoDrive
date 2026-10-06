function Get-ResoDriveSourceFingerprint([string]$ProjectRoot) {
    # Include source edits and new nonignored files, but exclude build outputs
    # through the repository's existing ignore rules. Deleted tracked files get
    # an explicit marker so they cannot match a previous source snapshot.
    $sourceFiles = @(git -C $ProjectRoot ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate the build source files.' }
    $sourceEntries = foreach ($relativePath in ($sourceFiles | Sort-Object -Unique)) {
        $path = Join-Path $ProjectRoot $relativePath
        $contentHash = if (Test-Path -LiteralPath $path -PathType Leaf) {
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { 'deleted' }
        "$relativePath`t$contentHash"
    }
    $sourceBytes = [Text.Encoding]::UTF8.GetBytes(($sourceEntries -join "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
}

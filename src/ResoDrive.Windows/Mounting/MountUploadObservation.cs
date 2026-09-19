namespace ResoDrive.Windows;

internal sealed record MountUploadObservation(VfsTransferStatus? Current, VfsTransferStatus? LastKnown)
{
    // Losing contact with rclone must not clear an earlier warning about local work.
    public bool BlocksStop => (Current ?? LastKnown)?.NeedsAttention == true;
    public bool HasCacheError => (Current ?? LastKnown) is { Errors: > 0 } or { OutOfSpace: true };
    public string Description => Current?.Description ?? (LastKnown?.NeedsAttention == true
        ? "Upload status unavailable · Last check: " + LastKnown.Description
        : "Upload status unavailable");
}

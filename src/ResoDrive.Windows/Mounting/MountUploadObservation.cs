using ResoDrive.Core.Domain;

namespace ResoDrive.Windows;

internal sealed record MountUploadObservation(VfsTransferStatus? Current, VfsTransferStatus? LastKnown, bool Checking = false)
{
    internal long Generation { get; init; }
    internal MountUploadObservation DuringCacheChange() => this with
    {
        Current = Current?.NeedsAttention == true ? Current : null,
        LastKnown = Current?.NeedsAttention == true ? Current : LastKnown,
        Checking = true,
    };
    // Losing contact with rclone must not clear an earlier warning about local work.
    public bool BlocksStop => Checking || Current is null || Current.NeedsAttention;
    public bool HasCacheError => (Current ?? LastKnown) is { Errors: > 0 } or { OutOfSpace: true };
    public string Description => Checking ? "Checking uploads · Close open documents and wait for confirmation" : Current?.Description ?? (LastKnown?.NeedsAttention == true
        ? "Upload status unavailable · Last check: " + LastKnown.Description
        : "Upload status unavailable");
}

internal static class MountUploadSnapshot
{
    internal static MountSnapshot WithTransfers(this MountSnapshot snapshot, MountUploadObservation observation)
    {
        var status = observation.Current ?? observation.LastKnown;
        return snapshot with
        {
            UploadsQueued = status?.Queued, UploadsInProgress = status?.Uploading,
            UploadStatusStale = observation.Current is null || observation.Checking, UploadsDirty = status?.Dirty,
            UploadStatusChecking = observation.Checking,
            UploadErrors = status is null ? null : Math.Max(status.Errors, status.OutOfSpace ? 1 : 0),
            UploadBytesRemaining = status?.BytesRemaining,
            UploadSpeedBytesPerSecond = observation.Checking ? null : observation.Current?.SpeedBytesPerSecond,
            UploadEtaSeconds = observation.Checking ? null : observation.Current?.EtaSeconds,
            Uploads = status?.Files ?? [], UploadDetailsTruncated = status?.DetailsTruncated ?? false,
            UploadObservedAt = status?.ObservedAt,
            UploadRecoveryRequired = snapshot.UploadRecoveryRequired && (observation.Current is null || observation.Checking)
        };
    }
}

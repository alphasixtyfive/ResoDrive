namespace ResoDrive.Core.Domain;

public enum MountLifecycle
{
    Stopped,
    Starting,
    Mounted,
    Degraded,
    Stopping,
    WaitingToRestart,
    Failed
}

public sealed record MountSnapshot
{
    public long? UploadsQueued { get; init; }
    public long? UploadsInProgress { get; init; }
    public bool UploadStatusStale { get; init; }
    public bool UploadStatusChecking { get; init; }
    public long? UploadsDirty { get; init; }
    public long? UploadErrors { get; init; }
    public long? UploadBytesRemaining { get; init; }
    public double? UploadSpeedBytesPerSecond { get; init; }
    public double? UploadEtaSeconds { get; init; }
    public IReadOnlyList<MountUploadFile> Uploads { get; init; } = [];
    public bool UploadDetailsTruncated { get; init; }
    public DateTimeOffset? UploadObservedAt { get; init; }
    public bool UploadRecoveryRequired { get; init; }
    public required MountId MountId { get; init; }
    public MountLifecycle Lifecycle { get; init; }
    public string? StatusText { get; init; }
}

public enum MountUploadState { Queued, Uploading, WaitingForClose, Retrying }

public sealed record MountUploadFile
{
    public required string RelativePath { get; init; }
    public MountUploadState State { get; init; }
    public long? TotalBytes { get; init; }
    public long BytesTransferred { get; init; }
    public double? SpeedBytesPerSecond { get; init; }
    public double? EtaSeconds { get; init; }
}

public enum SyncLifecycle
{
    Idle,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record SyncSnapshot
{
    public required MountId MountId { get; init; }
    public required SyncJobId JobId { get; init; }
    public SyncLifecycle Lifecycle { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? StatusText { get; init; }
    public long? BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
    public double? ProgressPercent { get; init; }
    public long? ChecksCompleted { get; init; }
    public long? TotalChecks { get; init; }
    public long? TransfersCompleted { get; init; }
    public long? TotalTransfers { get; init; }
    public long? Errors { get; init; }
    public double? SpeedBytesPerSecond { get; init; }
    public double? EtaSeconds { get; init; }
    public double? ElapsedSeconds { get; init; }
}

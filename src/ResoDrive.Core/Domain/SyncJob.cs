namespace ResoDrive.Core.Domain;

public sealed record SyncJob
{
    public required SyncJobId Id { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public required string LocalPath { get; init; }
    public bool ManagedLocalCopy { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public SyncMode Mode { get; init; } = SyncMode.CopyToRemote;
    public SyncSchedule Schedule { get; init; } = SyncSchedule.Manual;
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

public enum SyncMode
{
    CopyToRemote,
    CopyFromRemote,
    SyncToRemote,
    SyncFromRemote,
    Bisync
}

public sealed record SyncSchedule
{
    public static SyncSchedule Manual { get; } = new();

    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
    public bool RunOnApplicationStart { get; init; }
}

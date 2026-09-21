namespace ResoDrive.Core.Settings;

public sealed record ManagerSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Revision { get; init; }
    public ApplicationSettings Application { get; init; } = new();
    public IReadOnlyList<MountSettings> Mounts { get; init; } = Array.Empty<MountSettings>();
}

public sealed record ApplicationSettings
{
    public bool MinimizeToTray { get; init; } = true;
    public bool StartWithWindows { get; init; }
}

public sealed record MountSettings
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required string RemoteName { get; init; }
    public string? ConnectionHost { get; init; }
    public string? ConnectionType { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public MountTargetSettings Target { get; init; } = new();
    public bool Enabled { get; init; } = true;
    public string AutoMount { get; init; } = "never";
    public RestartSettings Restart { get; init; } = new();
    public string[] Arguments { get; init; } = Array.Empty<string>();
    public SyncJobSettings[] SyncJobs { get; init; } = Array.Empty<SyncJobSettings>();
}

public sealed record MountTargetSettings
{
    public string Kind { get; init; } = "drive";
    public char? DriveLetter { get; init; }
    public string? DirectoryPath { get; init; }
}

public sealed record RestartSettings
{
    public bool Enabled { get; init; } = true;
    public int MaximumAttempts { get; init; }
    public int InitialDelaySeconds { get; init; } = 5;
    public int MaximumDelaySeconds { get; init; } = 300;
}

public sealed record SyncJobSettings
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public required string LocalPath { get; init; }
    public bool ManagedLocalCopy { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public string Mode { get; init; } = "copyToRemote";
    public SyncScheduleSettings Schedule { get; init; } = new();
    public string[] Arguments { get; init; } = Array.Empty<string>();
}

public sealed record SyncScheduleSettings
{
    public bool Enabled { get; init; }
    public int IntervalMinutes { get; init; } = 60;
    public bool RunOnApplicationStart { get; init; }
}

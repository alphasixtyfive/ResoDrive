using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;

namespace ResoDrive.Windows;

public sealed class MountDefinitionMapper
{
    public static OperationResult<MountDefinition> ToDomain(MountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Target is null || settings.Restart is null || settings.SyncJobs is null ||
            settings.Arguments is null)
        {
            return Result.Failure<MountDefinition>(
                "mount.settings_incomplete",
                "The stored drive settings are incomplete.");
        }

        if (!Enum.TryParse<AutoMountPolicy>(settings.AutoMount, true, out var autoMount))
        {
            return Result.Failure<MountDefinition>("mount.auto_mount", "The auto-mount policy is invalid.");
        }

        var targetResult = CreateTarget(settings.Target);
        if (!targetResult.Succeeded || targetResult.Value is null)
        {
            return Result.Failure<MountDefinition>(
                targetResult.Error?.Code ?? "mount.target",
                targetResult.Error?.Message ?? "The mount target is invalid.");
        }

        var jobs = new List<SyncJob>();
        foreach (var job in settings.SyncJobs)
        {
            if (job is null || job.Schedule is null || job.Arguments is null)
            {
                return Result.Failure<MountDefinition>(
                    "sync.settings_incomplete",
                    "The stored sync job settings are incomplete.");
            }
            if (!Enum.TryParse<SyncMode>(job.Mode, true, out var mode))
            {
                return Result.Failure<MountDefinition>("sync.mode", $"Sync job '{job.DisplayName}' has an invalid mode.");
            }

            jobs.Add(new SyncJob
            {
                Id = new SyncJobId(job.Id),
                DisplayName = job.DisplayName,
                Enabled = job.Enabled,
                LocalPath = job.LocalPath,
                ManagedLocalCopy = job.ManagedLocalCopy,
                RemotePath = job.RemotePath,
                Mode = mode,
                Arguments = job.Arguments,
                Schedule = new SyncSchedule
                {
                    Enabled = job.Schedule.Enabled,
                    Interval = TimeSpan.FromMinutes(job.Schedule.IntervalMinutes),
                    RunOnApplicationStart = job.Schedule.RunOnApplicationStart
                }
            });
        }

        var definition = new MountDefinition
        {
            Id = new MountId(settings.Id),
            DisplayName = settings.DisplayName,
            RemoteName = settings.RemoteName,
            ConnectionHost = settings.ConnectionHost,
            ConnectionType = settings.ConnectionType,
            RemotePath = settings.RemotePath,
            Target = targetResult.Value,
            Enabled = settings.Enabled,
            AutoMount = autoMount,
            Arguments = settings.Arguments,
            Restart = new RestartPolicy
            {
                Enabled = settings.Restart.Enabled,
                MaximumAttempts = settings.Restart.MaximumAttempts,
                InitialDelay = TimeSpan.FromSeconds(settings.Restart.InitialDelaySeconds),
                MaximumDelay = TimeSpan.FromSeconds(settings.Restart.MaximumDelaySeconds)
            },
            SyncJobs = jobs
        };
        var validation = new ResoDrive.Core.Validation.MountDefinitionValidator().Validate(definition);
        return validation.IsValid
            ? Result.Success(definition)
            : Result.Failure<MountDefinition>(
                validation.Issues[0].Code,
                validation.Issues[0].Message);
    }

    public static MountSettings ToSettings(MountDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var target = definition.Target switch
        {
            MountTarget.Drive drive => new MountTargetSettings { Kind = "drive", DriveLetter = drive.Letter },
            MountTarget.Directory directory => new MountTargetSettings { Kind = "directory", DirectoryPath = directory.Path },
            _ => throw new InvalidOperationException("Unsupported mount target.")
        };

        return new MountSettings
        {
            Id = definition.Id.Value,
            DisplayName = definition.DisplayName,
            RemoteName = definition.RemoteName,
            ConnectionHost = definition.ConnectionHost,
            ConnectionType = definition.ConnectionType,
            RemotePath = definition.RemotePath,
            Target = target,
            Enabled = definition.Enabled,
            AutoMount = definition.AutoMount.ToString(),
            Arguments = definition.Arguments.ToArray(),
            Restart = new RestartSettings
            {
                Enabled = definition.Restart.Enabled,
                MaximumAttempts = definition.Restart.MaximumAttempts,
                InitialDelaySeconds = checked((int)definition.Restart.InitialDelay.TotalSeconds),
                MaximumDelaySeconds = checked((int)definition.Restart.MaximumDelay.TotalSeconds)
            },
            SyncJobs = definition.SyncJobs.Select(ToSettings).ToArray()
        };
    }

    private static SyncJobSettings ToSettings(SyncJob job) => new()
    {
        Id = job.Id.Value,
        DisplayName = job.DisplayName,
        Enabled = job.Enabled,
        LocalPath = job.LocalPath,
        ManagedLocalCopy = job.ManagedLocalCopy,
        RemotePath = job.RemotePath,
        Mode = job.Mode.ToString(),
        Arguments = job.Arguments.ToArray(),
        Schedule = new SyncScheduleSettings
        {
            Enabled = job.Schedule.Enabled,
            IntervalMinutes = checked((int)job.Schedule.Interval.TotalMinutes),
            RunOnApplicationStart = job.Schedule.RunOnApplicationStart
        }
    };

    private static OperationResult<MountTarget> CreateTarget(MountTargetSettings settings)
    {
        if (string.Equals(settings.Kind, "drive", StringComparison.OrdinalIgnoreCase) && settings.DriveLetter is char letter)
        {
            return Result.Success<MountTarget>(new MountTarget.Drive(letter));
        }

        if (string.Equals(settings.Kind, "directory", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.DirectoryPath))
        {
            return Result.Success<MountTarget>(new MountTarget.Directory(settings.DirectoryPath));
        }

        return Result.Failure<MountTarget>("mount.target", "A drive letter or directory target is required.");
    }
}

using System.Collections.Concurrent;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Validation;

namespace ResoDrive.Windows;

public sealed class RcloneSyncCoordinator : IDisposable
{
    private readonly string _rclonePath;
    private readonly string _configPath;
    private readonly ApplicationPaths _paths;
    private readonly Func<IReadOnlyList<MountDefinition>> _definitionProvider;
    private readonly IRcloneProcessRunner _processRunner;
    private readonly SyncRunStateStore _runStateStore;
    private readonly ConcurrentDictionary<SyncJobId, CancellationTokenSource> _runs = new();
    private readonly ConcurrentDictionary<SyncJobId, SyncSnapshot> _snapshots = new();

    public RcloneSyncCoordinator(
        string rclonePath,
        string configPath,
        ApplicationPaths paths,
        Func<IReadOnlyList<MountDefinition>> definitionProvider)
        : this(rclonePath, configPath, paths, definitionProvider, new RcloneProcessRunner())
    {
    }

    internal RcloneSyncCoordinator(
        string rclonePath,
        string configPath,
        ApplicationPaths paths,
        Func<IReadOnlyList<MountDefinition>> definitionProvider,
        IRcloneProcessRunner processRunner)
    {
        _rclonePath = Path.GetFullPath(rclonePath);
        _configPath = Path.GetFullPath(configPath);
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _definitionProvider = definitionProvider ?? throw new ArgumentNullException(nameof(definitionProvider));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _runStateStore = new SyncRunStateStore(paths);
        foreach (var snapshot in _runStateStore.Load())
        {
            _snapshots[snapshot.JobId] = snapshot;
        }
    }

    public IReadOnlyList<SyncSnapshot> GetSnapshots()
    {
        var currentJobs = _definitionProvider()
            .SelectMany(mount => mount.SyncJobs.Select(job => (JobId: job.Id, MountId: mount.Id)))
            .ToDictionary(item => item.JobId, item => item.MountId);
        return _snapshots.Values
            .Where(snapshot => currentJobs.TryGetValue(snapshot.JobId, out var mountId) &&
                mountId == snapshot.MountId)
            .OrderBy(snapshot => snapshot.JobId.Value)
            .ToArray();
    }

    public void MarkQueued(MountId mountId, SyncJobId syncJobId)
    {
        Publish(new SyncSnapshot
        {
            MountId = mountId,
            JobId = syncJobId,
            Lifecycle = SyncLifecycle.Queued,
            StatusText = "Queued"
        });
    }

    public async Task MarkCancelledAsync(MountId mountId, SyncJobId syncJobId)
    {
        await PublishFinalAsync(new SyncSnapshot
        {
            MountId = mountId,
            JobId = syncJobId,
            Lifecycle = SyncLifecycle.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            StatusText = "Cancelled"
        }).ConfigureAwait(false);
    }

    public async Task<OperationResult> RunAsync(
        MountId mountId,
        SyncJobId syncJobId,
        CancellationToken cancellationToken = default)
    {
        var definitions = _definitionProvider();
        var definition = definitions.FirstOrDefault(item => item.Id == mountId);
        var job = definition?.SyncJobs.FirstOrDefault(item => item.Id == syncJobId);
        if (definition is null || job is null)
        {
            return Result.Failure("sync.not_found", "The sync job no longer exists.");
        }

        if (!job.Enabled)
        {
            return Result.Failure("sync.disabled", "Enable this sync job before running it.");
        }

        if (job.Mode == SyncMode.Bisync)
        {
            return Result.Failure(
                "sync.bisync_not_enabled",
                "Bidirectional sync is not enabled until its recovery workflow is configured.");
        }

        var validation = new SyncJobValidator().Validate(job);
        if (!validation.IsValid)
        {
            return Result.Failure("sync.invalid", validation.Issues[0].Message);
        }

        if (definitions.Any(item => OverlapsMountTarget(job.LocalPath, item.Target)))
        {
            return Result.Failure(
                "sync.recursive_path",
                "The local sync path cannot contain or be inside a drive managed by ResoDrive.");
        }

        if (PathsOverlap(job.LocalPath, _paths.Root) ||
            PathsOverlap(job.LocalPath, AppContext.BaseDirectory))
        {
            return Result.Failure(
                "sync.protected_path",
                "The local sync path cannot contain or be inside ResoDrive application data or program files.");
        }

        var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_runs.TryAdd(syncJobId, runSource))
        {
            runSource.Dispose();
            return Result.Failure("sync.already_running", "This sync job is already running.");
        }

        Publish(new SyncSnapshot
        {
            MountId = mountId,
            JobId = syncJobId,
            Lifecycle = SyncLifecycle.Running,
            StatusText = "Running…"
        });

        RcloneSyncStats? latestStats = null;
        try
        {
            using var logWriter = TryOpenStructuredLog(
                Path.Combine(_paths.Logs, RcloneLogFileName.ForSync(definition, job)));
            var result = await _processRunner.RunAsync(
                _rclonePath,
                BuildArguments(definition, job).ToArray(),
                Timeout.InfiniteTimeSpan,
                runSource.Token,
                line =>
                {
                    var logEvent = RcloneJsonLogParser.TryParse(line, out var parsed) && parsed is not null
                        ? parsed
                        : RcloneJsonLogEvent.PlainText(line);
                    TryWriteLogEvent(logWriter, logEvent);
                    if (logEvent.Stats is { } stats)
                    {
                        latestStats = stats;
                        PublishProgress(mountId, syncJobId, stats);
                    }
                }).ConfigureAwait(false);
            var succeeded = result.ExitCode == 0 && !result.TimedOut;
            await PublishFinalAsync(CreateSnapshot(
                mountId,
                syncJobId,
                succeeded ? SyncLifecycle.Succeeded : SyncLifecycle.Failed,
                succeeded ? CompletionStatus(latestStats) : SafeError(result.StandardError),
                DateTimeOffset.UtcNow,
                latestStats)).ConfigureAwait(false);
            return succeeded
                ? Result.Success()
                : Result.Failure("sync.rclone_failed", SafeError(result.StandardError), true);
        }
        catch (OperationCanceledException)
        {
            await PublishFinalAsync(CreateSnapshot(
                mountId,
                syncJobId,
                SyncLifecycle.Cancelled,
                "Cancelled",
                DateTimeOffset.UtcNow,
                latestStats)).ConfigureAwait(false);
            return Result.Failure("sync.cancelled", "The sync job was cancelled.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            var message = SafeError(exception.Message);
            await PublishFinalAsync(CreateSnapshot(
                mountId,
                syncJobId,
                SyncLifecycle.Failed,
                message,
                DateTimeOffset.UtcNow,
                latestStats)).ConfigureAwait(false);
            return Result.Failure("sync.launch_failed", message, true);
        }
        finally
        {
            if (_runs.TryRemove(syncJobId, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    public Task<OperationResult> CancelAsync(
        MountId mountId,
        SyncJobId syncJobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_definitionProvider().Any(mount =>
                mount.Id == mountId && mount.SyncJobs.Any(job => job.Id == syncJobId)))
        {
            return Task.FromResult(Result.Failure("sync.not_found", "The sync job no longer exists."));
        }
        if (!_runs.TryGetValue(syncJobId, out var source))
        {
            return Task.FromResult(Result.Failure("sync.not_running", "The sync job is not running."));
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(Result.Failure("sync.not_running", "The sync job is not running."));
        }
        return Task.FromResult(Result.Success());
    }

    public void Dispose()
    {
        foreach (var source in _runs.Values)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        _runStateStore.Dispose();
    }

    private IEnumerable<string> BuildArguments(MountDefinition mount, SyncJob job)
    {
        var remote = RemotePathUtility.FormatSource(
            mount.RemoteName,
            RemotePathUtility.Combine(mount.RemotePath, job.RemotePath));
        var (command, source, destination) = job.Mode switch
        {
            SyncMode.CopyToRemote => ("copy", job.LocalPath, remote),
            SyncMode.CopyFromRemote => ("copy", remote, job.LocalPath),
            SyncMode.SyncToRemote => ("sync", job.LocalPath, remote),
            SyncMode.SyncFromRemote => ("sync", remote, job.LocalPath),
            _ => throw new InvalidOperationException("Unsupported sync mode.")
        };

        yield return command;
        yield return source;
        yield return destination;
        yield return "--config";
        yield return _configPath;
        yield return "--ask-password=false";
        foreach (var argument in RcloneUserAgentArguments.Create(job.Arguments))
            yield return argument;
        if (File.Exists(_paths.ConfigSecretFile))
        {
            yield return "--password-command";
            yield return RclonePasswordCommand.Create();
        }

        // JSON logging exposes the official structured `stats` object on stderr.
        // The process runner drains it continuously, so progress never blocks rclone.
        yield return "--use-json-log";
        yield return "--log-level";
        yield return "INFO";
        yield return "--stats";
        yield return "2s";
        foreach (var argument in job.Arguments)
        {
            yield return argument;
        }
    }

    private static bool OverlapsMountTarget(string localPath, MountTarget target)
    {
        var targetPath = target switch
        {
            MountTarget.Drive drive => $"{drive.Letter}:\\",
            MountTarget.Directory directory => directory.Path,
            _ => throw new InvalidOperationException("Unsupported mount target.")
        };
        return PathsOverlap(localPath, targetPath);
    }

    internal static bool PathsOverlap(string firstPath, string secondPath) =>
        IsSameOrDescendant(firstPath, secondPath) || IsSameOrDescendant(secondPath, firstPath);

    private static bool IsSameOrDescendant(string path, string candidateAncestor)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateAncestor));
        if (fullPath.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = Path.GetRelativePath(ancestor, fullPath);
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string SafeError(string error)
    {
        var line = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (line is not null &&
            RcloneJsonLogParser.TryParse(line, out var parsed) &&
            !string.IsNullOrWhiteSpace(parsed?.Message))
            line = parsed.Message;
        return RcloneErrorMessage.Clean(line, "rclone reported a transfer error.");
    }

    private void PublishProgress(MountId mountId, SyncJobId syncJobId, RcloneSyncStats stats) =>
        Publish(CreateSnapshot(
            mountId,
            syncJobId,
            SyncLifecycle.Running,
            "Syncing",
            completedAt: null,
            stats));

    private static RcloneStructuredLogWriter? TryOpenStructuredLog(string path)
    {
        try
        {
            return new RcloneStructuredLogWriter(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    private static void TryWriteLogEvent(
        RcloneStructuredLogWriter? writer,
        RcloneJsonLogEvent value)
    {
        if (writer is null)
        {
            return;
        }
        try
        {
            writer.Write(value);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging is diagnostic and must not interrupt a transfer.
        }
    }

    private static string CompletionStatus(RcloneSyncStats? stats)
    {
        if (stats is null)
            return "Completed";
        return stats.Bytes == 0 && stats.Transfers == 0
            ? "No changes"
            : "Completed";
    }

    private static SyncSnapshot CreateSnapshot(
        MountId mountId,
        SyncJobId syncJobId,
        SyncLifecycle lifecycle,
        string status,
        DateTimeOffset? completedAt,
        RcloneSyncStats? stats)
    {
        double? percentage = stats is { TotalBytes: > 0 }
            ? Math.Clamp(stats.Bytes * 100d / stats.TotalBytes, 0d, 100d)
            : null;
        return new SyncSnapshot
        {
            MountId = mountId,
            JobId = syncJobId,
            Lifecycle = lifecycle,
            CompletedAt = completedAt,
            StatusText = status,
            BytesTransferred = stats?.Bytes,
            TotalBytes = stats is { TotalBytes: > 0 } ? stats.TotalBytes : null,
            ProgressPercent = percentage,
            ChecksCompleted = stats?.Checks,
            TotalChecks = stats is { TotalChecks: > 0 } ? stats.TotalChecks : null,
            TransfersCompleted = stats?.Transfers,
            TotalTransfers = stats is { TotalTransfers: > 0 } ? stats.TotalTransfers : null,
            Errors = stats?.Errors,
            SpeedBytesPerSecond = stats is { Speed: > 0 } ? stats.Speed : null,
            EtaSeconds = stats?.EtaSeconds,
            ElapsedSeconds = stats?.ElapsedSeconds
        };
    }

    private void Publish(SyncSnapshot snapshot)
    {
        _snapshots[snapshot.JobId] = snapshot;
    }

    private async Task PublishFinalAsync(SyncSnapshot snapshot)
    {
        Publish(snapshot);
        await _runStateStore.SaveAsync(snapshot).ConfigureAwait(false);
    }
}

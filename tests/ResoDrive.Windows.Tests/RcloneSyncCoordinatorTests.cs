using ResoDrive.Core.Domain;

namespace ResoDrive.Windows.Tests;

public sealed class RcloneSyncCoordinatorTests
{
    [Fact]
    public async Task CancelAsync_RejectsJobFromDifferentMount()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.CancelAsync(new MountId(Guid.NewGuid()), job.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("sync.not_found", result.Error?.Code);
    }

    [Fact]
    public void MarkQueued_PublishesImmediateStatus()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        coordinator.MarkQueued(mount.Id, job.Id);

        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.Equal(SyncLifecycle.Queued, snapshot.Lifecycle);
        Assert.Equal("Queued", snapshot.StatusText);
    }

    [Fact]
    public async Task MarkCancelled_ReplacesQueuedStatus()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);
        coordinator.MarkQueued(mount.Id, job.Id);

        await coordinator.MarkCancelledAsync(mount.Id, job.Id);

        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.Equal(SyncLifecycle.Cancelled, snapshot.Lifecycle);
        Assert.Equal("Cancelled", snapshot.StatusText);
        Assert.NotNull(snapshot.CompletedAt);
    }

    [Fact]
    public async Task CompletedOutcome_IsRestoredAfterCoordinatorRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);

        using (var first = new RcloneSyncCoordinator(
                   Path.Combine(root, "rclone.exe"),
                   paths.ConfigFile,
                   paths,
                   () => [mount],
                   runner))
        {
            Assert.True((await first.RunAsync(mount.Id, job.Id)).Succeeded);
        }

        using var restarted = new RcloneSyncCoordinator(
            Path.Combine(root, "rclone.exe"),
            paths.ConfigFile,
            paths,
            () => [mount],
            new RecordingRunner());
        var restored = Assert.Single(restarted.GetSnapshots());
        Assert.Equal(mount.Id, restored.MountId);
        Assert.Equal(job.Id, restored.JobId);
        Assert.Equal(SyncLifecycle.Succeeded, restored.Lifecycle);
        Assert.Equal("Completed", restored.StatusText);
        Assert.NotNull(restored.CompletedAt);
    }

    [Fact]
    public void CorruptOutcomeState_DoesNotPreventCoordinatorStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SyncRunStateFile, "not valid json");
        var (mount, _) = CreateDefinition(enabled: true);

        using var coordinator = new RcloneSyncCoordinator(
            Path.Combine(root, "rclone.exe"),
            paths.ConfigFile,
            paths,
            () => [mount],
            new RecordingRunner());

        Assert.Empty(coordinator.GetSnapshots());
    }

    [Fact]
    public async Task RestoredOutcome_IsNotReportedForDeletedJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var (mount, job) = CreateDefinition(enabled: true);
        using (var first = new RcloneSyncCoordinator(
                   Path.Combine(root, "rclone.exe"),
                   paths.ConfigFile,
                   paths,
                   () => [mount],
                   new RecordingRunner()))
        {
            Assert.True((await first.RunAsync(mount.Id, job.Id)).Succeeded);
        }

        using var restarted = new RcloneSyncCoordinator(
            Path.Combine(root, "rclone.exe"),
            paths.ConfigFile,
            paths,
            () => [],
            new RecordingRunner());

        Assert.Empty(restarted.GetSnapshots());
    }

    [Fact]
    public async Task RunAsync_RejectsDisabledJobWithoutStartingRclone()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: false);
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("sync.disabled", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task RunAsync_ReturnsValidationFailureBeforeInspectingInvalidPath()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        job = job with { LocalPath = "not a fully qualified path" };
        mount = mount with { SyncJobs = [job] };
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.invalid", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task RunAsync_HasNoArbitraryTransferTimeout()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(Timeout.InfiniteTimeSpan, runner.Timeout);
        Assert.Contains("--ask-password=false", runner.Arguments);
        Assert.DoesNotContain("--dry-run", runner.Arguments);
    }

    [Fact]
    public async Task RunAsync_IdentifiesResoDriveUnlessUserAgentIsOverridden()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RCLONE_USER_AGENT")))
            AssertOption(runner.Arguments, "--user-agent", ClientUserAgent.Value);
        else
            Assert.DoesNotContain("--user-agent", runner.Arguments);
    }

    [Fact]
    public async Task RunAsync_RequestsStructuredPeriodicStats()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded);
        Assert.Contains("--use-json-log", runner.Arguments);
        AssertOption(runner.Arguments, "--stats", "2s");
    }

    [Fact]
    public async Task RunAsync_PublishesStructuredByteProgress()
    {
        var runner = new RecordingRunner
        {
            StandardErrorLines =
            [
                "{\"level\":\"info\",\"stats\":{\"bytes\":524288,\"totalBytes\":1048576,\"checks\":18,\"totalChecks\":20,\"transfers\":3,\"totalTransfers\":8,\"speed\":262144,\"eta\":12}}"
            ]
        };
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        var running = coordinator.RunAsync(mount.Id, job.Id);
        await runner.ProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.Equal(SyncLifecycle.Running, snapshot.Lifecycle);
        Assert.Equal(524288L, snapshot.BytesTransferred);
        Assert.Equal(1048576L, snapshot.TotalBytes);
        Assert.Equal(50d, snapshot.ProgressPercent);
        Assert.Equal("Syncing", snapshot.StatusText);
        Assert.Equal(18, snapshot.ChecksCompleted);
        Assert.Equal(20, snapshot.TotalChecks);
        Assert.Equal(3, snapshot.TransfersCompleted);
        Assert.Equal(8, snapshot.TotalTransfers);
        Assert.Equal(262144, snapshot.SpeedBytesPerSecond);
        Assert.Equal(12, snapshot.EtaSeconds);
        runner.Complete();
        Assert.True((await running).Succeeded);
    }

    [Fact]
    public async Task RunAsync_RejectsLocalPathInsideItsOwnMount()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        mount = mount with { Target = new MountTarget.Drive('C') };
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.recursive_path", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsLocalPathInsideAnotherManagedMount()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        job = job with { LocalPath = @"S:\Backups" };
        mount = mount with { SyncJobs = [job] };
        var otherMount = mount with
        {
            Id = MountId.New(),
            Target = new MountTarget.Drive('S'),
            SyncJobs = [],
        };
        using var coordinator = CreateCoordinator([mount, otherMount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.recursive_path", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsLocalPathContainingManagedDirectoryMount()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        job = job with { LocalPath = @"C:\Data" };
        mount = mount with { SyncJobs = [job] };
        var nestedMount = mount with
        {
            Id = MountId.New(),
            DisplayName = "Nested",
            Target = new MountTarget.Directory(@"C:\Data\MountedRemote"),
            SyncJobs = [],
        };
        using var coordinator = CreateCoordinator([mount, nestedMount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.recursive_path", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsApplicationDataPath()
    {
        var runner = new RecordingRunner();
        var root = Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        var (mount, job) = CreateDefinition(enabled: true);
        job = job with { LocalPath = Path.Combine(root, "logs") };
        mount = mount with { SyncJobs = [job] };
        using var coordinator = new RcloneSyncCoordinator(
            Path.Combine(root, "rclone.exe"),
            paths.ConfigFile,
            paths,
            () => [mount],
            runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.protected_path", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData(@"C:\Data", @"C:\Data\Child")]
    [InlineData(@"C:\Data\Child", @"C:\Data")]
    [InlineData(@"C:\Data", @"c:\data")]
    public void PathsOverlap_DetectsBothDirections(string first, string second)
    {
        Assert.True(RcloneSyncCoordinator.PathsOverlap(first, second));
    }

    [Fact]
    public void PathsOverlap_DoesNotConfuseSiblingPrefixes()
    {
        Assert.False(RcloneSyncCoordinator.PathsOverlap(@"C:\Data", @"C:\Database"));
    }

    [Theory]
    [InlineData(SyncMode.CopyToRemote, "copy", @"C:\Data", "storage:base/documents")]
    [InlineData(SyncMode.CopyFromRemote, "copy", "storage:base/documents", @"C:\Data")]
    [InlineData(SyncMode.SyncToRemote, "sync", @"C:\Data", "storage:base/documents")]
    [InlineData(SyncMode.SyncFromRemote, "sync", "storage:base/documents", @"C:\Data")]
    public async Task RunAsync_MapsEverySupportedMode(
        SyncMode mode,
        string command,
        string source,
        string destination)
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true, mode);
        mount = mount with { RemotePath = "base" };
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded);
        Assert.Equal([command, source, destination], runner.Arguments.Take(3));
    }

    [Fact]
    public async Task RunAsync_PreservesAbsoluteMountPathInRemoteSource()
    {
        var runner = new RecordingRunner();
        var (mount, job) = CreateDefinition(enabled: true);
        mount = mount with { RemotePath = "/srv/harbour" };
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded);
        Assert.Equal("storage:/srv/harbour/documents", runner.Arguments[2]);
    }

    [Fact]
    public async Task RunAsync_PublishesFailureWhenRcloneCannotStart()
    {
        var runner = new RecordingRunner { Exception = new InvalidOperationException("rclone unavailable") };
        var (mount, job) = CreateDefinition(enabled: true);
        using var coordinator = CreateCoordinator(mount, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.launch_failed", result.Error?.Code);
        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.Equal(SyncLifecycle.Failed, snapshot.Lifecycle);
        Assert.Contains("rclone unavailable", snapshot.StatusText);
    }

    private static RcloneSyncCoordinator CreateCoordinator(MountDefinition mount, RecordingRunner runner)
        => CreateCoordinator([mount], runner);

    private static RcloneSyncCoordinator CreateCoordinator(
        IReadOnlyList<MountDefinition> mounts,
        RecordingRunner runner)
    {
        var root = Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(root);
        return new RcloneSyncCoordinator(
            Path.Combine(root, "rclone.exe"),
            paths.ConfigFile,
            paths,
            () => mounts,
            runner);
    }

    private static (MountDefinition Mount, SyncJob Job) CreateDefinition(
        bool enabled,
        SyncMode mode = SyncMode.CopyToRemote)
    {
        var job = new SyncJob
        {
            Id = SyncJobId.New(),
            DisplayName = "Documents",
            Enabled = enabled,
            LocalPath = @"C:\Data",
            RemotePath = "documents",
            Mode = mode
        };
        var mount = new MountDefinition
        {
            Id = MountId.New(),
            DisplayName = "Storage",
            RemoteName = "storage",
            Target = new MountTarget.Drive('R'),
            SyncJobs = [job]
        };
        return (mount, job);
    }

    private static void AssertOption(
        IReadOnlyList<string> arguments,
        string option,
        string expectedValue)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private sealed class RecordingRunner : IRcloneProcessRunner
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public TimeSpan Timeout { get; private set; }
        public Exception? Exception { get; init; }
        public IReadOnlyList<string> StandardErrorLines { get; init; } = [];
        public TaskCompletionSource ProgressReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProcessRunResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessRunResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string>? standardErrorLineReceived = null)
        {
            if (Exception is not null)
            {
                throw Exception;
            }
            CallCount++;
            Arguments = arguments;
            Timeout = timeout;
            foreach (var line in StandardErrorLines)
            {
                standardErrorLineReceived?.Invoke(line);
                ProgressReported.TrySetResult();
            }
            return StandardErrorLines.Count == 0
                ? Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false))
                : _completion.Task;
        }

        public void Complete() =>
            _completion.TrySetResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
    }
}

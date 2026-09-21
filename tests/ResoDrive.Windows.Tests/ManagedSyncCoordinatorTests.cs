using System.Diagnostics;
using ResoDrive.Core.Domain;

namespace ResoDrive.Windows.Tests;

public sealed class ManagedSyncCoordinatorTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(
        Path.GetTempPath(), "resodrive-managed-sync-tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths _paths;

    public ManagedSyncCoordinatorTests()
    {
        _paths = new ApplicationPaths(Path.Combine(_fixtureRoot, "account-data"));
        _paths.EnsureCreated();
    }

    [Theory]
    [InlineData(SyncMode.CopyFromRemote, "copy")]
    [InlineData(SyncMode.SyncFromRemote, "sync")]
    public async Task EnrolledDownload_CreatesDedicatedFolderBeforeLaunching(
        SyncMode mode, string command)
    {
        var (mount, job) = CreateDefinition(mode);
        await EnrollAsync(mount.Id);
        var runner = new RecordingRunner(arguments =>
        {
            Assert.True(Directory.Exists(job.LocalPath));
            Assert.Equal([command, "storage:base/documents", job.LocalPath], arguments.Take(3));
        });
        using var coordinator = CreateCoordinator([mount], runner);
        Assert.False(Directory.Exists(job.LocalPath));

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(SyncLifecycle.Succeeded, Assert.Single(coordinator.GetSnapshots()).Lifecycle);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("parent")]
    [InlineData("other-job")]
    [InlineData("child")]
    public async Task ManagedFlag_CannotClaimAnArbitraryFolder(string destination)
    {
        var (mount, job) = CreateDefinition();
        var expectedPath = job.LocalPath;
        job = job with
        {
            LocalPath = destination switch
            {
                "external" => Path.Combine(_fixtureRoot, "ordinary-download"),
                "parent" => _paths.ManagedSyncRoot,
                "other-job" => _paths.ManagedSyncFolder(Guid.NewGuid()),
                _ => Path.Combine(job.LocalPath, "nested")
            }
        };
        mount = mount with { SyncJobs = [job] };
        await EnrollAsync(mount.Id);
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.managed_path", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(expectedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedDownload_RequiresEnrollmentForItsOwnAccount(bool enrollDifferentAccount)
    {
        var (mount, job) = CreateDefinition();
        if (enrollDifferentAccount) await EnrollAsync(MountId.New());
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.launch_failed", result.Error?.Code);
        Assert.Contains("Reconnect this account", result.Error!.Message);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(job.LocalPath));
    }

    [Fact]
    public async Task UnreadableEnrollment_BlocksManagedDownloadWithoutThrowing()
    {
        var (mount, job) = CreateDefinition();
        await File.WriteAllTextAsync(_paths.RemoteWipeFile, "not a protected enrollment file");
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.launch_failed", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(job.LocalPath));
    }

    [Fact]
    public async Task ExistingJob_DoesNotBecomeManagedJustByUsingTheDedicatedPath()
    {
        var (mount, job) = CreateDefinition();
        job = job with { ManagedLocalCopy = false };
        mount = mount with { SyncJobs = [job] };
        await EnrollAsync(mount.Id);
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.protected_path", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(job.LocalPath));
    }

    [Fact]
    public async Task ExistingExternalDownload_RemainsUsableWithoutEnrollment()
    {
        var (mount, job) = CreateDefinition();
        job = job with { ManagedLocalCopy = false, LocalPath = Path.Combine(_fixtureRoot, "downloads") };
        mount = mount with { SyncJobs = [job] };
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(job.LocalPath, runner.Arguments[2]);
        Assert.False(Directory.Exists(_paths.ManagedSyncRoot));
    }

    [Theory]
    [InlineData(SyncMode.CopyToRemote)]
    [InlineData(SyncMode.SyncToRemote)]
    public async Task ManagedUpload_IsRejectedBeforeStartingRclone(SyncMode mode)
    {
        var (mount, job) = CreateDefinition(mode);
        await EnrollAsync(mount.Id);
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.invalid", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(job.LocalPath));
    }

    [Theory]
    [InlineData("same", false)]
    [InlineData("child", true)]
    [InlineData("external-upload-parent", true)]
    public async Task ManagedDownload_RejectsOverlapWithAnotherJobEvenWhenDisabled(
        string overlap, bool differentAccount)
    {
        var (mount, job) = CreateDefinition();
        var otherJob = job with
        {
            Id = SyncJobId.New(),
            Enabled = false,
            ManagedLocalCopy = false,
            Mode = SyncMode.CopyToRemote,
            LocalPath = overlap switch
            {
                "same" => job.LocalPath,
                "child" => Path.Combine(job.LocalPath, "nested"),
                _ => _fixtureRoot
            }
        };
        var definitions = differentAccount
            ? new[] { mount, mount with { Id = MountId.New(), SyncJobs = [otherJob] } }
            : [mount with { SyncJobs = [job, otherJob] }];
        await EnrollAsync(mount.Id);
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator(definitions, runner);

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.managed_overlap", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(job.LocalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectedManagedRootOrChild_IsRejectedAndExternalDataIsPreserved(bool child)
    {
        var (mount, job) = CreateDefinition();
        await EnrollAsync(mount.Id);
        var outside = Path.Combine(_fixtureRoot, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "outside the managed copy");
        if (child) Directory.CreateDirectory(job.LocalPath);
        var junction = child ? Path.Combine(job.LocalPath, "redirected") : _paths.ManagedSyncRoot;
        await CreateJunctionAsync(junction, outside);
        try
        {
            var runner = new RecordingRunner();
            using var coordinator = CreateCoordinator([mount], runner);

            var result = await coordinator.RunAsync(mount.Id, job.Id);

            Assert.Equal("sync.launch_failed", result.Error?.Code);
            Assert.Equal(0, runner.CallCount);
            Assert.Equal("outside the managed copy", await File.ReadAllTextAsync(sentinel));
            Assert.Equal([sentinel], Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Theory]
    [InlineData(RemoteWipePhase.Requested)]
    [InlineData(RemoteWipePhase.Cleaned)]
    public async Task PendingWipe_BlocksManagedDirectoryCreationAndRunner(RemoteWipePhase phase)
    {
        var (mount, job) = CreateDefinition();
        await EnrollAsync(mount.Id);
        var runner = new RecordingRunner();
        using var coordinator = CreateCoordinator([mount], runner);
        await new RemoteWipeStateStore(_paths).SaveAsync(
            new RemoteWipeState(Guid.NewGuid(), phase, Registration(mount.Id)));

        var result = await coordinator.RunAsync(mount.Id, job.Id);

        Assert.Equal("sync.launch_failed", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(Directory.Exists(job.LocalPath));
    }

    private (MountDefinition Mount, SyncJob Job) CreateDefinition(SyncMode mode = SyncMode.CopyFromRemote)
    {
        var id = SyncJobId.New();
        var job = new SyncJob
        {
            Id = id,
            DisplayName = "Managed documents",
            LocalPath = _paths.ManagedSyncFolder(id.Value),
            ManagedLocalCopy = true,
            RemotePath = "documents",
            Mode = mode
        };
        return (new MountDefinition
        {
            Id = MountId.New(),
            DisplayName = "Storage",
            RemoteName = "storage",
            RemotePath = "base",
            Target = new MountTarget.Directory(Path.Combine(_fixtureRoot, "mounted-remote")),
            SyncJobs = [job]
        }, job);
    }

    private RcloneSyncCoordinator CreateCoordinator(
        IReadOnlyList<MountDefinition> definitions, RecordingRunner runner) =>
        new(_paths.RcloneExecutable, _paths.ConfigFile, _paths, () => definitions, runner);

    private async Task EnrollAsync(MountId mountId)
    {
        var staged = await new RemoteWipeStore(_paths).CreateStagedAsync([Registration(mountId)]);
        File.Move(staged, _paths.RemoteWipeFile);
    }

    private static RemoteWipeRegistration Registration(MountId mountId) => new(
        mountId.Value, "https://cloud.example/remote.php/dav/files/test-user/",
        "https://cloud.example/", "test-user", "disposable-test-token");

    private static async Task CreateJunctionAsync(string junction, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, target })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixtureRoot)) Directory.Delete(_fixtureRoot, recursive: true);
    }

    private sealed class RecordingRunner(Action<IReadOnlyList<string>>? onRun = null) : IRcloneProcessRunner
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Arguments = arguments;
            onRun?.Invoke(arguments);
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
        }
    }
}

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class WorkerQueueTests
{
    [Fact]
    public async Task StopRetiresAConnectionRetryQueuedBehindOtherDrives()
    {
        await using var fixture = new Fixture(connectionWait: true);
        await fixture.WaitForConnectionAsync();
        var release = Signal();
        var entered = new[] { Signal(), Signal() };
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var signal = entered[index];
                fixture.Queue("mount:" + Guid.NewGuid().ToString("N"), async token =>
                {
                    signal.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return Result.Success();
                });
            }
            await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.MakeRetryDue();
            fixture.QueueConnectionRetries();
            Assert.True(fixture.HasMountOperation);
            var result = await fixture.StopDriveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal(MountLifecycle.Stopped, Assert.Single(fixture.Mounts.GetSnapshots()).Lifecycle);
            fixture.MakeRetryDue();
            Assert.Empty(fixture.Mounts.GetConnectionRetryCandidates());
            fixture.QueueConnectionRetries();
            Assert.Equal(0, fixture.Inventory.StartChecks);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsAnInFlightConnectionProbeBeforeTakingTheUploadCheckGate(bool racedIntoStarting)
    {
        var probing = Signal();
        var attempts = 0;
        await using var fixture = new Fixture(connectionWait: true, async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return false;
            probing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        await fixture.WaitForConnectionAsync();
        fixture.MakeRetryDue();
        fixture.QueueConnectionRetries();
        await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (racedIntoStarting) fixture.SetStartingSnapshot();
        var result = await fixture.StopDriveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Succeeded || (racedIntoStarting && result.ErrorCode == "mount.uploads_pending"), result.ErrorMessage);
        // Cancellation may race the conservative upload guard. A rejected guard
        // still must return promptly and preserve its normal explicit-stop boundary.
        if (!result.Succeeded) Assert.True((await fixture.Mounts.StopAsync(fixture.Definition.Id)).Succeeded);
        Assert.Equal(MountLifecycle.Stopped, Assert.Single(fixture.Mounts.GetSnapshots()).Lifecycle);
        Assert.Empty(fixture.Mounts.GetConnectionRetryCandidates());
        Assert.Equal(0, fixture.Inventory.StartChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeverStartedCleanNetworkWaitAllowsOrdinaryHostShutdown(bool probing)
    {
        var entered = Signal();
        var release = Signal();
        var attempts = 0;
        await using var fixture = new Fixture(connectionWait: true, async token =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return false;
        });
        await fixture.WaitForConnectionAsync();
        try
        {
            if (probing)
            {
                fixture.MakeRetryDue();
                fixture.QueueConnectionRetries();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var result = await fixture.CommandAsync(new HostRequest("shutdown", ExpectedHostBaseDirectory: AppContext.BaseDirectory));
            Assert.True(result.Succeeded, result.ErrorMessage);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("sync:", "mount:")]
    [InlineData("mount:", "sync:")]
    public async Task LongTransfersAndMountOperationsCannotStarveEachOther(string occupied, string independent)
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new[] { Signal(), Signal() };
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var entered = started[index];
                Assert.True(fixture.Queue(occupied + Guid.NewGuid().ToString("N"), async token =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return Result.Success();
                }));
            }
            await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var completed = Signal();
            Assert.True(fixture.Queue(independent + Guid.NewGuid().ToString("N"), _ =>
            {
                completed.TrySetResult();
                return Task.FromResult(Result.Success());
            }));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task AbandonedMountQueueBecomesARetryableFailure(string failure)
    {
        await using var fixture = new Fixture();
        Assert.True((await fixture.Mounts.ReconcileAsync([fixture.Definition])).Succeeded);
        var release = Signal();
        var blockers = new[] { Signal(), Signal() };
        try
        {
            if (failure is "timeout" or "cancel")
                for (var index = 0; index < 2; index++)
                {
                    var entered = blockers[index];
                    fixture.Queue("mount:" + Guid.NewGuid().ToString("N"), async token =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                        return Result.Success();
                    });
                }
            if (failure is "timeout" or "cancel")
                await Task.WhenAll(blockers.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var ran = false;
            fixture.Mounts.MarkPending(fixture.Definition.Id, false);
            Assert.True(fixture.Queue($"mount:{fixture.Definition.Id.Value:N}", _ =>
            {
                ran = true;
                return failure == "exception" ? throw new InvalidOperationException("fixture failure")
                    : Task.FromResult(Result.Failure("fixture.failed", "The drive could not start."));
            }, cancellation.Token));
            if (failure == "cancel") cancellation.Cancel();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (fixture.Mounts.GetSnapshots().Single().Lifecycle == MountLifecycle.Starting)
                await Task.Delay(10, deadline.Token);
            var snapshot = Assert.Single(fixture.Mounts.GetSnapshots());
            Assert.Equal(MountLifecycle.Failed, snapshot.Lifecycle);
            Assert.True(snapshot.UploadStatusStale);
            Assert.NotEqual("Mount queued", snapshot.StatusText);
            Assert.Equal(failure is "failure" or "exception", ran);
            var row = new MountRow(MountDefinitionMapper.ToSettings(fixture.Definition), HostProtocol.ToStatus(snapshot));
            Assert.True(row.CanAct);
            Assert.Equal("Retry", row.ActionText);
        }
        finally { release.TrySetResult(); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "resodrive-queue-tests", Guid.NewGuid().ToString("N")));
        private readonly Worker _worker;
        private readonly RemoteWipeClient _wipe = new();
        internal RcloneMountCoordinator Mounts { get; }
        internal MountDefinition Definition { get; } = new()
        {
            Id = MountId.New(), DisplayName = "Disposable drive", RemoteName = "fixture", Target = new MountTarget.Drive('R'),
        };
        internal EmptyInventory Inventory { get; } = new();
        internal Fixture(bool connectionWait = false, Func<CancellationToken, Task<bool>>? probe = null)
        {
            Mounts = new(_paths.RcloneExecutable, _paths.ConfigFile, _paths, Inventory)
            {
                ConnectionProbe = (_, token) => probe?.Invoke(token) ?? Task.FromResult(false),
                DirectConnectionProbeAllowed = _ => true,
            };
            _worker = new(_paths, NullLogger<Worker>.Instance, new Lifetime(), _wipe)
            { MountQueueTimeout = connectionWait ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(300) };
            typeof(Worker).GetField("_mounts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_worker, Mounts);
            typeof(Worker).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_worker,
                new ManagerSettings { Mounts = [MountDefinitionMapper.ToSettings(Definition)] });
        }
        internal async Task WaitForConnectionAsync()
        {
            await File.WriteAllTextAsync(_paths.RcloneExecutable, "Disposable engine placeholder; it must not launch while offline.");
            await File.WriteAllTextAsync(_paths.ConfigFile, "[fixture]\ntype = webdav\nurl = https://files.example/\n");
            Assert.True((await Mounts.ReconcileAsync([Definition])).Succeeded);
            Assert.True((await Mounts.StartAsync(Definition.Id)).Succeeded);
            Assert.Equal(MountLifecycle.WaitingForConnection, Assert.Single(Mounts.GetSnapshots()).Lifecycle);
        }
        internal bool HasMountOperation => Operations.ContainsKey($"mount:{Definition.Id.Value:N}");
        internal void MakeRetryDue() => Mounts.NotifyNetworkChanged();
        internal void SetStartingSnapshot()
        {
            var snapshots = (ConcurrentDictionary<MountId, MountSnapshot>)typeof(RcloneMountCoordinator)
                .GetField("_snapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mounts)!;
            snapshots[Definition.Id] = snapshots[Definition.Id] with { Lifecycle = MountLifecycle.Starting };
        }
        internal void QueueConnectionRetries() => typeof(Worker)
            .GetMethod("QueueConnectionRetries", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_worker, [CancellationToken.None]);
        internal Task<HostResponse> StopDriveAsync() => CommandAsync(new HostRequest("stop", Definition.Id.Value,
            ExpectedHostBaseDirectory: AppContext.BaseDirectory));
        internal Task<HostResponse> CommandAsync(HostRequest request) => (Task<HostResponse>)typeof(Worker)
            .GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_worker, [request, CancellationToken.None])!;
        private ConcurrentDictionary<string, CancellationTokenSource> Operations =>
            (ConcurrentDictionary<string, CancellationTokenSource>)typeof(Worker)
                .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worker)!;
        internal bool Queue(string key, Func<CancellationToken, Task<OperationResult>> action, CancellationToken token = default) =>
            (bool)typeof(Worker).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_worker, [key, action, token, null])!;
        public async ValueTask DisposeAsync()
        {
            var operations = (ConcurrentDictionary<string, CancellationTokenSource>)typeof(Worker)
                .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worker)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!operations.IsEmpty) await Task.Delay(10, deadline.Token);
            await Mounts.DisposeAsync();
            _worker.Dispose();
            _wipe.Dispose();
            Directory.Delete(_paths.Root, true);
        }
    }
    private sealed class EmptyInventory : IMountTargetInventory
    {
        internal int StartChecks { get; private set; }
        public Task<OperationResult<IReadOnlySet<char>>> GetOccupiedDriveLettersAsync(CancellationToken cancellationToken = default)
        {
            StartChecks++;
            return Task.FromResult(Result.Success<IReadOnlySet<char>>(new HashSet<char>()));
        }
        public Task<OperationResult<bool>> IsMountedAsync(MountTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(false));
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Collections.Concurrent;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows.Tests;

public sealed class MountConnectionWaitingTests
{
    [Fact]
    public async Task OfflineProbesDoNotHoldTheCrossDriveLaunchGate()
    {
        await using var fixture = new Fixture();
        var second = fixture.Definition with { Id = MountId.New(), DisplayName = "Second private drive", Target = new MountTarget.Drive('S') };
        Assert.True((await fixture.Coordinator.ReconcileAsync([fixture.Definition, second])).Succeeded);
        var entered = Signal();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        fixture.ProbeOverride = async token =>
        {
            if (Interlocked.Increment(ref probes) == 2) entered.TrySetResult();
            return await release.Task.WaitAsync(token);
        };
        var firstStart = fixture.Coordinator.StartAsync(fixture.Definition.Id);
        var secondStart = fixture.Coordinator.StartAsync(second.Id);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(false); }
        Assert.All(await Task.WhenAll(firstStart, secondStart), result => Assert.True(result.Succeeded));
        Assert.All(fixture.Coordinator.GetSnapshots(), status => Assert.Equal(MountLifecycle.WaitingForConnection, status.Lifecycle));
    }

    [Fact]
    public async Task RefusedStopPreservesReconnectBudgetAndNetworkWaitIntent()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.SeedRecoveryAsync(dirty: true);
        var attempts = (ConcurrentDictionary<MountId, int>)typeof(RcloneMountCoordinator)
            .GetField("_restartAttempts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Coordinator)!;
        attempts[fixture.Definition.Id] = 1;
        var intentType = typeof(RcloneMountCoordinator).GetNestedType("StartIntent", BindingFlags.NonPublic)!;
        var reconnect = (Task<OperationResult>)typeof(RcloneMountCoordinator)
            .GetMethod("StartInternalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [fixture.Definition.Id, Enum.Parse(intentType, "Reconnect"), CancellationToken.None])!;
        Assert.True((await reconnect).Succeeded);
        Assert.False((await fixture.Coordinator.StopAsync(fixture.Definition.Id)).Succeeded);
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.True((await fixture.Coordinator.RetryConnectionAsync(Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates()))).Succeeded);
        Assert.Equal(2, fixture.Probes);
        Assert.Equal(1, attempts[fixture.Definition.Id]);
        Assert.Equal(MountLifecycle.WaitingForConnection, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
    }

    [Fact]
    public async Task CancelledManualRetryPreservesAnExistingDesiredConnection()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.StartAsync(fixture.Definition.Id, cancellation.Token));
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        Assert.Equal(MountLifecycle.WaitingForConnection, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
    }

    [Fact]
    public async Task UnreachableDriveWaitsWithoutCreatingRecoveryOrConsumingRestartBudget()
    {
        await using var fixture = new Fixture();
        Assert.True((await fixture.StartAsync()).Succeeded);
        var status = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.WaitingForConnection, status.Lifecycle);
        Assert.False(status.UploadStatusStale);
        Assert.False(status.UploadRecoveryRequired);
        Assert.Equal(0, status.UploadsQueued);
        Assert.Equal(0, status.UploadErrors);
        Assert.True((await fixture.Coordinator.CheckPendingUploadsAsync()).Succeeded);
        Assert.False(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var retry = Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        Assert.True((await fixture.Coordinator.RetryConnectionAsync(retry)).Succeeded);
        Assert.Equal(2, fixture.Probes);
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Fact]
    public async Task ExplicitDisconnectRetiresPreviouslyQueuedCandidatesAndSurvivesReload()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Coordinator.NotifyNetworkChanged();
        var queued = Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        Assert.True((await fixture.Coordinator.StopAsync(fixture.Definition.Id)).Succeeded);
        fixture.Reachable = true;
        Assert.True((await fixture.Coordinator.RetryConnectionAsync(queued)).Succeeded);
        await fixture.Coordinator.ReconcileAsync([fixture.Definition]);
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
        Assert.Equal(1, fixture.Probes);
        Assert.Equal(MountLifecycle.Stopped, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
    }

    [Fact]
    public async Task DisconnectDuringProbeCannotBeUndoneByLateReachability()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Coordinator.NotifyNetworkChanged();
        var candidate = Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        var entered = Signal();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProbeOverride = async token => { entered.TrySetResult(); return await release.Task.WaitAsync(token); };
        var retry = fixture.Coordinator.RetryConnectionAsync(candidate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Coordinator.StopAsync(fixture.Definition.Id);
        release.TrySetResult(true);
        Assert.True((await retry.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Equal(MountLifecycle.Stopped, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
        using var ownership = new MountOwnershipStore(fixture.Paths);
        Assert.Empty(await ownership.LoadAsync(CancellationToken.None));
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Fact]
    public async Task CancelledRetryRetainsBoundedRetryIntentAndReleasesOperationGate()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Coordinator.NotifyNetworkChanged();
        var candidate = Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        var entered = Signal();
        fixture.ProbeOverride = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return false;
        };
        using var cancellation = new CancellationTokenSource();
        var retry = fixture.Coordinator.RetryConnectionAsync(candidate, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.WaitAsync(TimeSpan.FromSeconds(5)));
        fixture.ProbeOverride = null;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        Assert.True((await fixture.Coordinator.StopAsync(fixture.Definition.Id)).Succeeded);
    }

    [Fact]
    public async Task DeclinedStopPreservesDirtyCacheAndTheDesiredConnection()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.SeedRecoveryAsync(dirty: true);
        Assert.True((await fixture.Coordinator.StartAsync(fixture.Definition.Id)).Succeeded);
        var waiting = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.WaitingForConnection, waiting.Lifecycle);
        Assert.True(waiting.UploadStatusStale);
        Assert.False(waiting.UploadRecoveryRequired);
        Assert.False((await fixture.Coordinator.CheckPendingUploadsAsync()).Succeeded);
        var stop = await fixture.Coordinator.StopAsync(fixture.Definition.Id);
        Assert.Equal("mount.upload_recovery_pending", stop.Error?.Code);
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
        Assert.True(File.Exists(Path.Combine(fixture.Metadata, "file")));
        Assert.True((await fixture.Coordinator.StopAsync(fixture.Definition.Id, allowPendingUploads: true)).Succeeded);
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Fact]
    public async Task RetiringACleanJournalPreservesTheVisibleConnectionWait()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.SeedRecoveryAsync(dirty: false);
        await fixture.Coordinator.StartAsync(fixture.Definition.Id);
        Assert.True((await fixture.Coordinator.CheckPendingUploadsAsync()).Succeeded);
        var snapshot = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.WaitingForConnection, snapshot.Lifecycle);
        Assert.False(snapshot.UploadStatusStale);
        Assert.Equal(0, snapshot.UploadErrors);
        fixture.Coordinator.NotifyNetworkChanged();
        Assert.Single(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Fact]
    public async Task OfflineServerCannotHideAnEarlierCacheIdentityProblem()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.SeedRecoveryAsync(dirty: true, differentIdentity: true);
        var start = await fixture.Coordinator.StartAsync(fixture.Definition.Id);
        Assert.Equal("mount.cache_identity_changed", start.Error?.Code);
        Assert.Equal(0, fixture.Probes);
        Assert.True(Assert.Single(fixture.Coordinator.GetSnapshots()).UploadRecoveryRequired);
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Theory]
    [InlineData("type = local")]
    [InlineData("type = sftp\nhost = server.invalid\nport = 2222\nsocks_proxy = 127.0.0.1:1080")]
    public async Task UnknownOrProxiedTransportFallsBackToTheEngine(string configuration)
    {
        await using var fixture = new Fixture(configuration);
        var start = await fixture.StartAsync();
        Assert.Equal("mount.start_failed", start.Error?.Code);
        Assert.Equal(0, fixture.Probes);
        Assert.Equal(MountLifecycle.Failed, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Fact]
    public async Task ReachableServerUsesNormalEngineDiagnosticsRatherThanWaiting()
    {
        await using var fixture = new Fixture();
        fixture.Reachable = true;
        var start = await fixture.StartAsync();
        Assert.Equal("mount.start_failed", start.Error?.Code);
        Assert.Equal(1, fixture.Probes);
        Assert.Empty(fixture.Coordinator.GetConnectionRetryCandidates());
    }

    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("ALL_PROXY")]
    [InlineData("RCLONE_SFTP_SOCKS_PROXY")]
    [InlineData("RCLONE_BIND")]
    public void CustomEnvironmentTransportIsNeverBlockedByADirectProbe(string environmentName) =>
        Assert.False(MountConnectionReachability.CanProbeDirectly([], [environmentName]));

    [Fact]
    public async Task RealLoopbackProbeWaitsForClosedServiceAndHonorsCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new MountConnectionEndpoint("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.True(await MountConnectionReachability.IsReachableAsync(endpoint, CancellationToken.None));
        listener.Stop();
        Assert.False(await MountConnectionReachability.IsReachableAsync(endpoint, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MountConnectionReachability.IsReachableAsync(endpoint, cancellation.Token));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ApplicationPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "resodrive-network-wait-tests", Guid.NewGuid().ToString("N")));
        internal Clock Clock { get; } = new();
        internal RcloneMountCoordinator Coordinator { get; }
        internal MountDefinition Definition { get; } = new()
        {
            Id = MountId.New(), DisplayName = "Disposable private drive", RemoteName = "fixture", Target = new MountTarget.Drive('R'),
        };
        internal string Metadata => Path.Combine(Paths.Cache, "vfsMeta", "fixture");
        internal bool Reachable { get; set; }
        internal int Probes { get; private set; }
        internal Func<CancellationToken, Task<bool>>? ProbeOverride { get; set; }
        internal Fixture(string config = "type = webdav\nurl = https://server.invalid:8443/dav")
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.RcloneExecutable, "Disposable fixture; not an executable");
            File.WriteAllText(Paths.ConfigFile, "[fixture]\n" + config + "\n");
            Coordinator = new(Paths.RcloneExecutable, Paths.ConfigFile, Paths, new Inventory())
            {
                ConnectionClock = Clock, DirectConnectionProbeAllowed = _ => true,
                ConnectionProbe = (_, token) => { Probes++; return ProbeOverride?.Invoke(token) ?? Task.FromResult(Reachable); },
            };
        }
        internal async Task InitializeAsync() => Assert.True((await Coordinator.ReconcileAsync([Definition])).Succeeded);
        internal async Task<OperationResult> StartAsync() { await InitializeAsync(); return await Coordinator.StartAsync(Definition.Id); }
        internal async Task SeedRecoveryAsync(bool dirty, bool differentIdentity = false)
        {
            Directory.CreateDirectory(Metadata);
            await File.WriteAllTextAsync(Path.Combine(Metadata, "file"), dirty ? "{\"Dirty\":true,\"Size\":20}" : "{\"Dirty\":false,\"Size\":20}");
            var recovery = (MountRecoveryStore)typeof(RcloneMountCoordinator).GetField("_recovery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Coordinator)!;
            await recovery.RecordAsync(differentIdentity ? Definition with { RemoteName = "earlier" } : Definition, Metadata, CancellationToken.None);
        }
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); Directory.Delete(Paths.Root, true); }
    }

    private sealed class Inventory : IMountTargetInventory
    {
        public Task<OperationResult<IReadOnlySet<char>>> GetOccupiedDriveLettersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlySet<char>>(new HashSet<char>()));
        public Task<OperationResult<bool>> IsMountedAsync(MountTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(false));
    }
}

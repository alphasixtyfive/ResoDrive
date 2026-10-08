using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows.Tests;

public sealed class RcloneMountCoordinatorLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitObserverUsesCurrentPolicyAndKeepsRecoveryPausedAcrossReconciliation(bool lowerAttemptLimit)
    {
        await using var fixture = new Fixture();
        var original = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([original]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(original, null, CancellationToken.None);
        if (lowerAttemptLimit)
            Field<ConcurrentDictionary<MountId, int>>(fixture.Coordinator, "_restartAttempts")[original.Id] = 1;
        using var process = await ExitedProcessAsync();
        var session = fixture.AddSession(process, original);
        var policy = lowerAttemptLimit ? original.Restart with { MaximumAttempts = 1 } : original.Restart with { Enabled = false };
        var replacement = original with { Restart = policy };
        await fixture.Coordinator.ReconcileAsync([replacement]);

        await fixture.ObserveAsync(session).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MountLifecycle.Failed, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
        await fixture.Coordinator.ReconcileAsync([replacement]);
        await fixture.Coordinator.ReconcileAsync([replacement with { AutoMount = AutoMountPolicy.OnApplicationStart }]);
        var snapshot = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.Failed, snapshot.Lifecycle);
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        Assert.Equal(0, fixture.Inventory.StartChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRestartHonorsPolicyChangesWithoutDiscardingRecovery(bool lowerAttemptLimit)
    {
        await using var fixture = new Fixture();
        var original = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([original]);
        var recovery = Field<MountRecoveryStore>(fixture.Coordinator, "_recovery");
        await recovery.RecordAsync(original, null, CancellationToken.None);
        if (lowerAttemptLimit)
            Field<ConcurrentDictionary<MountId, int>>(fixture.Coordinator, "_restartAttempts")[original.Id] = 1;
        using var process = await ExitedProcessAsync();
        var observer = fixture.ObserveAsync(fixture.AddSession(process, original));
        await WaitUntilAsync(() => fixture.Coordinator.GetSnapshots().Single().Lifecycle == MountLifecycle.WaitingToRestart);
        var policy = lowerAttemptLimit ? original.Restart with { MaximumAttempts = 1 } : original.Restart with { Enabled = false };
        var replacement = original with { Restart = policy };
        await fixture.Coordinator.ReconcileAsync([replacement]);

        await observer.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.Stopped, snapshot.Lifecycle);
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        Assert.Equal(0, fixture.Inventory.StartChecks);
        await fixture.Coordinator.ReconcileAsync([replacement]);
        snapshot = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.Stopped, snapshot.Lifecycle);
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        Assert.Equal(0, fixture.Inventory.StartChecks);

        // A failed manual preflight preserves the pause and exhausted retry budget.
        // The disposable fixture intentionally has no rclone binary.
        var manual = await fixture.Coordinator.StartAsync(original.Id);
        Assert.Equal("rclone.not_found", manual.Error?.Code);
        var attempts = Field<ConcurrentDictionary<MountId, int>>(fixture.Coordinator, "_restartAttempts");
        Assert.True(attempts.ContainsKey(original.Id));
        Assert.True(RecoveryIsPaused(fixture.Coordinator, original.Id));
        await fixture.Coordinator.ReconcileAsync([replacement]);
        Assert.Equal(MountLifecycle.Failed, Assert.Single(fixture.Coordinator.GetSnapshots()).Lifecycle);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledManualStartPreservesExplicitRecoveryPause(bool cancelWhileWaiting)
    {
        await using var fixture = new Fixture();
        var definition = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([definition]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(definition, null, CancellationToken.None);
        Assert.True((await fixture.Coordinator.StopAsync(definition.Id, allowPendingUploads: true)).Succeeded);
        var before = Assert.Single(fixture.Coordinator.GetSnapshots());
        var gate = OperationGate(fixture);
        using var cancellation = new CancellationTokenSource();
        if (cancelWhileWaiting) await gate.WaitAsync();
        else cancellation.Cancel();
        try
        {
            var start = fixture.Coordinator.StartAsync(definition.Id, cancellation.Token);
            if (cancelWhileWaiting)
            {
                Assert.False(start.IsCompleted);
                cancellation.Cancel();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { if (cancelWhileWaiting) gate.Release(); }

        await fixture.Coordinator.ReconcileAsync([definition]);

        Assert.Equal(before, Assert.Single(fixture.Coordinator.GetSnapshots()));
        Assert.True(RecoveryIsPaused(fixture.Coordinator, definition.Id));
        Assert.Equal(0, fixture.Inventory.StartChecks);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        Assert.NotNull(Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").Find(definition.Id));
    }

    [Fact]
    public async Task FailedManualPreflightAndAutomaticRecoveryPreserveExplicitPause()
    {
        await using var fixture = new Fixture();
        var definition = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([definition]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(definition, null, CancellationToken.None);
        Assert.True((await fixture.Coordinator.StopAsync(definition.Id, allowPendingUploads: true)).Succeeded);

        var start = await fixture.Coordinator.StartAsync(definition.Id);

        Assert.Equal("rclone.not_found", start.Error?.Code);
        Assert.True(RecoveryIsPaused(fixture.Coordinator, definition.Id));
        var before = Assert.Single(fixture.Coordinator.GetSnapshots());
        var intentType = typeof(RcloneMountCoordinator).GetNestedType("StartIntent", BindingFlags.NonPublic)!;
        var automatic = (Task<OperationResult>)typeof(RcloneMountCoordinator)
            .GetMethod("StartInternalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [definition.Id, Enum.Parse(intentType, "CacheRecovery"), CancellationToken.None])!;
        Assert.True((await automatic).Succeeded);
        await fixture.Coordinator.ReconcileAsync([definition]);
        Assert.Equal(before, Assert.Single(fixture.Coordinator.GetSnapshots()));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        Assert.Equal(0, fixture.Inventory.StartChecks);
    }

    [Fact]
    public async Task CancellationDuringManualPreflightPreservesRecoveryPause()
    {
        await using var fixture = new Fixture();
        var definition = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([definition]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(definition, null, CancellationToken.None);
        Assert.True((await fixture.Coordinator.StopAsync(definition.Id, allowPendingUploads: true)).Succeeded);
        var before = Assert.Single(fixture.Coordinator.GetSnapshots());
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.RcloneExecutable)!);
        await File.WriteAllTextAsync(fixture.Paths.RcloneExecutable, "Disposable preflight fixture; never executed");
        await File.WriteAllTextAsync(fixture.Paths.ConfigFile, "[fixture]\ntype = local\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Inventory.BeforeStartCheck = token =>
        {
            entered.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var cancellation = new CancellationTokenSource();
        var start = fixture.Coordinator.StartAsync(definition.Id, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));

        await fixture.Coordinator.ReconcileAsync([definition]);

        Assert.Equal(before, Assert.Single(fixture.Coordinator.GetSnapshots()));
        Assert.True(RecoveryIsPaused(fixture.Coordinator, definition.Id));
        Assert.Equal(1, fixture.Inventory.StartChecks);
        Assert.NotNull(Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").Find(definition.Id));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
    }

    [Fact]
    public async Task AcceptedManualStartOfLiveSessionClearsPauseAndRetryBudget()
    {
        await using var fixture = new Fixture();
        var definition = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([definition]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(definition, null, CancellationToken.None);
        Assert.True((await fixture.Coordinator.StopAsync(definition.Id, allowPendingUploads: true)).Succeeded);
        var attempts = Field<ConcurrentDictionary<MountId, int>>(fixture.Coordinator, "_restartAttempts");
        attempts[definition.Id] = 3;
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            { UseShellExecute = false, CreateNoWindow = true })!;
        fixture.AddSession(process, definition);
        try
        {
            Assert.True((await fixture.Coordinator.StartAsync(definition.Id)).Succeeded);
            Assert.False(RecoveryIsPaused(fixture.Coordinator, definition.Id));
            Assert.False(attempts.ContainsKey(definition.Id));
            Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
        }
        finally
        {
            Assert.True((await fixture.Coordinator.StopAsync(definition.Id, allowPendingUploads: true)).Succeeded);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeliberatePolicyChangeCanResumeOnlyAPolicyPausedRecovery(bool lowerAttemptLimit)
    {
        await using var fixture = new Fixture();
        var original = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([original]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(original, null, CancellationToken.None);
        if (lowerAttemptLimit)
            Field<ConcurrentDictionary<MountId, int>>(fixture.Coordinator, "_restartAttempts")[original.Id] = 1;
        using var process = await ExitedProcessAsync();
        var session = fixture.AddSession(process, original);
        var pausedPolicy = lowerAttemptLimit ? original.Restart with { MaximumAttempts = 1 } : original.Restart with { Enabled = false };
        await fixture.Coordinator.ReconcileAsync([original with { Restart = pausedPolicy }]);
        await fixture.ObserveAsync(session).WaitAsync(TimeSpan.FromSeconds(5));

        var resumedPolicy = pausedPolicy with { Enabled = true, MaximumAttempts = 3 };
        await fixture.Coordinator.ReconcileAsync([original with { Restart = resumedPolicy }]);

        var snapshot = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.Failed, snapshot.Lifecycle);
        Assert.Contains("rclone.exe could not be found", snapshot.StatusText, StringComparison.Ordinal);
        Assert.False(Field<ConcurrentDictionary<MountId, int>>(fixture.Coordinator, "_restartAttempts").ContainsKey(original.Id));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
    }

    [Fact]
    public async Task ADelayedRetryAndPolicyEditDoNotUndoAnExplicitDisconnectWithPendingRecovery()
    {
        await using var fixture = new Fixture();
        var original = fixture.Definition;
        await fixture.Coordinator.ReconcileAsync([original]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(original, null, CancellationToken.None);
        using var process = await ExitedProcessAsync();
        var observer = fixture.ObserveAsync(fixture.AddSession(process, original));
        await WaitUntilAsync(() => fixture.Coordinator.GetSnapshots().Single().Lifecycle == MountLifecycle.WaitingToRestart);
        Assert.True((await fixture.Coordinator.StopAsync(original.Id, allowPendingUploads: true)).Succeeded);
        await observer.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Coordinator.ReconcileAsync([original with { Restart = original.Restart with { MaximumAttempts = 3 } }]);

        var snapshot = Assert.Single(fixture.Coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.Stopped, snapshot.Lifecycle);
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.Equal(0, fixture.Inventory.StartChecks);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
    }

    [Fact]
    public async Task PolicyPauseDoesNotPreventCrashRecoveryInANewCoordinatorLifetime()
    {
        await using var fixture = new Fixture();
        var original = fixture.Definition;
        var replacement = original with { Restart = original.Restart with { Enabled = false } };
        await fixture.Coordinator.ReconcileAsync([original]);
        await Field<MountRecoveryStore>(fixture.Coordinator, "_recovery").RecordAsync(original, null, CancellationToken.None);
        using var process = await ExitedProcessAsync();
        var session = fixture.AddSession(process, original);
        await fixture.Coordinator.ReconcileAsync([replacement]);
        await fixture.ObserveAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.DisposeCoordinatorAsync();

        await using var recovered = new RcloneMountCoordinator(fixture.Paths.RcloneExecutable, fixture.Paths.ConfigFile,
            fixture.Paths, new CountingInventory());
        await recovered.ReconcileAsync([replacement]);

        var snapshot = Assert.Single(recovered.GetSnapshots());
        Assert.Equal(MountLifecycle.Failed, snapshot.Lifecycle);
        Assert.Contains("rclone.exe could not be found", snapshot.StatusText, StringComparison.Ordinal);
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.Root, "mount-upload-recovery.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalWaitsForEveryObserverToReleaseTheActualOperationGate(bool failedObserver)
    {
        await using var fixture = new Fixture();
        var gate = OperationGate(fixture);
        var lifetime = Field<CancellationTokenSource>(fixture.Coordinator, "_lifetime");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ObserveAsync()
        {
            await gate.WaitAsync();
            entered.SetResult();
            try
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); }
                catch (OperationCanceledException) { cancellationObserved.SetResult(); }
                await release.Task;
            }
            finally { gate.Release(); }
        }
        var observer = ObserveAsync();
        fixture.Coordinator.TrackExitObserver(observer);
        if (failedObserver)
            fixture.Coordinator.TrackExitObserver(Task.FromException(new IOException("Disposable observer failure")));
        await entered.Task;
        var disposal = fixture.DisposeCoordinatorAsync();
        try
        {
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await observer.WaitAsync(TimeSpan.FromSeconds(5));
        if (failedObserver)
            await Assert.ThrowsAsync<IOException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        else await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static T Field<T>(RcloneMountCoordinator coordinator, string name) =>
        (T)typeof(RcloneMountCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;

    private static SemaphoreSlim OperationGate(Fixture fixture) =>
        (SemaphoreSlim)typeof(RcloneMountCoordinator).GetMethod("OperationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [fixture.Definition.Id])!;

    private static bool RecoveryIsPaused(RcloneMountCoordinator coordinator, MountId id)
    {
        var pauses = Field<object>(coordinator, "_pausedRecovery");
        return (bool)pauses.GetType().GetMethod("ContainsKey")!.Invoke(pauses, [id])!;
    }

    private static async Task<Process> ExitedProcessAsync()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 7") { UseShellExecute = false, CreateNoWindow = true })!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return process;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private bool _disposed;
        public ApplicationPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "resodrive-observer-tests", Guid.NewGuid().ToString("N")));
        public CountingInventory Inventory { get; } = new();
        public MountDefinition Definition { get; } = new()
        {
            Id = MountId.New(), DisplayName = "Disposable drive", RemoteName = "fixture", Target = new MountTarget.Drive('R'),
            Restart = new RestartPolicy { InitialDelay = TimeSpan.FromSeconds(1), MaximumDelay = TimeSpan.FromSeconds(1) }
        };
        public RcloneMountCoordinator Coordinator { get; }

        public Fixture() => Coordinator = new(Paths.RcloneExecutable, Paths.ConfigFile, Paths, Inventory);

        public object AddSession(Process process, MountDefinition definition)
        {
            var type = typeof(RcloneMountCoordinator).GetNestedType("Session", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [process, definition, null], null)!;
            var sessions = typeof(RcloneMountCoordinator).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Coordinator)!;
            Assert.True((bool)sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [definition.Id, session])!);
            return session;
        }

        public Task ObserveAsync(object session)
        {
            var task = (Task)typeof(RcloneMountCoordinator).GetMethod("ObserveExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Coordinator, [session])!;
            Coordinator.TrackExitObserver(task);
            return task;
        }

        public async Task DisposeCoordinatorAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Coordinator.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { await DisposeCoordinatorAsync(); }
            finally { if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, recursive: true); }
        }
    }

    private sealed class CountingInventory : IMountTargetInventory
    {
        public int StartChecks { get; private set; }
        public Func<CancellationToken, Task>? BeforeStartCheck { get; set; }
        public async Task<OperationResult<IReadOnlySet<char>>> GetOccupiedDriveLettersAsync(CancellationToken cancellationToken = default)
        {
            StartChecks++;
            if (BeforeStartCheck is not null) await BeforeStartCheck(cancellationToken);
            return Result.Success<IReadOnlySet<char>>(new HashSet<char>());
        }
        public Task<OperationResult<bool>> IsMountedAsync(MountTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(false));
    }
}

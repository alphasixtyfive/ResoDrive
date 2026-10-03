using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Host;
using ResoDrive.Windows;
using ResoDrive.Windows.Hosting;

namespace ResoDrive.App.Tests;

public sealed class WorkerSessionProtectionTests
{
    [Theory]
    [InlineData(SyncMode.CopyToRemote)]
    [InlineData(SyncMode.CopyFromRemote)]
    [InlineData(SyncMode.SyncToRemote)]
    [InlineData(SyncMode.SyncFromRemote)]
    public async Task QueuedAndRunningSyncProtectSessionUntilCompletion(SyncMode mode)
    {
        await using var fixture = new SessionFixture(mode);
        AssertSafe(await fixture.RefreshAsync());

        fixture.Syncs.MarkQueued(fixture.Mount.Id, fixture.Job.Id);
        Assert.Equal(SyncLifecycle.Queued, Assert.Single(fixture.Syncs.GetSnapshots()).Lifecycle);
        AssertProtected(await fixture.RefreshAsync(), fixture.Guard);
        Assert.Contains("still transferring files", ReadShutdownReason(fixture.Guard), StringComparison.Ordinal);

        var run = fixture.StartSync();
        await fixture.Runner.Started.Task.WaitAsync(fixture.Token);
        Assert.Equal(SyncLifecycle.Running, Assert.Single(fixture.Syncs.GetSnapshots()).Lifecycle);
        AssertProtected(await fixture.RefreshAsync(), fixture.Guard);
        Assert.Contains("transfers to finish", ReadShutdownReason(fixture.Guard), StringComparison.Ordinal);

        fixture.Runner.Complete();
        Assert.True((await run.WaitAsync(fixture.Token)).Succeeded);
        Assert.Equal(SyncLifecycle.Succeeded, Assert.Single(fixture.Syncs.GetSnapshots()).Lifecycle);
        AssertSafe(await fixture.RefreshAsync());
        Assert.False(fixture.Guard.BlocksSessionEnding);
        Assert.Null(ReadShutdownReason(fixture.Guard));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("stale")]
    [InlineData("checking")]
    [InlineData("unobserved")]
    [InlineData("expired")]
    public async Task CompletedSyncCannotReleaseProtectionForUnconfirmedUploads(string observation)
    {
        await using var fixture = new SessionFixture(SyncMode.CopyToRemote);
        var run = fixture.StartSync();
        await fixture.Runner.Started.Task.WaitAsync(fixture.Token);
        AssertProtected(await fixture.RefreshAsync(), fixture.Guard);

        fixture.Runner.Complete();
        Assert.True((await run.WaitAsync(fixture.Token)).Succeeded);
        var clean = fixture.CleanUploadObservation();
        fixture.SetUploadObservation(observation switch
        {
            "error" => clean with { UploadErrors = 1 },
            "stale" => clean with { UploadStatusStale = true },
            "checking" => clean with { UploadStatusChecking = true },
            "unobserved" => clean with { UploadObservedAt = null },
            "expired" => clean with { UploadObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            _ => throw new ArgumentOutOfRangeException(nameof(observation)),
        });

        AssertProtected(await fixture.RefreshAsync(), fixture.Guard);
        Assert.Contains(observation == "error" ? "upload errors" : "could not yet confirm",
            ReadShutdownReason(fixture.Guard), StringComparison.Ordinal);

        fixture.SetUploadObservation(fixture.CleanUploadObservation());
        AssertSafe(await fixture.RefreshAsync());
        Assert.False(fixture.Guard.BlocksSessionEnding);
        Assert.Null(ReadShutdownReason(fixture.Guard));
    }

    [Fact]
    public async Task UninitializedHostRetainsProtectionEvenWithoutVisibleWork()
    {
        await using var fixture = new SessionFixture(SyncMode.CopyToRemote);
        AssertSafe(await fixture.RefreshAsync());
        fixture.MarkSettingsUnavailable();

        AssertProtected(await fixture.RefreshAsync(), fixture.Guard);
        Assert.Contains("checking for files", ReadShutdownReason(fixture.Guard), StringComparison.Ordinal);
    }

    private static void AssertProtected(NativeSessionGuardStatus status, NativeSessionGuard guard)
    {
        Assert.True(guard.BlocksSessionEnding);
        Assert.True(status.WindowReady, status.Error);
        Assert.Null(status.Error);
        Assert.True(status.ShutdownReasonRegistered);
        Assert.True(status.AutomaticSleepPrevented);
    }

    private static void AssertSafe(NativeSessionGuardStatus status)
    {
        Assert.True(status.WindowReady, status.Error);
        Assert.Null(status.Error);
        Assert.False(status.ShutdownReasonRegistered);
        Assert.False(status.AutomaticSleepPrevented);
    }

    private static string? ReadShutdownReason(NativeSessionGuard guard)
    {
        Assert.NotEqual(0, guard.WindowHandle);
        uint length = 256;
        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            return ShutdownBlockReasonQuery(guard.WindowHandle, buffer, ref length)
                ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // The fixture never starts Worker, opens its host pipe, mounts a drive or
    // launches rclone. It exercises the real worker policy and isolated native
    // window against coordinator state, with asynchronous completion barriers.
    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "resodrive-session-protection-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly HttpClient _http = new(new NoNetworkHandler());
        private readonly Worker _worker;
        private readonly RcloneMountCoordinator _mounts;
        private Task<OperationResult>? _run;

        public SessionFixture(SyncMode mode)
        {
            var paths = new ApplicationPaths(Path.Combine(_root, "data"));
            paths.EnsureCreated();
            var local = Path.Combine(_root, "local");
            Directory.CreateDirectory(local);
            Job = new SyncJob
            {
                Id = SyncJobId.New(), DisplayName = "Disposable sync", LocalPath = local,
                RemotePath = "documents", Mode = mode,
            };
            Mount = new MountDefinition
            {
                Id = MountId.New(), DisplayName = "Disposable drive", RemoteName = "remote",
                Target = new MountTarget.Drive('R'), SyncJobs = [Job],
            };
            Syncs = new RcloneSyncCoordinator(paths.RcloneExecutable, paths.ConfigFile,
                paths, () => [Mount], Runner);
            _mounts = new RcloneMountCoordinator(paths.RcloneExecutable, paths.ConfigFile,
                paths, new EmptyInventory());
            _worker = new Worker(paths, NullLogger<Worker>.Instance, new TestLifetime(), new RemoteWipeClient(_http));
            Guard = new NativeSessionGuard();
            SetWorkerField("_settings", new ManagerSettings { Mounts = [MountDefinitionMapper.ToSettings(Mount)] });
            SetWorkerField("_syncs", Syncs);
            SetWorkerField("_mounts", _mounts);
            SetWorkerField("_sessionGuard", Guard);
        }

        public SyncJob Job { get; }
        public MountDefinition Mount { get; }
        public RcloneSyncCoordinator Syncs { get; }
        public GatedRunner Runner { get; } = new();
        public NativeSessionGuard Guard { get; }
        public CancellationToken Token => _deadline.Token;

        public Task<OperationResult> StartSync() => _run = Syncs.RunAsync(Mount.Id, Job.Id, Token);

        public async Task<NativeSessionGuardStatus> RefreshAsync()
        {
            typeof(Worker).GetMethod("UpdateSessionProtection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_worker, null);
            return await Guard.SynchronizeAsync(Token);
        }

        public MountSnapshot CleanUploadObservation() => new()
        {
            MountId = Mount.Id, Lifecycle = MountLifecycle.Mounted,
            UploadsQueued = 0, UploadsInProgress = 0, UploadsDirty = 0, UploadErrors = 0,
            UploadObservedAt = DateTimeOffset.UtcNow,
        };

        public void SetUploadObservation(MountSnapshot snapshot)
        {
            var snapshots = (ConcurrentDictionary<MountId, MountSnapshot>)typeof(RcloneMountCoordinator)
                .GetField("_snapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_mounts)!;
            snapshots[snapshot.MountId] = snapshot;
        }

        public void MarkSettingsUnavailable() => SetWorkerField("_settings", null);

        private void SetWorkerField(string name, object? value) =>
            typeof(Worker).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_worker, value);

        public async ValueTask DisposeAsync()
        {
            try
            {
                Runner.Complete();
                if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                try { await _mounts.DisposeAsync(); }
                finally
                {
                    Syncs.Dispose();
                    await Guard.DisposeAsync();
                    _worker.Dispose();
                    _http.Dispose();
                    _deadline.Dispose();
                    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                }
            }
        }
    }

    private sealed class GatedRunner : IRcloneProcessRunner
    {
        private readonly TaskCompletionSource<ProcessRunResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null)
        {
            Started.TrySetResult();
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _completion.TrySetResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
    }

    private sealed class EmptyInventory : IMountTargetInventory
    {
        public Task<OperationResult<IReadOnlySet<char>>> GetOccupiedDriveLettersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlySet<char>>(new HashSet<char>()));
        public Task<OperationResult<bool>> IsMountedAsync(MountTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(false));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Session protection tests must not access a server.");
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => throw new InvalidOperationException("Session protection tests must not start or stop a host.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "This test project does not enable unsafe source-generated interop.")]
    private static extern bool ShutdownBlockReasonQuery(nint window, nint buffer, ref uint length);
}

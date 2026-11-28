using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResoDrive.Core.Domain;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class WorkerOperationLoggingTests
{
    [Fact]
    public async Task FailedQueuedSyncHasOneOperationFailureLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "resodrive-operation-log-tests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(Path.Combine(root, "data"));
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var job = new SyncJob { Id = SyncJobId.New(), DisplayName = "Disposable sync", LocalPath = local };
        var mount = new MountDefinition
        {
            Id = MountId.New(), DisplayName = "Disposable drive", RemoteName = "fixture", Target = new MountTarget.Drive('R'), SyncJobs = [job]
        };
        var logger = new CountingLogger();
        using var syncs = new RcloneSyncCoordinator(paths.RcloneExecutable, paths.ConfigFile, paths, () => [mount], new FailedRunner());
        using var worker = new Worker(paths, logger, new TestLifetime(), new RemoteWipeClient());
        try
        {
            typeof(Worker).GetField("_syncs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, syncs);
            var queue = typeof(Worker).GetMethod("QueueSync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True((bool)queue.Invoke(worker, [mount.Id, job.Id, CancellationToken.None])!);
            var operations = (ConcurrentDictionary<string, CancellationTokenSource>)typeof(Worker)
                .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!operations.IsEmpty) await Task.Delay(10, deadline.Token);

            Assert.Equal(SyncLifecycle.Failed, Assert.Single(syncs.GetSnapshots()).Lifecycle);
            Assert.Equal(1, logger.OperationFailures);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class FailedRunner : IRcloneProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null) =>
            Task.FromResult(new ProcessRunResult(1, string.Empty, "Disposable transfer failure", false));
    }

    private sealed class CountingLogger : ILogger<Worker>
    {
        private int _operationFailures;
        public int OperationFailures => Volatile.Read(ref _operationFailures);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1003) Interlocked.Increment(ref _operationFailures);
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

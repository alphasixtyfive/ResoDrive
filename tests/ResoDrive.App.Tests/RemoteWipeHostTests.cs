using System.IO;
using System.Net;
using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class RemoteWipeHostTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "resodrive-wipe-host", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task ActualWorkerDetectsPersistsStopsAndResumesWipeBeforeLoadingSettings()
    {
        _paths.EnsureCreated();
        using (var settings = new AtomicSettingsStore(_paths))
            Assert.True((await settings.SaveAsync(new(), 0)).Succeeded);
        var registration = new RemoteWipeRegistration(Guid.NewGuid(), "https://cloud.example/dav", "https://cloud.example/", "test", "token");
        var staged = await new RemoteWipeStore(_paths).CreateStagedAsync([registration]);
        File.Move(staged, _paths.RemoteWipeFile);
        File.WriteAllText(Path.Combine(_paths.Cache, "cache-fixture"), "cached content");
        using var http = new HttpClient(new WipeServer());

        // First lifetime polls the protocol and stops; it must not delete data under running work.
        using (var lifetime = new TestLifetime())
        using (var worker = new Worker(_paths, NullLogger<Worker>.Instance, lifetime, new RemoteWipeClient(http)))
        {
            await worker.StartAsync(CancellationToken.None);
            await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await worker.StopAsync(CancellationToken.None);
            Assert.True(worker.CanResumeRemoteWipe);
        }
        Assert.Equal(RemoteWipePhase.Requested, new RemoteWipeStateStore(_paths).Read()!.Phase);
        Assert.True(File.Exists(_paths.SettingsFile));

        // Corrupt settings prove the recovery lifetime cannot take the ordinary settings/load/mount path.
        File.WriteAllText(_paths.SettingsFile, "unreadable old settings");
        using (var lifetime = new TestLifetime())
        using (var worker = new Worker(_paths, NullLogger<Worker>.Instance, lifetime, new RemoteWipeClient(http)))
        {
            await worker.StartAsync(CancellationToken.None);
            await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.Equal(RemoteWipePhase.Completed, new RemoteWipeStateStore(_paths).Read()!.Phase);
        Assert.False(File.Exists(_paths.SettingsFile));
        Assert.False(File.Exists(_paths.SettingsFile + ".bak"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_paths.Cache));
    }

    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }

    [Fact]
    public async Task PendingAcknowledgementBlocksMountingButAllowsConfirmedInstallerShutdown()
    {
        _paths.EnsureCreated();
        var registration = new RemoteWipeRegistration(Guid.NewGuid(), "https://cloud.example/dav", "https://cloud.example/", "test", "token");
        await new RemoteWipeStateStore(_paths).SaveAsync(new(Guid.NewGuid(), RemoteWipePhase.Cleaned, registration));
        using var http = new HttpClient(new OfflineServer());
        using var lifetime = new TestLifetime();
        using var worker = new Worker(_paths, NullLogger<Worker>.Instance, lifetime, new RemoteWipeClient(http));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("host.remote_wipe", (await SendAsync(new("mount", Confirmed: true))).ErrorCode);
            Assert.False((await SendAsync(new("shutdown"))).Succeeded);
            Assert.True((await SendAsync(new("shutdown", Confirmed: true))).Succeeded);
            await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(worker.RestartForRemoteWipe);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal(RemoteWipePhase.Cleaned, new RemoteWipeStateStore(_paths).Read()!.Phase);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task RecoveryRecordsAndLogsWhetherTheServerAcknowledged(HttpStatusCode status, bool acknowledged)
    {
        _paths.EnsureCreated();
        var registration = new RemoteWipeRegistration(Guid.NewGuid(), "https://cloud.example/dav", "https://cloud.example/", "test", "token");
        await new RemoteWipeStateStore(_paths).SaveAsync(new(Guid.NewGuid(), RemoteWipePhase.Cleaned, registration));
        using var http = new HttpClient(new AcknowledgementServer(status));
        using var lifetime = new TestLifetime();
        var logger = new RecoveryLogger();
        using var worker = new Worker(_paths, logger, lifetime, new RemoteWipeClient(http));

        await worker.StartAsync(CancellationToken.None);
        try { await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await worker.StopAsync(CancellationToken.None); }

        var state = new RemoteWipeStateStore(_paths).Read()!;
        Assert.Equal(RemoteWipePhase.Completed, state.Phase);
        Assert.Equal(acknowledged, state.ServerAcknowledged);
        Assert.Null(state.Registration);
        Assert.Contains($"Server acknowledgement confirmed: {acknowledged}.", logger.CompletionMessage);
    }

    [Fact]
    public async Task LockedCachePreventsAcknowledgementAndRecoversAfterHostRestart()
    {
        _paths.EnsureCreated();
        var cachedFile = Path.Combine(_paths.Cache, "locked-document");
        File.WriteAllText(cachedFile, "disposable cached content");
        var registration = new RemoteWipeRegistration(Guid.NewGuid(), "https://cloud.example/dav", "https://cloud.example/", "test", "token");
        await new RemoteWipeStateStore(_paths).SaveAsync(new(Guid.NewGuid(), RemoteWipePhase.Requested, registration));
        var server = new AcknowledgementServer(HttpStatusCode.OK);
        using var http = new HttpClient(server);

        using (var locked = new FileStream(cachedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var lifetime = new TestLifetime())
        {
            var logger = new RecoveryLogger();
            using var worker = new Worker(_paths, logger, lifetime, new RemoteWipeClient(http));
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, server.RequestCount);
                Assert.Equal(RemoteWipePhase.Requested, new RemoteWipeStateStore(_paths).Read()!.Phase);
                Assert.Equal("host.remote_wipe", (await SendAsync(new("mount", Confirmed: true))).ErrorCode);
            }
            finally { await worker.StopAsync(CancellationToken.None); }
        }

        using (var lifetime = new TestLifetime())
        using (var worker = new Worker(_paths, NullLogger<Worker>.Instance, lifetime, new RemoteWipeClient(http)))
        {
            await worker.StartAsync(CancellationToken.None);
            try { await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { await worker.StopAsync(CancellationToken.None); }
        }
        Assert.False(File.Exists(cachedFile));
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(RemoteWipePhase.Completed, new RemoteWipeStateStore(_paths).Read()!.Phase);
    }

    private async Task<HostResponse> SendAsync(HostRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", HostProtocol.GetPipeName(_paths), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await HostProtocol.WriteAsync(pipe, request with { ExpectedHostBaseDirectory = AppContext.BaseDirectory }, timeout.Token);
        return (await HostProtocol.ReadAsync<HostResponse>(pipe, timeout.Token))!;
    }

    private sealed class OfflineServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline test fixture"));
    }

    private sealed class WipeServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Method.Method == "PROPFIND" ? new(HttpStatusCode.Unauthorized) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"wipe\":true}") });
    }

    private sealed class AcknowledgementServer(HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.EndsWith("/index.php/core/wipe/success", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class RecoveryLogger : ILogger<Worker>
    {
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CompletionMessage { get; private set; } = string.Empty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1007) CompletionMessage = formatter(state, exception);
            if (eventId.Id == 1008) Failed.TrySetResult();
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { _stopping.Cancel(); Stopping.TrySetResult(); }
        public void Dispose() => _stopping.Dispose();
    }
}

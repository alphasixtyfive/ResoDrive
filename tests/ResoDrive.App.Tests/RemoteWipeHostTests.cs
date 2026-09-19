using System.IO;
using System.Net;
using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
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

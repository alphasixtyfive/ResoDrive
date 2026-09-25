using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class HostInitializationStatusTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "resodrive-startup-status-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task OlderHostStatusWithoutInitializationFieldsRemainsUsable()
    {
        await using var message = new MemoryStream();
        await HostProtocol.WriteAsync(message,
            new { succeeded = true, mounts = Array.Empty<object>(), syncJobs = Array.Empty<object>() },
            CancellationToken.None);
        message.Position = 0;

        var response = await HostProtocol.ReadAsync<HostResponse>(message, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.InitializationErrorCode);
        Assert.True(HostStatusPresentation.HasUsableMountStatus(response));
    }

    [Fact]
    public async Task ReconciledAutomaticDriveIsNotExposedAsStoppedBeforeItsStartIsQueued()
    {
        _paths.EnsureCreated();
        var mount = new MountSettings
        {
            Id = Guid.NewGuid(), DisplayName = "Disposable drive", RemoteName = "cloud",
            Target = new MountTargetSettings { DriveLetter = 'R' },
            AutoMount = AutoMountPolicy.OnApplicationStart.ToString(),
        };
        using (var store = new AtomicSettingsStore(_paths))
            Assert.True((await store.SaveAsync(new ManagerSettings { Mounts = [mount] }, 0)).Succeeded);

        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new NoNetworkHandler());
        using var worker = new Worker(_paths, NullLogger<Worker>.Instance,
            new TestLifetime(), new RemoteWipeClient(http),
            beforeFirstAutoMountQueue: async token =>
            {
                reconciled.TrySetResult();
                await releaseQueue.Task.WaitAsync(token);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beforeQueue = await SendAsync("status", CancellationToken.None);
            Assert.True(beforeQueue.Succeeded, beforeQueue.ErrorMessage);
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<HostMountStatus>>(beforeQueue.Mounts));

            releaseQueue.TrySetResult();
            HostMountStatus visible;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                do
                {
                    var status = await SendAsync("status", deadline.Token);
                    var snapshot = status.Mounts?.SingleOrDefault(item => item.MountId == mount.Id);
                    if (snapshot is not null)
                    {
                        visible = snapshot;
                        break;
                    }
                    await Task.Delay(25, deadline.Token);
                } while (true);
            }
            Assert.NotEqual(MountLifecycle.Stopped.ToString(), visible.Lifecycle);
        }
        finally
        {
            releaseQueue.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedFirstLoadReportsErrorAndCanBeRetriedWithoutRestartingHost()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.SettingsFile, "{invalid settings");
        using var http = new HttpClient(new NoNetworkHandler());
        using var worker = new Worker(_paths, NullLogger<Worker>.Instance,
            new TestLifetime(), new RemoteWipeClient(http));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            HostResponse failed;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                do
                {
                    failed = await SendAsync("status", deadline.Token);
                    if (failed.InitializationErrorCode is not null)
                        break;
                    await Task.Delay(25, deadline.Token);
                } while (true);
            }
            Assert.True(failed.Succeeded);
            Assert.False(HostStatusPresentation.HasUsableMountStatus(failed));
            Assert.Equal("settings.corrupt", failed.InitializationErrorCode);
            Assert.Contains("unreadable or invalid", failed.InitializationErrorMessage, StringComparison.Ordinal);
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<HostMountStatus>>(failed.Mounts));
            Assert.True(HostProtocol.IsSameBaseDirectory(
                failed.HostBaseDirectory, AppContext.BaseDirectory));

            await File.WriteAllTextAsync(_paths.SettingsFile,
                JsonSerializer.Serialize(new ManagerSettings()));
            var reload = await SendAsync("reload", CancellationToken.None);
            Assert.True(reload.Succeeded, reload.ErrorMessage);

            var ready = await SendAsync("status", CancellationToken.None);
            Assert.True(ready.Succeeded, ready.ErrorMessage);
            Assert.True(HostStatusPresentation.HasUsableMountStatus(ready));
            Assert.Null(ready.InitializationErrorCode);
            Assert.NotNull(ready.Mounts);
            Assert.Empty(ready.Mounts);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedFirstLoadStillAllowsAuthenticatedIdleHostShutdown()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.SettingsFile, "{invalid settings");
        using var http = new HttpClient(new NoNetworkHandler());
        using var worker = new Worker(_paths, NullLogger<Worker>.Instance,
            new TestLifetime(), new RemoteWipeClient(http));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            HostResponse status;
            do
            {
                status = await SendAsync("status", deadline.Token);
                if (status.InitializationErrorCode is not null)
                    break;
                await Task.Delay(25, deadline.Token);
            } while (true);

            Assert.True(status.Succeeded);
            Assert.Equal("settings.corrupt", status.InitializationErrorCode);
            var shutdown = await SendAsync("shutdown", deadline.Token);
            Assert.True(shutdown.Succeeded, shutdown.ErrorMessage);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private async Task<HostResponse> SendAsync(string command, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", HostProtocol.GetPipeName(_paths),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await HostProtocol.WriteAsync(pipe,
            new HostRequest(command, ExpectedHostBaseDirectory: AppContext.BaseDirectory),
            timeout.Token);
        return (await HostProtocol.ReadAsync<HostResponse>(pipe, timeout.Token))!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
            Directory.Delete(_paths.Root, recursive: true);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

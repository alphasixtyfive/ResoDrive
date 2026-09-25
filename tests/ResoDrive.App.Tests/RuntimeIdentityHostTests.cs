using System.IO.Pipes;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class RuntimeIdentityHostTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "resodrive-runtime-identity-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task HostRecoversIdentityAfterInitialVersionTimeoutWithoutRestarting()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.RcloneExecutable, "test fixture");
        using (var settings = new AtomicSettingsStore(_paths))
            Assert.True((await settings.SaveAsync(new(), 0)).Succeeded);

        var runner = new RecoveringRunner();
        var locator = new RcloneRuntimeLocator(_paths, runner);
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var wipeClient = new RemoteWipeClient(http);
        using var worker = new Worker(_paths, NullLogger<Worker>.Instance, new TestLifetime(), wipeClient,
            inspectRuntimeUserAgent: true, runtimeLocator: locator,
            runtimeIdentityRetryInterval: TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var initial = await SendStatusAsync();
            Assert.Null(initial.ReportedRcloneVersion);
            Assert.Equal("rclone.version_timeout", initial.RcloneIdentityErrorCode);

            runner.Release();
            HostResponse recovered;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                do
                {
                    await Task.Delay(25, deadline.Token);
                    recovered = await SendStatusAsync();
                } while (recovered.ReportedRcloneVersion is null);
            }
            Assert.Equal("v1.75.1", recovered.ReportedRcloneVersion);
            Assert.Null(recovered.RcloneIdentityErrorCode);

            var registration = new RemoteWipeRegistration(Guid.NewGuid(),
                "https://cloud.example/dav", "https://cloud.example/", "test", "token");
            Assert.False(await wipeClient.IsWipeRequestedAsync(registration));
            Assert.Contains("Rclone: v1.75.1", handler.UserAgent, StringComparison.Ordinal);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    private async Task<HostResponse> SendStatusAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", HostProtocol.GetPipeName(_paths),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await HostProtocol.WriteAsync(pipe,
            new HostRequest("status", ExpectedHostBaseDirectory: AppContext.BaseDirectory), timeout.Token);
        return (await HostProtocol.ReadAsync<HostResponse>(pipe, timeout.Token))!;
    }

    private sealed class RecoveringRunner : IRcloneProcessRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public void Release() => _release.TrySetResult();

        public async Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null)
        {
            Assert.Equal(["version"], arguments);
            if (Interlocked.Increment(ref _runs) == 1)
                return new ProcessRunResult(0, string.Empty, string.Empty, true);
            await _release.Task.WaitAsync(cancellationToken);
            return new ProcessRunResult(0, "rclone v1.75.1\n- os/version: Microsoft Windows 11", string.Empty, false);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string UserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus));
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

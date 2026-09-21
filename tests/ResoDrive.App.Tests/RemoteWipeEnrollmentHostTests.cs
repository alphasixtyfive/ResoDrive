using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class RemoteWipeEnrollmentHostTests : IDisposable
{
    private const string AppPassword = "potato";
    private readonly ApplicationPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "resodrive-enrollment-host-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task FirstUpgradeEnrollsSavedAccountAndChecksForWipeBeforeAutomaticMounting()
    {
        _paths.EnsureCreated();
        var definition = new MountDefinition
        {
            Id = MountId.New(), DisplayName = "Disposable Nextcloud", RemoteName = "cloud",
            Target = new MountTarget.Drive('R'), ConnectionType = "WebDAV",
            AutoMount = AutoMountPolicy.OnApplicationStart,
        };
        using (var settings = new AtomicSettingsStore(_paths))
        {
            var saved = await settings.SaveAsync(new ManagerSettings
            {
                Mounts = [MountDefinitionMapper.ToSettings(definition)],
            }, 0);
            Assert.True(saved.Succeeded, saved.Error?.Message);
        }
        await new DpapiSecretStore(_paths).SaveAsync("disposable-config-password");
        await File.WriteAllTextAsync(_paths.ConfigFile, "RCLONE_ENCRYPT_V0:\nDisposable encrypted config fixture");
        await File.WriteAllTextAsync(_paths.RcloneExecutable, "Disposable placeholder; must never be launched");
        var cached = Path.Combine(_paths.Cache, "disposable-document");
        await File.WriteAllTextAsync(cached, "cached document before wipe");
        Assert.False(File.Exists(_paths.RemoteWipeFile));
        var configBefore = await File.ReadAllBytesAsync(_paths.ConfigFile);
        var settingsBefore = await File.ReadAllBytesAsync(_paths.SettingsFile);

        var configuration = new SavedConfiguration();
        var enrollment = new RemoteWipeEnrollmentService(_paths, configuration, _paths.RcloneExecutable);
        using var server = new PendingWipeServer();
        using var http = new HttpClient(server);
        using var lifetime = new TestLifetime();
        var logger = new RecordingLogger();
        using var worker = new Worker(_paths, logger, lifetime, new RemoteWipeClient(http), enrollment);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await server.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Query the real host pipe while the first newly enrolled account check is blocked.
            var status = await ReadStatusAsync();
            Assert.True(status.Succeeded, status.ErrorMessage);
            Assert.NotNull(status.Mounts);
            Assert.Empty(status.Mounts);
            Assert.NotNull(status.SyncJobs);
            Assert.Empty(status.SyncJobs);
            Assert.False(lifetime.Stopping.Task.IsCompleted);
            var registration = Assert.Single(await new RemoteWipeStore(_paths).LoadAsync());
            Assert.Equal(definition.Id.Value, registration.MountId);
            Assert.Equal(AppPassword, registration.AppToken);
            Assert.Equal(1, configuration.ReadCount);
            Assert.Equal(configBefore, await File.ReadAllBytesAsync(_paths.ConfigFile));
            Assert.Equal(settingsBefore, await File.ReadAllBytesAsync(_paths.SettingsFile));

            server.ReleaseProbe.TrySetResult();
            await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            server.ReleaseProbe.TrySetResult();
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        }

        var wipe = new RemoteWipeStateStore(_paths).Read();
        Assert.NotNull(wipe);
        Assert.Equal(RemoteWipePhase.Requested, wipe.Phase);
        Assert.Equal(definition.Id.Value, wipe.Registration!.MountId);
        Assert.True(worker.RestartForRemoteWipe);
        Assert.True(worker.CanResumeRemoteWipe);
        Assert.Equal("cached document before wipe", await File.ReadAllTextAsync(cached));
        Assert.True(File.Exists(_paths.ConfigSecretFile));
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(_paths.ConfigFile));
        Assert.Equal(1, server.WipeChecks);
        Assert.Equal(0, server.Acknowledgements);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(AppPassword, message, StringComparison.Ordinal));
    }

    private async Task<HostResponse> ReadStatusAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", HostProtocol.GetPipeName(_paths),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await HostProtocol.WriteAsync(pipe,
            new HostRequest("status", ExpectedHostBaseDirectory: AppContext.BaseDirectory), timeout.Token);
        return (await HostProtocol.ReadAsync<HostResponse>(pipe, timeout.Token))!;
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(_paths.Root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root));
        Assert.StartsWith("resodrive-enrollment-host-", Path.GetFileName(root), StringComparison.Ordinal);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class SavedConfiguration : IRcloneRemoteConfigurationAccess
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAsync(
            string executablePath, string configPath, string passwordCommand,
            IReadOnlyCollection<string> remoteNames, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("cloud", Assert.Single(remoteNames));
            ReadCount++;
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> remotes =
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["cloud"] = new Dictionary<string, string>
                    {
                        ["type"] = "webdav", ["vendor"] = "nextcloud", ["user"] = "test",
                        ["url"] = "https://cloud.example/nextcloud/remote.php/dav/files/test",
                        // Upstream rclone known-answer vector for the disposable app password.
                        ["pass"] = "YWFhYWFhYWFhYWFhYWFhYXMaGgIlEQ",
                    },
                };
            return Task.FromResult(remotes);
        }
    }

    private sealed class PendingWipeServer : HttpMessageHandler
    {
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WipeChecks { get; private set; }
        public int Acknowledgements { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method.Method == "PROPFIND")
            {
                Assert.Equal("https://cloud.example/nextcloud/remote.php/dav/files/test", request.RequestUri!.AbsoluteUri);
                Assert.Equal("test:" + AppPassword,
                    Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
                ProbeStarted.TrySetResult();
                await ReleaseProbe.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.Unauthorized);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/index.php/core/wipe/success", StringComparison.Ordinal))
            {
                Acknowledgements++;
                return new(HttpStatusCode.OK);
            }
            Assert.EndsWith("/index.php/core/wipe/check", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("token=potato", await request.Content!.ReadAsStringAsync(cancellationToken));
            WipeChecks++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"wipe\":true}") };
        }
    }

    private sealed class RecordingLogger : ILogger<Worker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (exception is not null) Messages.Enqueue(exception.ToString());
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

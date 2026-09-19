using System.Net;
using System.Net.Http.Headers;

namespace ResoDrive.Windows.Tests;

public sealed class RemoteWipeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "resodrive-remote-wipe-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClientChecksOnlyAfterUnauthorizedProbeAndSignalsCompletion()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, "{\"wipe\":true}"),
            Response(HttpStatusCode.OK));
        var client = new RemoteWipeClient(new HttpClient(handler));
        var registration = new RemoteWipeRegistration(
            Guid.NewGuid(),
            "https://cloud.example/remote.php/dav/files/alex",
            "https://cloud.example/",
            "alex",
            "app-token");

        Assert.True(await client.IsWipeRequestedAsync(registration));
        await client.SignalSuccessAsync(registration);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/index.php/core/wipe/check", handler.Requests[1].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.EndsWith("/index.php/core/wipe/success", handler.Requests[2].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Basic", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task ClientLeavesDataAloneWhenServerHasNoWipeRequest()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, "{\"wipe\":false}"));
        var client = new RemoteWipeClient(new HttpClient(handler));
        var registration = new RemoteWipeRegistration(
            Guid.NewGuid(), "https://cloud.example/dav", "https://cloud.example/", "alex", "token");

        Assert.False(await client.IsWipeRequestedAsync(registration));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void CleanupRemovesAccountStateIncludingRegistrationSecrets()
    {
        var paths = new ApplicationPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "settings");
        File.WriteAllText(paths.ConfigFile, "config");
        File.WriteAllText(paths.RemoteWipeFile, "protected-token");
        File.WriteAllText(Path.Combine(paths.Cache, "cached-file"), "cache");
        File.WriteAllText(Path.Combine(paths.Logs, "ui.log"), "log");

        RemoteWipeCleanup.DeleteAccountData(paths);

        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.ConfigFile));
        Assert.False(File.Exists(paths.RemoteWipeFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Logs));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string? body = null) => new(statusCode)
    {
        Content = body is null ? null : new StringContent(body)
    };
}

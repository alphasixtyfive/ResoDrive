using System.Net;
using System.Security.Cryptography;
using System.Text;
using ResoDrive.Core;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public async Task CheckingAgainReplacesTheAvailableReleaseAndDownloadAddress()
    {
        using var handler = new ChangingReleaseHandler();
        using var client = new HttpClient(handler);
        var service = new ApplicationUpdateService(client, ProductLinks.LatestRelease);
        var first = await service.CheckAsync("0.3.30");
        var second = await service.CheckAsync("0.3.30");
        Assert.Equal("0.3.34", first.Value!.AvailableVersion);
        Assert.Equal("0.3.35", second.Value!.AvailableVersion);
        Assert.EndsWith("/v0.3.35/resodrive-win-x64-0.3.35.msi", second.Value.InstallerDownload!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(second.Value.InstallerDownload.AbsoluteUri + ".sha256", second.Value.ChecksumDownload!.AbsoluteUri);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryReusesOnlyThePackageMatchingTheCurrentPublishedChecksum(bool validCache)
    {
        const string name = "resodrive-win-x64-0.3.35.msi";
        var installer = new Uri(ProductLinks.Repository.AbsoluteUri + "/releases/download/v0.3.35/" + name);
        var checksum = new Uri(installer.AbsoluteUri + ".sha256");
        var payload = Encoding.UTF8.GetBytes("verified package for retry");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var responses = new Dictionary<string, byte[]> { [checksum.AbsoluteUri] = Encoding.ASCII.GetBytes(hash + "  " + name) };
        // A valid cache must succeed even when no installer download is available.
        if (!validCache) responses.Add(installer.AbsoluteUri, payload);
        using var client = new HttpClient(new RoutingHandler(responses));
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-cached-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, validCache ? payload : "untrusted old bytes"u8.ToArray());
        ApplicationUpdateDownloadProgress? finalProgress = null;
        try
        {
            var result = await new ApplicationUpdateService(client, ProductLinks.LatestRelease).DownloadInstallerAsync(
                new ApplicationUpdateCheck("0.3.30", "0.3.35", true, ProductLinks.LatestRelease, installer, checksum),
                directory, new CallbackProgress<ApplicationUpdateDownloadProgress>(value => finalProgress = value));
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(hash, result.Value!.Sha256);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.Value.InstallerPath));
            Assert.Equal(payload.Length, finalProgress!.BytesReceived);
            Assert.Equal(payload.Length, finalProgress.TotalBytes);
            Assert.False(File.Exists(path + ".download"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StalledChecksumRequestIsRetryableAndNeverStartsTheInstallerDownload()
    {
        var clock = new DownloadTestClock();
        using var handler = new StalledChecksumHandler();
        using var client = new HttpClient(handler);
        var service = new ApplicationUpdateService(client, ProductLinks.LatestRelease, clock);
        var installer = new Uri(ProductLinks.Repository.AbsoluteUri + "/releases/download/v0.3.35/resodrive-win-x64-0.3.35.msi");
        var update = new ApplicationUpdateCheck("0.3.30", "0.3.35", true, ProductLinks.LatestRelease,
            installer, new Uri(installer.AbsoluteUri + ".sha256"));
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-checksum-stall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var operation = service.DownloadInstallerAsync(update, directory);
            await handler.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(45));
            var result = await operation;
            Assert.False(result.Succeeded);
            Assert.Equal("app.update_download_failed", result.Error?.Code);
            Assert.True(result.Error?.IsTransient);
            Assert.Contains("checksum request timed out", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(directory));
            Assert.Equal(1, handler.Requests);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CheckAsync_ReportsNewerStableRelease()
    {
        using var client = Client(
            HttpStatusCode.OK,
            "https://github.com/alphasixtyfive/resodrive/releases/tag/v0.3.0");
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://api.github.test/releases/latest"));

        var result = await service.CheckAsync("0.2.28");

        Assert.True(result.Succeeded);
        Assert.True(result.Value?.UpdateAvailable);
        Assert.Equal("0.3.0", result.Value?.AvailableVersion);
        Assert.Equal(
            "https://github.com/alphasixtyfive/resodrive/releases/tag/v0.3.0",
            result.Value?.ReleasePage.AbsoluteUri);
        Assert.EndsWith("resodrive-win-x64-0.3.0.msi", result.Value?.InstallerDownload?.AbsoluteUri);
    }

    [Fact]
    public async Task CheckAsyncRequestsFreshLatestReleaseRedirect()
    {
        var handler = new ResponseHandler(
            HttpStatusCode.OK,
            new Uri("https://github.com/alphasixtyfive/resodrive/releases/tag/v0.3.9"));
        using var client = new HttpClient(handler);
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://api.github.test/releases/latest"));

        var result = await service.CheckAsync("0.3.8");

        Assert.True(result.Succeeded);
        Assert.True(handler.SawNoCacheRequest);
    }

    [Fact]
    public async Task CheckAsync_DoesNotTrustNonRepositoryReleaseLink()
    {
        using var client = Client(HttpStatusCode.OK, "https://example.com/releases/tag/v0.2.29");
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://api.github.test/releases/latest"));

        var result = await service.CheckAsync("0.2.29");

        Assert.False(result.Succeeded);
        Assert.Equal("app.update_response_invalid", result.Error?.Code);
    }

    [Fact]
    public async Task CheckAsync_HandlesRepositoryWithoutPublishedRelease()
    {
        using var client = Client(HttpStatusCode.NotFound, ProductLinks.LatestRelease.AbsoluteUri);
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://api.github.test/releases/latest"));

        var result = await service.CheckAsync("0.2.29");

        Assert.False(result.Succeeded);
        Assert.Equal("app.update_check_failed", result.Error?.Code);
        Assert.Contains("No published", result.Error?.Message);
    }

    [Fact]
    public async Task CheckAsync_RejectsLatestLinkThatDidNotRedirect()
    {
        using var client = Client(HttpStatusCode.OK, ProductLinks.LatestRelease.AbsoluteUri);
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://api.github.test/releases/latest"));

        var result = await service.CheckAsync("0.2.29");

        Assert.False(result.Succeeded);
        Assert.Equal("app.update_response_invalid", result.Error?.Code);
    }

    [Fact]
    public async Task CheckAsync_RejectsPrereleaseTag()
    {
        using var client = Client(
            HttpStatusCode.OK,
            "https://github.com/alphasixtyfive/resodrive/releases/tag/v0.3.0-beta.1");
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.test/releases/latest"));

        var result = await service.CheckAsync("0.2.29");

        Assert.False(result.Succeeded);
        Assert.Equal("app.update_response_invalid", result.Error?.Code);
    }

    [Theory]
    [InlineData("resodrive")]
    [InlineData("ResoDrive")]
    public async Task DownloadInstallerAsync_VerifiesChecksumAndReplacesOldPackage(string repository)
    {
        var installerUri = new Uri(
            $"https://github.com/alphasixtyfive/{repository}/releases/download/v0.3.0/resodrive-win-x64-0.3.0.msi");
        var checksumUri = new Uri(installerUri.AbsoluteUri + ".sha256");
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var checksum = Encoding.ASCII.GetBytes(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() + "  resodrive-win-x64-0.3.0.msi");
        using var client = new HttpClient(new RoutingHandler(new Dictionary<string, byte[]>
        {
            [installerUri.AbsoluteUri] = payload,
            [checksumUri.AbsoluteUri] = checksum,
        }));
        var service = new ApplicationUpdateService(
            client,
            new Uri("https://api.github.test/releases/latest"));
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-app-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "resodrive-win-x64-0.2.29.msi"), "old");
        try
        {
            var result = await service.DownloadInstallerAsync(
                new ApplicationUpdateCheck(
                    "0.2.29",
                    "0.3.0",
                    true,
                    new Uri("https://github.com/alphasixtyfive/resodrive/releases/tag/v0.3.0"),
                    installerUri,
                    checksumUri),
                directory);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.Value!.InstallerPath));
            Assert.False(File.Exists(Path.Combine(directory, "resodrive-win-x64-0.2.29.msi")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("v0.2.9/resodrive-win-x64-0.3.0.msi")]
    [InlineData("v0.3.0/other.msi")]
    [InlineData("v0.3.0/ResoDrive-win-x64-0.3.0.msi")]
    [InlineData("v0.3.0/resodrive-win-x64-0.3.0.msi?download=1")]
    [InlineData("v0.3.0/resodrive-win-x64-0.3.0.msi#other")]
    public async Task DownloadInstallerAsync_RejectsAssetOutsideExactVersionAndFilename(string suffix)
    {
        using var client = new HttpClient(new NoRequestHandler());
        var service = new ApplicationUpdateService(client, ProductLinks.LatestRelease);
        var installer = new Uri(ProductLinks.Repository.AbsoluteUri + "/releases/download/" + suffix);
        var update = new ApplicationUpdateCheck("0.2.29", "0.3.0", true,
            ProductLinks.LatestRelease, installer, new Uri(installer.AbsoluteUri + ".sha256"));

        var result = await service.DownloadInstallerAsync(update, Path.GetTempPath());

        Assert.False(result.Succeeded);
        Assert.Equal("app.update_download_invalid", result.Error?.Code);
    }

    [Theory]
    [InlineData("wrong-name")]
    [InlineData("bare-hash")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("mismatch")]
    public async Task DownloadInstallerAsync_RejectsInvalidChecksumWithoutPublishingPackage(string scenario)
    {
        const string name = "resodrive-win-x64-0.3.0.msi";
        var installer = new Uri(ProductLinks.Repository.AbsoluteUri + "/releases/download/v0.3.0/" + name);
        var checksumUri = new Uri(installer.AbsoluteUri + ".sha256");
        var payload = Encoding.UTF8.GetBytes("test installer bytes");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var checksum = scenario switch
        {
            "wrong-name" => hash + "  different.msi",
            "bare-hash" => hash,
            "duplicate" => hash + "  " + name + "\n" + hash + "  " + name,
            "oversized" => new string('a', 4097),
            _ => new string('0', 64) + "  " + name,
        };
        using var client = new HttpClient(new RoutingHandler(new Dictionary<string, byte[]>
        {
            [installer.AbsoluteUri] = payload,
            [checksumUri.AbsoluteUri] = Encoding.ASCII.GetBytes(checksum),
        }));
        var service = new ApplicationUpdateService(client, ProductLinks.LatestRelease);
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-checksum-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await service.DownloadInstallerAsync(
                new ApplicationUpdateCheck("0.2.29", "0.3.0", true, ProductLinks.LatestRelease, installer, checksumUri), directory);

            Assert.False(result.Succeeded);
            Assert.Equal(scenario == "mismatch" ? "app.update_checksum_mismatch" : "app.update_checksum_invalid", result.Error?.Code);
            Assert.False(File.Exists(Path.Combine(directory, name)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Invalid metadata must be rejected before making an HTTP request.");
    }

    private sealed class ChangingReleaseHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.CacheControl?.NoCache);
            var version = ++Requests == 1 ? "0.3.34" : "0.3.35";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    ProductLinks.Repository.AbsoluteUri + "/releases/tag/v" + version),
            });
        }
    }

    private sealed class StalledChecksumHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.EndsWith(".sha256", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The stalled checksum request must be cancelled.");
        }
    }

    private static HttpClient Client(HttpStatusCode statusCode, string finalUri) =>
        new(new ResponseHandler(statusCode, new Uri(finalUri)));

    private sealed class ResponseHandler(HttpStatusCode statusCode, Uri finalUri) : HttpMessageHandler
    {
        public bool SawNoCacheRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SawNoCacheRequest = request.Headers.CacheControl?.NoCache == true &&
                request.Headers.Pragma.Any(value => value.Name.Equals("no-cache", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = new HttpRequestMessage(request.Method, finalUri),
            });
        }
    }

    private sealed class RoutingHandler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.RequestUri?.AbsoluteUri ?? string.Empty;
            return Task.FromResult(responses.TryGetValue(key, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                    RequestMessage = request,
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }
}

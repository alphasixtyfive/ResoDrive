using System.Net;
using System.Text;

namespace ResoDrive.Windows.Tests;

public sealed class RemoteWipeProtocolTests
{
    private static RemoteWipeRegistration Registration => new(Guid.NewGuid(),
        "https://cloud.example/nextcloud/remote.php/dav/files/test", "https://cloud.example/nextcloud", "test", "dedicated-token");

    [Theory]
    [InlineData(200)]
    [InlineData(207)]
    [InlineData(302)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task SuccessfulAccessRedirectsAndServerErrorsNeverTriggerAWipe(int status)
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return new((HttpStatusCode)status); }));
        using var client = new RemoteWipeClient(http);
        Assert.False(await client.IsWipeRequestedAsync(Registration));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("{\"wipe\":false}")]
    [InlineData("{\"wipe\":\"true\"}")]
    [InlineData("{\"wipe\":1}")]
    [InlineData("{\"Wipe\":true}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task OnlyAnExplicitBooleanTrueAuthorizesDeletion(string body)
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => ++count == 1
            ? new(HttpStatusCode.Forbidden)
            : new(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var client = new RemoteWipeClient(http);
        Assert.False(await client.IsWipeRequestedAsync(Registration));
    }

    [Theory]
    [InlineData("http://cloud.example/", "https://cloud.example/dav")]
    [InlineData("https://other.example/", "https://cloud.example/dav")]
    [InlineData("https://cloud.example:444/", "https://cloud.example/dav")]
    [InlineData("https://user:password@cloud.example/", "https://cloud.example/dav")]
    [InlineData("https://cloud.example/?url=other", "https://cloud.example/dav")]
    [InlineData("https://cloud.example/", "https://cloud.example/dav#fragment")]
    public async Task UntrustedEndpointsNeverReceiveTheToken(string server, string probe)
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request allowed")));
        using var client = new RemoteWipeClient(http);
        Assert.False(await client.IsWipeRequestedAsync(Registration with { ServerBaseUrl = server, ProbeEndpoint = probe }));
    }

    [Fact]
    public async Task RequestsPreserveSubdirectoryAndSendTokenOnlyInTheBody()
    {
        List<(string Method, Uri Uri, string? Authorization, string Body)> requests = [];
        using var http = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            return requests.Count == 1 ? new(HttpStatusCode.Unauthorized) :
                new(HttpStatusCode.OK) { Content = new StringContent("{\"wipe\":true}") };
        }));
        using var client = new RemoteWipeClient(http);
        Assert.True(await client.IsWipeRequestedAsync(Registration));
        await client.SignalSuccessAsync(Registration);
        Assert.Equal("PROPFIND", requests[0].Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test:dedicated-token")), requests[0].Authorization);
        Assert.Equal("/nextcloud/index.php/core/wipe/check", requests[1].Uri.AbsolutePath);
        Assert.Equal("/nextcloud/index.php/core/wipe/success", requests[2].Uri.AbsolutePath);
        Assert.All(requests.Skip(1), request =>
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("token=dedicated-token", request.Body);
            Assert.Null(request.Authorization);
            Assert.Empty(request.Uri.Query);
        });
    }

    [Fact]
    public async Task OversizedResponsesCannotAuthorizeDeletion()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => ++count == 1 ? new(HttpStatusCode.Unauthorized) :
            new(HttpStatusCode.OK) { Content = new StringContent("{\"wipe\":true,\"padding\":\"" + new string('x', 5000) + "\"}") }));
        using var client = new RemoteWipeClient(http);
        Assert.False(await client.IsWipeRequestedAsync(Registration));
    }

    [Fact]
    public async Task RequestTimeoutDoesNotBecomeAWipeOrAnUnhandledHostFailure()
    {
        using var http = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http, TimeSpan.FromMilliseconds(40));
        Assert.False(await client.IsWipeRequestedAsync(Registration));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SignalSuccessAsync(Registration));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.IsWipeRequestedAsync(Registration, cancellation.Token));
    }

    [Fact]
    public async Task BodyReadSharesTheDeadlineAfterHeadersHaveArrived()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => ++count == 1 ? new(HttpStatusCode.Unauthorized) :
            new(HttpStatusCode.OK) { Content = new StalledContent() }));
        using var client = new RemoteWipeClient(http, TimeSpan.FromMilliseconds(40));
        Assert.False(await client.IsWipeRequestedAsync(Registration).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Body reading must be cancellable");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}

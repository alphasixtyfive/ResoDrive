using System.Net;
using System.Net.Sockets;
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
    [InlineData("{\"wipe\":false,\"wipe\":true}")]
    [InlineData("{\"wipe\":true,\"wipe\":false}")]
    [InlineData("{\"wipe\":true,\"wipe\":true}")]
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
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(206)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task WipeCommandRequiresHttp200EvenWithAnExplicitTrueBody(int status)
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => ++count == 1
            ? new(HttpStatusCode.Unauthorized)
            : new((HttpStatusCode)status) { Content = new StringContent("{\"wipe\":true}") }));
        using var client = new RemoteWipeClient(http);
        Assert.False(await client.IsWipeRequestedAsync(Registration));
        Assert.Equal(2, count);
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
        var registration = Registration with { ServerBaseUrl = server, ProbeEndpoint = probe };
        Assert.False(await client.IsWipeRequestedAsync(registration));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SignalSuccessAsync(registration));
    }

    [Theory]
    [InlineData("dedicated-token", "token=dedicated-token")]
    [InlineData("dedicated+token&name=ä/?#", "token=dedicated%2Btoken%26name%3D%C3%A4%2F%3F%23")]
    public async Task RequestsPreserveSubdirectoryAndEncodeTokenOnlyInTheBody(string token, string encodedBody)
    {
        List<(string Method, Uri Uri, string? Authorization, string? ContentType, string Body)> requests = [];
        using var http = new HttpClient(new AsyncHandler(async (request, cancellationToken) =>
        {
            requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return requests.Count == 1 ? new(HttpStatusCode.Unauthorized) :
                new(HttpStatusCode.OK) { Content = new StringContent("{\"wipe\":true}") };
        }));
        using var client = new RemoteWipeClient(http);
        var registration = Registration with { AppToken = token };
        Assert.True(await client.IsWipeRequestedAsync(registration));
        Assert.True(await client.SignalSuccessAsync(registration));
        Assert.Equal("PROPFIND", requests[0].Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"test:{token}")), requests[0].Authorization);
        Assert.Equal("/nextcloud/index.php/core/wipe/check", requests[1].Uri.AbsolutePath);
        Assert.Equal("/nextcloud/index.php/core/wipe/success", requests[2].Uri.AbsolutePath);
        Assert.All(requests.Skip(1), request =>
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal(encodedBody, request.Body);
            Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
            Assert.Null(request.Authorization);
            Assert.Empty(request.Uri.Query);
        });
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(206)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task UnexpectedAcknowledgementStatusMustBeRetried(int status)
    {
        using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)status)));
        using var client = new RemoteWipeClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SignalSuccessAsync(Registration));
    }

    [Fact]
    public async Task RetiredTokenDoesNotClaimAnAcknowledgement()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        using var client = new RemoteWipeClient(http);
        Assert.False(await client.SignalSuccessAsync(Registration));
    }

    [Fact]
    public async Task DefaultTransportDoesNotReuseSessionCookiesOrFollowRedirects()
    {
        // Exercise the real transport on loopback with disposable HTTP data. No
        // TLS exceptions, production credentials or external servers are used.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var requests = ServeTransportResponsesAsync(listener, cancellation.Token);
        using var http = RemoteWipeClient.CreateHttpClient();
        using var first = await http.GetAsync(endpoint, cancellation.Token);
        using var second = await http.GetAsync(endpoint, cancellation.Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.All(await requests, request => Assert.DoesNotContain("Cookie:", request, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<string>> ServeTransportResponsesAsync(
        TcpListener listener, CancellationToken cancellationToken)
    {
        List<string> requests = [];
        string[] responses = [
            "HTTP/1.1 200 OK\r\nSet-Cookie: session=disposable; Path=/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        ];
        foreach (var response in responses)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var headers = new StringBuilder();
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
                headers.AppendLine(line);
            requests.Add(headers.ToString());
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
        return requests;
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

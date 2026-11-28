using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ResoDrive.Windows.Tests;

public sealed class ResumableHttpDownloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledBodyPreservesPartialBytesAndDistinguishesUserCancellation(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var clock = new DownloadTestClock();
        using var stream = new PausedStream();
        using var client = new HttpClient(new StreamHandler(stream));
        using var cancellation = new CancellationTokenSource();
        var operation = ResumableHttpDownload.DownloadAsync(client,
            new Uri("https://downloads.example.test/package"), directory.Destination, 1024,
            null, cancellation.Token, clock);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cancellation.Cancel();
        else clock.Advance(TimeSpan.FromMinutes(2));

        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => operation);
            Assert.Contains("resume", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(directory.Destination));
        Assert.True(clock.Timer.Disposed);
    }

    [Fact]
    public async Task StalledResponseHeadersTimeOutBeforeAnyFileIsWritten()
    {
        using var directory = new TemporaryDirectory();
        var clock = new DownloadTestClock();
        using var handler = new PausedHandler();
        using var client = new HttpClient(handler);
        var operation = ResumableHttpDownload.DownloadAsync(client,
            new Uri("https://downloads.example.test/package"), directory.Destination, 1024,
            null, CancellationToken.None, clock);
        await handler.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<HttpRequestException>(() => operation);
        Assert.False(File.Exists(directory.Destination));
    }

    [Fact]
    public async Task SlowDownloadRemainsAllowedWhileBytesKeepArriving()
    {
        using var directory = new TemporaryDirectory();
        var clock = new DownloadTestClock();
        using var stream = new SlowStream(clock);
        using var client = new HttpClient(new StreamHandler(stream));
        var diskOrUiDelay = new CallbackProgress<DownloadProgress>(value => {
            // Processing received data is not waiting on the network.
            if (value.BytesReceived > 0) clock.Advance(TimeSpan.FromMinutes(3));
        });
        var result = await ResumableHttpDownload.DownloadAsync(client,
            new Uri("https://downloads.example.test/package"), directory.Destination, 1024,
            diskOrUiDelay, CancellationToken.None, clock);

        Assert.Equal(3, result.BytesReceived);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(directory.Destination));
        Assert.True(clock.Timer.Disposed);
    }

    [Fact]
    public async Task DownloadAsync_ContinuesAnExistingPartialFile()
    {
        var content = Encoding.UTF8.GetBytes("a complete download");
        var partialLength = 5;
        using var handler = new RangeHandler(content, partialLength);
        using var client = new HttpClient(handler);
        var directory = Path.Combine(Path.GetTempPath(), "resodrive-download-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "package.download");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(destination, content[..partialLength]);

        try
        {
            var result = await ResumableHttpDownload.DownloadAsync(
                client,
                new Uri("https://downloads.example.test/package"),
                destination,
                1024,
                null,
                CancellationToken.None);

            Assert.Equal(content.Length, result.BytesReceived);
            Assert.Equal(content.Length, result.TotalBytes);
            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.Equal(partialLength, handler.RequestedOffset);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RangeHandler(byte[] content, int expectedOffset) : HttpMessageHandler
    {
        public long? RequestedOffset { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedOffset = request.Headers.Range?.Ranges.Single().From;
            Assert.Equal(expectedOffset, RequestedOffset);
            var remaining = content[expectedOffset..];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remaining),
                RequestMessage = request,
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                expectedOffset,
                content.Length - 1,
                content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream), RequestMessage = request });
    }

    private sealed class PausedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A stalled request must be cancelled.");
        }
    }

    private abstract class TestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PausedStream : TestStream
    {
        private bool _sent;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent) { _sent = true; new byte[] { 1, 2, 3 }.CopyTo(buffer); return 3; }
            Waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A stalled read must be cancelled.");
        }
    }

    private sealed class SlowStream(DownloadTestClock clock) : TestStream
    {
        private byte _next = 1;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next > 3) return ValueTask.FromResult(0);
            clock.Advance(TimeSpan.FromSeconds(90));
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span[0] = _next++;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "resodrive-download-" + Guid.NewGuid().ToString("N"));
        public string Destination => Path.Combine(_path, "package.download");
        public TemporaryDirectory() => Directory.CreateDirectory(_path);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}

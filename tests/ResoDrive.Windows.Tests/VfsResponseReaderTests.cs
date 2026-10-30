using System.Buffers;
using System.Text;

namespace ResoDrive.Windows.Tests;

public sealed class VfsResponseReaderTests
{
    [Fact]
    public async Task ThreeSmallControlResponsesUseOnlySmallClearedBuffers()
    {
        var pool = new TrackingPool();
        const string json = "{\"uploadsQueued\":0}";
        for (var index = 0; index < 3; index++)
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Assert.Equal(json, await VfsStatusReader.ReadResponseAsync(stream, CancellationToken.None, pool));
        }
        Assert.Equal(3, pool.RentSizes.Count);
        Assert.All(pool.RentSizes, size => Assert.Equal(8 * 1024, size));
        Assert.Equal(24 * 1024, pool.ReturnedBytes);
        Assert.Equal(3, pool.ClearedReturns);
        Assert.Equal(0, pool.Outstanding);
        var captureDirectory = Environment.GetEnvironmentVariable("RESODRIVE_PERF_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
            var previousBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024 + 1);
            try
            {
                File.WriteAllText(Path.Combine(captureDirectory, "control-buffers.json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        responses = 3, previousSharedPoolClearedBytes = 3L * previousBuffer.Length,
                        newSmallResponseClearedBytes = pool.ReturnedBytes,
                        newRequestedBufferBytes = 8 * 1024,
                    }));
            }
            finally { ArrayPool<byte>.Shared.Return(previousBuffer, clearArray: true); }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8 * 1024)]
    [InlineData(8 * 1024 + 1)]
    [InlineData(1024 * 1024)]
    public async Task ReadsThroughEofIncludingExactCapacityAndMaximumLength(int length)
    {
        var pool = new TrackingPool();
        var expected = new string('x', length);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(expected));

        var actual = await VfsStatusReader.ReadResponseAsync(stream, CancellationToken.None, pool);

        Assert.Equal(expected, actual);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.RentSizes.Count, pool.ClearedReturns);
        Assert.All(pool.RentSizes, size => Assert.InRange(size, 8 * 1024, 1024 * 1024 + 1));
    }

    [Fact]
    public async Task OversizedResponseStopsAfterOneByteBeyondTheHardLimit()
    {
        var pool = new TrackingPool();
        await using var stream = new MemoryStream(new byte[2 * 1024 * 1024]);

        var actual = await VfsStatusReader.ReadResponseAsync(stream, CancellationToken.None, pool);

        Assert.Null(actual);
        Assert.Equal(1024 * 1024 + 1, stream.Position);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.RentSizes.Count, pool.ClearedReturns);
    }

    [Fact]
    public async Task PartialReadsAndUtf8CharactersSurviveBufferGrowth()
    {
        var pool = new TrackingPool();
        var expected = new string('\u03B1', 5000);
        await using var stream = new PartialReadStream(Encoding.UTF8.GetBytes(expected));

        var actual = await VfsStatusReader.ReadResponseAsync(stream, CancellationToken.None, pool);

        Assert.Equal(expected, actual);
        Assert.Equal(2, pool.RentSizes.Count);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.RentSizes.Count, pool.ClearedReturns);
    }

    [Fact]
    public async Task CancellationAndReadFailuresReturnEveryRentedBufferCleared()
    {
        var cancelledPool = new TrackingPool();
        await using var empty = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VfsStatusReader.ReadResponseAsync(empty, cancellation.Token, cancelledPool));
        Assert.Equal(0, cancelledPool.Outstanding);
        Assert.Equal(1, cancelledPool.ClearedReturns);

        var failedPool = new TrackingPool();
        await using var failure = new FailedReadStream();
        await Assert.ThrowsAsync<IOException>(() =>
            VfsStatusReader.ReadResponseAsync(failure, CancellationToken.None, failedPool));
        Assert.Equal(0, failedPool.Outstanding);
        Assert.Equal(2, failedPool.ClearedReturns);
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        internal List<int> RentSizes { get; } = [];
        internal int ReturnedBytes { get; private set; }
        internal int ClearedReturns { get; private set; }
        internal int Outstanding { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            RentSizes.Add(minimumLength);
            Outstanding++;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(clearArray);
            Array.Clear(array);
            ClearedReturns++;
            ReturnedBytes += array.Length;
            Outstanding--;
        }
    }

    private sealed class PartialReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    private sealed class FailedReadStream() : MemoryStream(new byte[8 * 1024])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == 0 ? base.ReadAsync(buffer, cancellationToken) :
                ValueTask.FromException<int>(new IOException("Simulated control stream failure."));
    }
}

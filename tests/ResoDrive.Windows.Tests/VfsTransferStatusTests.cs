using System.Text.Json;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class VfsTransferStatusTests
{
    [Theory]
    [InlineData(0, 0, 0, false, false)]
    [InlineData(1, 0, 0, false, true)]
    [InlineData(0, 1, 0, false, true)]
    [InlineData(0, 0, 1, false, true)]
    [InlineData(0, 0, 0, true, true)]
    public void QueuedActiveFailedAndOutOfSpaceCachesNeedAttention(
        long queued, long uploading, long errors, bool outOfSpace, bool expected)
    {
        var status = VfsTransferStatus.Parse(JsonSerializer.Serialize(new
        {
            diskCache = new { uploadsQueued = queued, uploadsInProgress = uploading, erroredFiles = errors, bytesUsed = 123, outOfSpace }
        }));
        Assert.NotNull(status);
        Assert.Equal(expected, status.NeedsAttention);
        Assert.Equal(123, status.CacheBytes);
    }

    [Fact]
    public void CacheOffIsUnknownRatherThanZeroUploads() =>
        Assert.Null(VfsTransferStatus.Parse("{\"metadataCache\":{}}"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"uploadsQueued\":-1}")]
    [InlineData("{\"uploadsQueued\":0,\"uploadsInProgress\":0,\"erroredFiles\":0,\"bytesUsed\":0}")]
    public void IncompleteStatisticsCannotBeReportedAsSafe(string cache) =>
        Assert.Throws<JsonException>(() => VfsTransferStatus.Parse("{\"diskCache\":" + cache + "}"));

    [Fact]
    public async Task ControlCredentialsCannotBeSentToRemoteServers() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            VfsStatusReader.ReadAsync("example.com:1234", "user", "secret", CancellationToken.None));

    [Fact]
    public void LostControlConnectionCannotEraseKnownPendingWork()
    {
        var previous = new VfsTransferStatus(2, 1, 0, 123, false);
        var unknown = new MountUploadObservation(null, previous);
        Assert.True(unknown.BlocksStop);
        Assert.False(unknown.HasCacheError);
        Assert.Contains("unavailable", unknown.Description, StringComparison.Ordinal);
        Assert.Contains("2 queued", unknown.Description, StringComparison.Ordinal);

        var recovered = new MountUploadObservation(new(0, 0, 0, 123, false), previous);
        Assert.False(recovered.BlocksStop);
        Assert.Equal("No queued uploads", recovered.Description);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void CacheErrorsRemainVisibleWhenControlConnectionIsLost(long errors, bool full)
    {
        var status = new MountUploadObservation(null, new(0, 0, errors, 123, full));
        Assert.True(status.BlocksStop);
        Assert.True(status.HasCacheError);
    }
}

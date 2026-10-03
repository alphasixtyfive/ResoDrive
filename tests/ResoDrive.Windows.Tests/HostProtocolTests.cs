using ResoDrive.Core.Domain;

namespace ResoDrive.Windows.Tests;

public sealed class HostProtocolTests
{
    [Fact]
    public async Task LargeUnicodeHistoryFitsStatusFrameAndPreservesActiveWorkCount()
    {
        var syncs = Enumerable.Range(0, 900).Select(index => new HostSyncStatus(Guid.NewGuid(), Guid.NewGuid(),
            index == 899 ? "Running" : "Succeeded", new string('\u4E00', 300), DateTimeOffset.UtcNow)).ToArray();
        var mounts = Enumerable.Range(0, 900).Select(_ => new HostMountStatus(Guid.NewGuid(), "Mounted", new string('\u4E00', 300))).ToArray();
        var response = HostProtocol.BoundStatus(new HostResponse(true, Mounts: mounts, SyncJobs: syncs));
        await using var stream = new MemoryStream();

        await HostProtocol.WriteAsync(stream, response, CancellationToken.None);
        Assert.True(stream.Length < 1024 * 1024);
        stream.Position = 0;
        var restored = await HostProtocol.ReadAsync<HostResponse>(stream, CancellationToken.None);

        Assert.Equal(1, restored!.ActiveSyncJobs);
        Assert.Equal("Running", restored.SyncJobs![0].Lifecycle);
        Assert.True(restored.SyncStatusTruncated);
        Assert.True(restored.MountStatusTruncated);
    }
    [Fact]
    public async Task StatusBoundsSharedUnicodeDetailsWhilePreservingCompleteCounts()
    {
        var path = new string('\u4E00', 4096);
        var statuses = Enumerable.Range(0, 3).Select(_ => new HostMountStatus(Guid.NewGuid(), "Mounted", "Uploading", 100, 3)
        {
            UploadsDirty = 200,
            Uploads = Enumerable.Range(0, 100).Select(index => new MountUploadFile { RelativePath = path + index }).ToArray()
        });
        var response = new HostResponse(true, Mounts: HostProtocol.BoundUploadDetails(statuses));
        await using var stream = new MemoryStream();

        await HostProtocol.WriteAsync(stream, response, CancellationToken.None);
        Assert.True(stream.Length < 1024 * 1024);
        stream.Position = 0;
        var restored = await HostProtocol.ReadAsync<HostResponse>(stream, CancellationToken.None);

        Assert.Equal(3, restored!.Mounts!.Count);
        Assert.All(restored.Mounts, status =>
        {
            Assert.True(status.UploadDetailsTruncated);
            Assert.Equal(100, status.UploadsQueued);
            Assert.Equal(3, status.UploadsInProgress);
            Assert.Equal(200, status.UploadsDirty);
        });
        Assert.NotEmpty(restored.Mounts[0].Uploads);
    }

    [Fact]
    public void AcceptsBaseDirectory_RequiresEquivalentExplicitPath()
    {
        var current = Path.Combine(Path.GetTempPath(), "rdrive-install");

        Assert.False(HostProtocol.AcceptsBaseDirectory(null, current));
        Assert.False(HostProtocol.AcceptsBaseDirectory("  ", current));
        Assert.True(HostProtocol.AcceptsBaseDirectory(current + Path.DirectorySeparatorChar, current));
        Assert.False(HostProtocol.AcceptsBaseDirectory(
            Path.Combine(Path.GetTempPath(), "other-rdrive-install"),
            current));
    }

    [Fact]
    public async Task Request_RoundTripsShutdownConfirmation()
    {
        var request = new HostRequest(
            "shutdown",
            Confirmed: true,
            ExpectedHostBaseDirectory: @"C:\Program Files\rdrive"
        );
        await using var stream = new MemoryStream();

        await HostProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var restored = await HostProtocol.ReadAsync<HostRequest>(stream, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("shutdown", restored.Command);
        Assert.True(restored.Confirmed);
        Assert.Equal(@"C:\Program Files\rdrive", restored.ExpectedHostBaseDirectory);
    }

    [Fact]
    public async Task SendAsync_RejectsNonPositiveTimeout()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            HostClient.SendAsync(new HostRequest("status"), TimeSpan.Zero));
    }

    [Fact]
    public void IsSameBaseDirectory_NormalizesCaseAndTrailingSeparators()
    {
        Assert.True(HostProtocol.IsSameBaseDirectory(
            @"C:\Program Files\rdrive\",
            @"c:\program files\RDRIVE"
        ));
    }
}

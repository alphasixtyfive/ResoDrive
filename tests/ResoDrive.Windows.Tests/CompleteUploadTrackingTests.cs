using System.Text.Json;
using ResoDrive.Core.Domain;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class CompleteUploadTrackingTests
{
    private static string Statistics(long queued = 0, long uploading = 0, long errors = 0, int mode = 3) => JsonSerializer.Serialize(new
    {
        fs = "remote:", opt = new { CacheMode = mode },
        diskCache = new { uploadsQueued = queued, uploadsInProgress = uploading, erroredFiles = errors, bytesUsed = 99, outOfSpace = false }
    });
    private static string Core(object[]? active = null, long errors = 0) => JsonSerializer.Serialize(new
    {
        bytes = 123, errors, fatalError = false, retryError = errors > 0, transferring = active ?? []
    });
    private static object Active(string name = "folder/file.txt", long bytes = 25, string? srcFs = null, string? dstFs = "remote:") =>
        new { name, bytes, size = 100, speedAvg = 10, eta = 7, srcFs, dstFs };
    private static object Queued(string name = "folder/file.txt", bool uploading = false, int tries = 0) =>
        new { name, size = 100, uploading, tries };
    private static string Queue(params object[] files) => JsonSerializer.Serialize(new { queue = files });
    private static DirtyCacheStatus Dirty(params string[] names) => new(null,
        names.Select(name => new MountUploadFile { RelativePath = name, State = MountUploadState.WaitingForClose, TotalBytes = 100 }).ToArray());

    [Fact]
    public void SynchronousUploadIsPendingEvenWhenVfsQueueIsZero()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core([Active()]), Dirty("folder/file.txt"));
        Assert.True(status.NeedsAttention);
        Assert.Equal(1, status.Uploading);
        Assert.Equal(0, status.Dirty);
        Assert.Equal(75, status.BytesRemaining);
        Assert.Equal(25, Assert.Single(status.Files).BytesTransferred);
    }

    [Fact]
    public void OpenDirtyFilesArePendingWhenAllControlCountersAreZero()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core(), Dirty("open.txt"));
        Assert.True(status.NeedsAttention);
        Assert.Equal(1, status.Dirty);
        Assert.Equal(MountUploadState.WaitingForClose, Assert.Single(status.Files).State);
    }

    [Fact]
    public void QueueCoreAndMetadataRepresentOneFileWithoutDoubleCounting()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(uploading: 1), Queue(Queued(uploading: true)),
            Core([Active()]), Dirty("folder/file.txt"));
        Assert.Single(status.Files);
        Assert.Equal(1, status.Uploading);
        Assert.Equal(0, status.Queued);
        Assert.Equal(0, status.Dirty);
    }

    [Fact]
    public void RetryAfterAnErrorRemainsPendingButHistoricalCoreErrorsDoNotBlockRecovery()
    {
        var failed = VfsTransferStatus.ParseComplete(Statistics(), Queue(Queued(tries: 2)), Core(errors: 4), Dirty());
        Assert.True(failed.NeedsAttention);
        Assert.Equal(1, failed.Errors);
        Assert.Equal(MountUploadState.Retrying, Assert.Single(failed.Files).State);
        var recovered = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core(errors: 4), Dirty());
        Assert.False(recovered.NeedsAttention);
    }

    [Fact]
    public void HydrationDownloadsDoNotPretendToBeUploads()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core([Active(srcFs: "remote:", dstFs: null)]), Dirty());
        Assert.False(status.NeedsAttention);
        Assert.Empty(status.Files);
    }

    [Fact]
    public void UnknownTransferDirectionRemainsConservativelyPending()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core([Active(dstFs: null)]), Dirty());
        Assert.True(status.NeedsAttention);
    }

    [Fact]
    public void DirtyUploadIdentityRemainsCaseSensitiveWhenFilteringHydration()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(),
            Core([Active("File.txt", srcFs: "remote:", dstFs: null), Active("file.txt", srcFs: "remote:", dstFs: null)]),
            Dirty("File.txt"));
        Assert.Equal(1, status.Uploading);
        Assert.Equal("File.txt", Assert.Single(status.Files).RelativePath);
    }

    [Fact]
    public void CacheOffCanBeObservedThroughCompleteControlResponses()
    {
        var idle = VfsTransferStatus.ParseComplete(Statistics(mode: 0), Queue(), Core(), Dirty());
        Assert.False(idle.NeedsAttention);
        var busy = VfsTransferStatus.ParseComplete(Statistics(mode: 0), Queue(), Core([Active()]), Dirty());
        Assert.True(busy.NeedsAttention);
    }

    [Theory]
    [InlineData("{}", "{\"queue\":[]}", "{}")]
    [InlineData("{\"fs\":\"remote:\",\"opt\":{\"CacheMode\":3}}", "{\"queue\":[]}", "{}")]
    public void IncompleteObservationsCannotBecomeSafe(string stats, string queue, string core) =>
        Assert.Throws<JsonException>(() => VfsTransferStatus.ParseComplete(stats, queue, core, Dirty()));

    [Fact]
    public void AggregateTransitionsRemainPendingWhenDetailsDisappearBetweenCalls()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(queued: 1), Queue(), Core(), Dirty());
        Assert.True(status.NeedsAttention);
        Assert.Null(status.BytesRemaining);
    }

    [Fact]
    public void FullBytesDoNotMeanServerAcceptedTheUpload()
    {
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core([Active(bytes: 100)]), Dirty());
        Assert.True(status.NeedsAttention);
        Assert.Equal(0, status.BytesRemaining);
    }

    [Fact]
    public void DetailLimitsKeepFullPendingCounts()
    {
        var names = Enumerable.Range(0, 150).Select(index => $"file-{index}.txt").ToArray();
        var status = VfsTransferStatus.ParseComplete(Statistics(), Queue(), Core(), Dirty(names));
        Assert.Equal(150, status.Dirty);
        Assert.Equal(VfsTransferStatus.MaximumVisibleFiles, status.Files.Count);
        Assert.True(status.DetailsTruncated);
    }

    [Fact]
    public void LostObservationBlocksEvenAfterAnEarlierIdleCheck()
    {
        Assert.True(new MountUploadObservation(null, null).BlocksStop);
        Assert.True(new MountUploadObservation(null, new(0, 0, 0, 0, false)).BlocksStop);
    }

    [Fact]
    public void NormalPendingUploadsAreNotLabeledCacheRecovery()
    {
        var snapshot = new MountSnapshot { MountId = MountId.New(), Lifecycle = MountLifecycle.Mounted };
        var result = snapshot.WithTransfers(new(new(1, 0, 0, 0, false), null));
        Assert.False(result.UploadRecoveryRequired);
        Assert.Equal(1, result.UploadsQueued);
    }

    [Fact]
    public void WatcherInvalidationIsCheckingRatherThanAnEndpointFailure()
    {
        var snapshot = new MountSnapshot { MountId = MountId.New(), Lifecycle = MountLifecycle.Mounted };
        var result = snapshot.WithTransfers(new(null, new(1, 0, 0, 0, false), Checking: true));
        Assert.True(result.UploadStatusStale);
        Assert.True(result.UploadStatusChecking);
        Assert.Equal(1, result.UploadsQueued);
    }
}

using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class UploadPresentationTests
{
    [Fact]
    public void DirtyOpenFilesRemainVisibleAndBlockSafeExit()
    {
        var mount = Mount();
        var status = new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0)
        {
            UploadsDirty = 1,
            Uploads = [new MountUploadFile { RelativePath = "report.docx", State = MountUploadState.WaitingForClose }],
        };
        var row = new MountRow(mount, status);
        var model = new UploadsViewModel();
        model.Update([row], false);

        Assert.True(row.HasPendingUploads);
        Assert.Contains("waiting for close", row.UploadActivityText, StringComparison.Ordinal);
        Assert.Contains("close this file", Assert.Single(model.Files).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void HostLossInvalidatesPreviouslyCleanMountedStatus()
    {
        var row = new MountRow(Mount(), new HostMountStatus(Guid.NewGuid(), "Mounted", "Mounted", 0, 0));
        row.MarkHostUnavailable();
        Assert.True(row.UploadNeedsAttention);
        Assert.True(row.UploadStatus!.UploadStatusStale);
        Assert.Equal("Upload status unavailable", row.UploadActivityText);
    }

    [Fact]
    public void FullyTransferredFileWaitsForServerConfirmation()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 1)
        {
            Uploads = [new MountUploadFile { RelativePath = "report.docx", State = MountUploadState.Uploading, TotalBytes = 12, BytesTransferred = 12 }],
        });
        var model = new UploadsViewModel();
        model.Update([row], false);
        Assert.Equal("Waiting for server confirmation", Assert.Single(model.Files).Detail);
        Assert.DoesNotContain("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshCleanObservationRemovesStaleDetails()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", UploadStatusStale: true));
        var model = new UploadsViewModel();
        model.Update([row], true);
        Assert.Single(model.Files);
        row.ApplyStatus(new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0));
        model.Update([row], false);
        Assert.Empty(model.Files);
        Assert.Contains("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckingCacheChangesDoesNotClaimCleanOrReportConnectionFailure()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0, UploadStatusStale: true)
        {
            UploadStatusChecking = true,
        });
        var model = new UploadsViewModel();
        model.Update([row], false);

        Assert.Equal("Checking uploads…", row.UploadActivityText);
        Assert.Contains("Checking changed files", Assert.Single(model.Files).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseSensitiveServerPathsKeepSeparateProgressRows()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 2, 0)
        {
            Uploads =
            [
                new MountUploadFile { RelativePath = "report.docx", State = MountUploadState.Queued },
                new MountUploadFile { RelativePath = "Report.docx", State = MountUploadState.Queued },
            ],
        });
        var model = new UploadsViewModel();
        model.Update([row], false);

        Assert.Equal(2, model.Files.Count);
        Assert.All(model.Files, file => Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressVisibility));
    }

    [Fact]
    public void SyncAndNativeProtectionFailureRemainVisibleWithoutMountUploads()
    {
        var model = new UploadsViewModel();
        model.Update([], false, otherTransfersActive: true, powerProtectionUnavailable: true);

        Assert.Equal(2, model.Files.Count);
        Assert.Contains("pending", model.Summary, StringComparison.Ordinal);
        model.Update([], false, powerProtectionUnavailable: true);
        Assert.Single(model.Files);
        Assert.Contains("power protection needs attention", model.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DisconnectingDriveRemainsUnconfirmedUntilItsStopCompletes()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Stopping", "Stopping…", 0, 0));
        var model = new UploadsViewModel();
        model.Update([row], false);

        Assert.True(row.UploadNeedsAttention);
        Assert.Contains("connects or disconnects", Assert.Single(model.Files).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No active uploads", model.Summary, StringComparison.Ordinal);
        row.ApplyStatus(new HostMountStatus(mount.Id, "Stopped", "Stopped", 0, 0));
        model.Update([row], false);
        Assert.Empty(model.Files);
        Assert.Contains("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeQueuesKeepThePanelCompactAndPrioritizeActiveProgress()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 20, 1)
        {
            Uploads = [.. Enumerable.Range(0, 20).Select(index => new MountUploadFile { RelativePath = $"queued-{index}.txt" }),
                new MountUploadFile { RelativePath = "active.txt", State = MountUploadState.Uploading, TotalBytes = 100, BytesTransferred = 40 }]
        });
        var model = new UploadsViewModel();
        model.Update([row], false);

        Assert.Equal(6, model.Files.Count);
        Assert.Contains("active.txt", model.Files[0].Name, StringComparison.Ordinal);
        Assert.Equal(40, model.Files[0].Percent);
        Assert.Equal("More files pending", model.Files[^1].Detail);
        var retained = model.Files[1];
        row.ApplyStatus(row.UploadStatus! with
        {
            Uploads = [.. row.UploadStatus!.Uploads.Where(file => file.RelativePath != "active.txt").Select(file =>
                file.RelativePath == "queued-6.txt" ? file with { State = MountUploadState.Uploading, TotalBytes = 100, BytesTransferred = 50 } : file)]
        });
        model.Update([row], false);
        Assert.Contains("queued-6.txt", model.Files[0].Name, StringComparison.Ordinal);
        Assert.Equal(50, model.Files[0].Percent);
        Assert.Same(retained, model.Files[1]);
        Assert.Equal("More files pending", model.Files[^1].Detail);
    }

    [Fact]
    public void FileNamedStatusDoesNotReplaceTheDriveWarning()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 1, 0, UploadStatusStale: true)
        { Uploads = [new MountUploadFile { RelativePath = "status" }] });
        var model = new UploadsViewModel();
        model.Update([row], false);
        Assert.Equal(2, model.Files.Count);
        Assert.Contains(model.Files, file => file.Name.EndsWith("status", StringComparison.Ordinal));
        Assert.Contains(model.Files, file => file.Detail.Contains("unavailable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void UnavailableObservationsDoNotPresentOldProgressAsLive(bool stale, bool checking, bool hostUnavailable)
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 1)
        {
            Uploads = [new MountUploadFile
            {
                RelativePath = "report.docx", State = MountUploadState.Uploading,
                TotalBytes = 100, BytesTransferred = 40, SpeedBytesPerSecond = 16,
            }],
        });
        var model = new UploadsViewModel();
        model.Update([row], false);
        var file = Assert.Single(model.Files);
        Assert.Contains("/s", file.ProgressText, StringComparison.Ordinal);
        row.ApplyStatus(row.UploadStatus! with { UploadStatusStale = stale, UploadStatusChecking = checking });
        model.Update([row], hostUnavailable);

        Assert.Same(file, model.Files.Single(item => item.Key == file.Key));
        Assert.Equal("Waiting for upload status", file.Detail);
        Assert.DoesNotContain("/s", file.ProgressText, StringComparison.Ordinal);
        Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressTextVisibility);
        Assert.DoesNotContain("No active uploads", model.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void UnknownFileSizeShowsTransferredBytesWithoutAFixedZeroPercentBar(long? totalBytes)
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 1)
        {
            Uploads = [new MountUploadFile
            {
                RelativePath = "stream.bin", State = MountUploadState.Uploading,
                TotalBytes = totalBytes, BytesTransferred = 1024, SpeedBytesPerSecond = 256,
            }],
        });
        var model = new UploadsViewModel();
        model.Update([row], false);
        var file = Assert.Single(model.Files);

        Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, file.ProgressTextVisibility);
        Assert.Contains("/s", file.ProgressText, StringComparison.Ordinal);
        Assert.DoesNotContain("of", file.ProgressText, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeedWithoutAByteObservationDoesNotStartWithASeparator()
    {
        Assert.Equal("256 B/s", UploadPresentation.Progress(null, null, 256));
    }

    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud", Target = new() { DriveLetter = 'R' },
    };
}

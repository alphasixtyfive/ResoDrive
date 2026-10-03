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
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.True(row.HasPendingUploads);
        Assert.Contains("waiting for close", row.UploadActivityText, StringComparison.Ordinal);
        Assert.Contains("close this file", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No active transfers", model.Summary, StringComparison.Ordinal);
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
        var model = new TransfersViewModel();
        model.Update([row], false);
        Assert.Equal("Waiting for server confirmation", Assert.Single(model.Transfers).Detail);
        Assert.Empty(model.Summary);
    }

    [Theory]
    [InlineData(2, 0, 0, "2 queued")]
    [InlineData(0, 1, 0, "1 uploading")]
    [InlineData(0, 0, 1, "1 waiting for close")]
    public void PendingCountsWithoutFileDetailsStillShowActivity(int queued, int uploading, int dirty, string activity)
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", queued, uploading)
        {
            UploadsDirty = dirty,
        });
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Empty(model.Summary);
        var file = Assert.Single(model.Transfers);
        Assert.Equal("Cloud", file.Name);
        Assert.Equal(activity, file.Detail);
        Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressVisibility);

        row.ApplyStatus(new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0));
        model.Update([row], false);
        Assert.Empty(model.Transfers);
        Assert.Equal("No active transfers.", model.Summary);
    }

    [Theory]
    [InlineData(long.MaxValue, 1, 0)]
    [InlineData(long.MaxValue, long.MaxValue, 0)]
    [InlineData(long.MaxValue, 0, 1)]
    public void LargePendingCountsKeepTheOrdinaryActiveSummaryAbsent(long queued, long uploading, long dirty)
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", queued, uploading)
        {
            UploadsDirty = dirty,
        });
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Empty(model.Summary);
        Assert.Single(model.Transfers);
    }

    [Fact]
    public void PendingTrayCountCombinesDrivesAndCountsEachChangingDriveOnce()
    {
        var first = Mount();
        var second = Mount();
        var starting = Mount();
        var rows = new[]
        {
            new MountRow(first, new HostMountStatus(first.Id, "Mounted", "Mounted", 3, 2)
            {
                UploadsDirty = 4, UploadStatusChecking = true,
            }),
            new MountRow(second, new HostMountStatus(second.Id, "Stopping", "Stopping", 2, 0)),
            new MountRow(starting, new HostMountStatus(starting.Id, "Starting", "Starting")
            {
                UploadStatusChecking = true,
            }),
        };

        Assert.Equal(14, UploadPresentation.PendingCount(rows));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingTrayCountSaturatesWithinAndAcrossDrives(bool splitAcrossDrives)
    {
        var first = Mount();
        var second = Mount();
        var rows = new[]
        {
            new MountRow(first, new HostMountStatus(first.Id, "Mounted", "Mounted", long.MaxValue,
                splitAcrossDrives ? 0 : long.MaxValue)),
            new MountRow(second, new HostMountStatus(second.Id, "Mounted", "Mounted", 1, 0)
            {
                UploadStatusChecking = true,
            }),
        };

        Assert.Equal(long.MaxValue, UploadPresentation.PendingCount(rows));
    }

    [Fact]
    public void PendingTrayCountIgnoresNegativeAndUnknownCounts()
    {
        var first = Mount();
        var second = Mount();
        var rows = new[]
        {
            new MountRow(first, new HostMountStatus(first.Id, "Mounted", "Mounted", -4)
            {
                UploadsDirty = -8,
            }),
            new MountRow(second, new HostMountStatus(second.Id, "Mounted", "Mounted", 2, -1)
            {
                UploadsDirty = -3, UploadStatusChecking = true,
            }),
        };

        Assert.Equal(3, UploadPresentation.PendingCount(rows));
    }

    [Fact]
    public void FreshCleanObservationRemovesStaleDetails()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", UploadStatusStale: true));
        var model = new TransfersViewModel();
        model.Update([row], true);
        Assert.Single(model.Transfers);
        row.ApplyStatus(new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0));
        model.Update([row], false);
        Assert.Empty(model.Transfers);
        Assert.Contains("No active transfers", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckingCacheChangesDoesNotClaimCleanOrReportConnectionFailure()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0, UploadStatusStale: true)
        {
            UploadStatusChecking = true,
        });
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Equal("Checking uploads…", row.UploadActivityText);
        Assert.Contains("Checking changed files", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No active transfers", model.Summary, StringComparison.Ordinal);
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
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Equal(2, model.Transfers.Count);
        Assert.All(model.Transfers, file => Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressVisibility));
    }

    [Fact]
    public void SyncAndNativeProtectionFailureRemainVisibleWithoutMountUploads()
    {
        var mount = Mount();
        var job = new SyncJobSettings
        {
            Id = Guid.NewGuid(), DisplayName = "Documents", LocalPath = @"C:\Data", Mode = nameof(SyncMode.CopyToRemote),
        };
        var sync = new SyncRow(mount, job, new HostSyncStatus(mount.Id, job.Id, "Running", "Syncing", null));
        var model = new TransfersViewModel();
        model.Update([], false, [sync], 1);
        Assert.Single(model.Transfers);
        Assert.Empty(model.Summary);
        model.Update([], false, [sync], 1, powerProtectionUnavailable: true);

        Assert.Equal(2, model.Transfers.Count);
        Assert.Contains("power protection needs attention", model.Summary, StringComparison.Ordinal);
        model.Update([], false, powerProtectionUnavailable: true);
        Assert.Equal("Windows power protection", Assert.Single(model.Transfers).Name);
        Assert.Contains("power protection needs attention", model.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("No active transfers", model.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait for uploads", model.Transfers[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DisconnectingDriveRemainsUnconfirmedUntilItsStopCompletes()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Stopping", "Stopping…", 0, 0));
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.True(row.UploadNeedsAttention);
        Assert.Contains("connects or disconnects", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("No active transfers", model.Summary, StringComparison.Ordinal);
        row.ApplyStatus(new HostMountStatus(mount.Id, "Stopped", "Stopped", 0, 0));
        model.Update([row], false);
        Assert.Empty(model.Transfers);
        Assert.Contains("No active transfers", model.Summary, StringComparison.Ordinal);
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
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Equal(6, model.Transfers.Count);
        Assert.Contains("active.txt", model.Transfers[0].Name, StringComparison.Ordinal);
        Assert.Equal(40, model.Transfers[0].Percent);
        Assert.Equal("More files pending", model.Transfers[^1].Detail);
        var retained = model.Transfers[1];
        row.ApplyStatus(row.UploadStatus! with
        {
            Uploads = [.. row.UploadStatus!.Uploads.Where(file => file.RelativePath != "active.txt").Select(file =>
                file.RelativePath == "queued-6.txt" ? file with { State = MountUploadState.Uploading, TotalBytes = 100, BytesTransferred = 50 } : file)]
        });
        model.Update([row], false);
        Assert.Contains("queued-6.txt", model.Transfers[0].Name, StringComparison.Ordinal);
        Assert.Equal(50, model.Transfers[0].Percent);
        Assert.Same(retained, model.Transfers[1]);
        Assert.Equal("More files pending", model.Transfers[^1].Detail);
    }

    [Fact]
    public void RetryOutsideTheVisibleFilesDoesNotHideTheUploadError()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 5, 0)
        {
            UploadErrors = 1,
            Uploads = [.. Enumerable.Range(0, 5).Select(index => new MountUploadFile { RelativePath = $"queued-{index}.txt" }),
                new MountUploadFile { RelativePath = "retry.txt", State = MountUploadState.Retrying }],
        });
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Equal("Some transfers need attention.", model.Summary);
        Assert.Contains(model.Transfers, file => file.Detail.Contains("upload error", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Transfers, file => file.Name.Contains("retry.txt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "1 upload error")]
    [InlineData(2, "2 upload errors")]
    public void ErrorOnlyObservationsRetainTheirFaultSummaryAndCount(long count, string expected)
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0)
        {
            UploadErrors = count,
        });
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Equal("Some transfers need attention.", model.Summary);
        Assert.StartsWith(expected + ".", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.Equal(expected, UploadPresentation.Activity(row.UploadStatus));
    }

    [Fact]
    public void FileNamedStatusDoesNotReplaceTheDriveWarning()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 1, 0, UploadStatusStale: true)
        { Uploads = [new MountUploadFile { RelativePath = "status" }] });
        var model = new TransfersViewModel();
        model.Update([row], false);
        Assert.Equal(2, model.Transfers.Count);
        Assert.Contains(model.Transfers, file => file.Name.EndsWith("status", StringComparison.Ordinal));
        Assert.Contains(model.Transfers, file => file.Detail.Contains("unavailable", StringComparison.Ordinal));
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
        var model = new TransfersViewModel();
        model.Update([row], false);
        var file = Assert.Single(model.Transfers);
        Assert.Contains("/s", file.ProgressText, StringComparison.Ordinal);
        row.ApplyStatus(row.UploadStatus! with { UploadStatusStale = stale, UploadStatusChecking = checking });
        model.Update([row], hostUnavailable);

        Assert.Same(file, model.Transfers.Single(item => item.Key == file.Key));
        Assert.Equal("Waiting for upload status", file.Detail);
        Assert.DoesNotContain("/s", file.ProgressText, StringComparison.Ordinal);
        Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, file.ProgressTextVisibility);
        Assert.DoesNotContain("No active transfers", model.Summary, StringComparison.Ordinal);
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
        var model = new TransfersViewModel();
        model.Update([row], false);
        var file = Assert.Single(model.Transfers);

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

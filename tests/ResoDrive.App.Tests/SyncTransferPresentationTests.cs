using System.Windows;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class SyncTransferPresentationTests
{
    [Fact]
    public void UploadsAndDownloadsShowSeparateLiveProgressWithoutRepeatingMetrics()
    {
        var sync = Job(nameof(SyncMode.CopyFromRemote));
        sync.ApplyStatus(sync.TransferStatus! with
        {
            BytesTransferred = 2048, TotalBytes = 4096, ProgressPercent = 50,
            TransfersCompleted = 1, TotalTransfers = 3, SpeedBytesPerSecond = 1024, EtaSeconds = 12,
        });
        var mount = new MountSettings { Id = sync.MountId, DisplayName = "Cloud", RemoteName = "cloud" };
        var upload = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 1)
        {
            Uploads = [new MountUploadFile
            {
                RelativePath = "report.docx", State = MountUploadState.Uploading,
                BytesTransferred = 1024, TotalBytes = 4096,
            }],
        });
        var model = new TransfersViewModel();
        model.Update([upload], false, [sync], 1);

        Assert.Equal(2, model.Transfers.Count);
        Assert.Equal(25, model.Transfers[0].Percent);
        var download = SyncFile(model, sync);
        Assert.Equal("Cloud · Documents", download.Name);
        Assert.Contains("Copy from remote", download.Detail, StringComparison.Ordinal);
        Assert.Contains("1 of 3 files", download.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/s", download.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("files", download.ProgressText, StringComparison.Ordinal);
        Assert.Contains("2.00 KB of 4.00 KB", download.ProgressText, StringComparison.Ordinal);
        Assert.Contains("1.00 KB/s", download.ProgressText, StringComparison.Ordinal);
        Assert.Contains("12s left", download.ProgressText, StringComparison.Ordinal);
        Assert.Equal(50, download.Percent);
        Assert.Equal(Visibility.Visible, download.ProgressVisibility);
        Assert.Empty(model.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void UnknownTotalsShowTransferredBytesAndRateWithoutAFixedZeroBar(long? total)
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { BytesTransferred = 1024, TotalBytes = total, SpeedBytesPerSecond = 256 });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);

        var row = Assert.Single(model.Transfers);
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
        Assert.Equal(Visibility.Visible, row.ProgressTextVisibility);
        Assert.Contains("1.00 KB", row.ProgressText, StringComparison.Ordinal);
        Assert.Contains("256 B/s", row.ProgressText, StringComparison.Ordinal);
        Assert.DoesNotContain(" of ", row.ProgressText, StringComparison.Ordinal);
        Assert.Empty(model.Summary);
    }

    [Fact]
    public void PercentOnlyObservationsRemainDeterminateAndKeepRawPrecision()
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { ProgressPercent = 37.51 });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);
        var row = Assert.Single(model.Transfers);
        Assert.Equal(Visibility.Visible, row.ProgressVisibility);
        Assert.Equal(37.51, row.Percent);
        Assert.Equal(Visibility.Collapsed, row.ProgressTextVisibility);

        job.ApplyStatus(job.TransferStatus! with { ProgressPercent = 37.52 });
        model.Update([], false, [job], 1);
        Assert.Same(row, Assert.Single(model.Transfers));
        Assert.Equal(37.52, row.Percent);
    }

    [Fact]
    public void CheckingAndPreparingStagesDoNotInventTransferredBytes()
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { BytesTransferred = 0, TotalBytes = 0, ChecksCompleted = 6, TotalChecks = 12 });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);
        var row = Assert.Single(model.Transfers);
        Assert.Contains("6 of 12 checked", row.Detail, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, row.ProgressTextVisibility);

        job.ApplyStatus(job.TransferStatus! with { BytesTransferred = null, ChecksCompleted = null, TotalChecks = null });
        model.Update([], false, [job], 1);
        Assert.Same(row, Assert.Single(model.Transfers));
        Assert.Equal("Preparing files", row.ProgressText);
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
    }

    [Fact]
    public void QueuedJobsDoNotShowCountersFromAnEarlierRun()
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { Lifecycle = "Queued", BytesTransferred = 50, TotalBytes = 100, ProgressPercent = 50 });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);

        var row = Assert.Single(model.Transfers);
        Assert.Contains("Waiting to start", row.Detail, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, row.ProgressTextVisibility);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Idle")]
    public void CompletedOrIdleJobsLeaveThePopupAfterFreshConfirmation(string lifecycle)
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { BytesTransferred = 100, TotalBytes = 100, ProgressPercent = 100 });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);
        Assert.Contains("Syncing 100%", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.Empty(model.Summary);

        job.ApplyStatus(job.TransferStatus! with { Lifecycle = lifecycle, CompletedAt = DateTimeOffset.UtcNow });
        model.Update([], false, [job], 0);
        Assert.Empty(model.Transfers);
        Assert.Equal("No active transfers.", model.Summary);
    }

    [Fact]
    public void HostLossRetainsIdentityButSuppressesStaleProgressUntilAValidObservation()
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { BytesTransferred = 50, TotalBytes = 100, ProgressPercent = 50, SpeedBytesPerSecond = 64, EtaSeconds = 10 });
        var current = job.TransferStatus;
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);
        var row = Assert.Single(model.Transfers);
        job.MarkStatusUnavailable();
        model.Update([], true, [job], 1, syncStatusAvailable: false);

        Assert.Same(row, Assert.Single(model.Transfers));
        Assert.Contains("unavailable", row.Detail, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, row.ProgressTextVisibility);
        Assert.Empty(row.ProgressText);
        Assert.Equal("Checking transfers…", model.Summary);

        job.ApplyStatus(current);
        model.Update([], false, [job], 1);
        Assert.Same(row, Assert.Single(model.Transfers));
        Assert.Equal(Visibility.Visible, row.ProgressVisibility);
        Assert.Contains("/s", row.ProgressText, StringComparison.Ordinal);
        Assert.Empty(model.Summary);
    }

    [Fact]
    public void MountOnlyTruncationKeepsFreshSyncProgressVisible()
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { BytesTransferred = 50, TotalBytes = 100, ProgressPercent = 50 });
        var model = new TransfersViewModel();
        model.Update([], true, [job], 1, syncStatusAvailable: true);

        Assert.Equal(Visibility.Visible, Assert.Single(model.Transfers).ProgressVisibility);
        Assert.Equal("Checking transfers…", model.Summary);
    }

    [Fact]
    public void TruncatedTerminalHistoryDoesNotRetainACompletedBusyRow()
    {
        var old = Job();
        var current = Job(name: "Pictures");
        old.MarkStatusUnavailable();
        var model = new TransfersViewModel();
        model.Update([], false, [old, current], 1, syncStatusAvailable: true);

        Assert.Equal(current.Name, Assert.Single(model.Transfers).Name.Split(" · ")[1]);
        Assert.Empty(model.Summary);
        current.MarkStatusUnavailable();
        model.Update([], false, [old, current], 0, syncStatusAvailable: true);
        Assert.Empty(model.Transfers);
        Assert.Equal("No active transfers.", model.Summary);
    }

    [Fact]
    public void CountOnlyActiveWorkUsesOneFallbackInsteadOfOmittedIdleRows()
    {
        var current = Job();
        var omitted = Job(name: "Omitted");
        omitted.ApplyStatus(null);
        omitted.MarkStatusUnavailable();
        var model = new TransfersViewModel();
        model.Update([], false, [current, omitted], 3, syncStatusAvailable: true);

        Assert.Equal(2, model.Transfers.Count);
        var fallback = Assert.Single(model.Transfers, row => row.Key == "sync:unrepresented");
        Assert.Contains("2 active sync jobs", fallback.Detail, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, fallback.ProgressVisibility);
        Assert.Equal("Checking transfers…", model.Summary);
        Assert.DoesNotContain(model.Transfers, row => row.Name.Contains(omitted.Name, StringComparison.Ordinal));

        model.Update([], false, activeSyncJobs: 1);
        Assert.Contains("1 active sync job", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        model.Update([], false);
        Assert.Empty(model.Transfers);
        Assert.Equal("No active transfers.", model.Summary);
    }

    [Fact]
    public void UnavailableActiveSnapshotsRemainConservativeEvenWithNoHostCount()
    {
        var job = Job();
        job.MarkStatusUnavailable();
        var model = new TransfersViewModel();
        model.Update([], true, [job], 0, syncStatusAvailable: false);

        Assert.Single(model.Transfers);
        Assert.Equal("Checking transfers…", model.Summary);
    }

    [Fact]
    public void DuplicateNamesAcrossDrivesKeepDistinctStableRows()
    {
        var first = Job();
        var second = Job();
        var model = new TransfersViewModel();
        model.Update([], false, [first, second], 2);
        var rows = model.Transfers.ToArray();
        Assert.Equal(2, rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count());
        model.Update([], false, [second, first], 2);
        Assert.Same(rows[1], model.Transfers[0]);
        Assert.Same(rows[0], model.Transfers[1]);
    }

    [Theory]
    [InlineData(false, "CopyFromRemote", "Copy from remote")]
    [InlineData(true, "invalid", "Sync")]
    public void RunningWorkIsNotHiddenByEditedJobSettings(bool enabled, string mode, string detail)
    {
        var job = Job(mode, enabled: enabled);
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);

        Assert.StartsWith(detail + " · Syncing", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.Empty(model.Summary);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void InvalidNumericProgressDoesNotReachTheBarOrDisplay(double invalid)
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with
        {
            BytesTransferred = -1, TotalBytes = 100, ProgressPercent = invalid,
            ChecksCompleted = -1, TotalChecks = -1, Errors = -1,
            SpeedBytesPerSecond = invalid, EtaSeconds = invalid,
        });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);

        var row = Assert.Single(model.Transfers);
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
        Assert.True(double.IsFinite(row.Percent));
        Assert.Equal("Preparing files", row.ProgressText);
        Assert.DoesNotContain("NaN", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", row.Detail, StringComparison.Ordinal);
        Assert.Empty(model.Summary);
    }

    [Fact]
    public void RunningErrorsKeepAttentionVisibleWithoutAddingCompletedHistory()
    {
        var job = Job();
        job.ApplyStatus(job.TransferStatus! with { Errors = 1 });
        var model = new TransfersViewModel();
        model.Update([], false, [job], 1);

        Assert.Contains("1 transfer error", Assert.Single(model.Transfers).Detail, StringComparison.Ordinal);
        Assert.Equal("Some transfers need attention.", model.Summary);
    }

    private static TransferRow SyncFile(TransfersViewModel model, SyncRow job) =>
        Assert.Single(model.Transfers, row => row.Key == $"sync:{job.MountId}:{job.Id}");

    private static SyncRow Job(string mode = "CopyToRemote", string name = "Documents", bool enabled = true)
    {
        var settings = new SyncJobSettings
        {
            Id = Guid.NewGuid(), DisplayName = name, LocalPath = @"C:\Data", RemotePath = "documents", Mode = mode, Enabled = enabled,
        };
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud", SyncJobs = [settings] };
        return new SyncRow(mount, settings, new HostSyncStatus(mount.Id, settings.Id, "Running", "Syncing", null));
    }
}

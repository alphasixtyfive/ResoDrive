using System.Windows;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class SyncProgressPresentationTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void MainRowAndFlyoutOmitMalformedProgressWithoutChangingTheRawObservation(double invalid)
    {
        var (mount, job) = Settings();
        var status = new HostSyncStatus(mount.Id, job.Id, "Running", "Syncing", null,
            BytesTransferred: -1, TotalBytes: 100, ProgressPercent: invalid,
            ChecksCompleted: -1, TotalChecks: 5, TransfersCompleted: -1, TotalTransfers: 5,
            Errors: -1, SpeedBytesPerSecond: invalid, EtaSeconds: invalid, ElapsedSeconds: invalid);
        var row = new SyncRow(mount, job, status);
        var model = new TransfersViewModel();
        model.Update([], false, [row], 1);
        var transfer = Assert.Single(model.Transfers);

        Assert.Same(status, row.TransferStatus);
        Assert.Equal("Syncing", row.StatusPrimary);
        Assert.Equal("0 of 5 files", row.StatusSecondary);
        Assert.Contains("0 of 5 files", transfer.Detail, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, transfer.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, transfer.ProgressTextVisibility);
        Assert.True(double.IsFinite(transfer.Percent));
        Assert.Empty(model.Summary);

        var recovered = status with
        {
            BytesTransferred = 4096, TotalBytes = 8192, ProgressPercent = 50.125,
            TransfersCompleted = 1, SpeedBytesPerSecond = 1024, EtaSeconds = 12,
        };
        row.ApplyStatus(recovered);
        model.Update([], false, [row], 1);
        Assert.Same(recovered, row.TransferStatus);
        Assert.Same(transfer, Assert.Single(model.Transfers));
        Assert.Equal("Syncing 50%", row.StatusPrimary);
        Assert.Equal(50.125, transfer.Percent);
        Assert.Contains("1.00 KB/s", row.StatusSecondary, StringComparison.Ordinal);
        Assert.Contains("1.00 KB/s", transfer.ProgressText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(double.MaxValue)]
    public void InvalidCompletedDurationsCannotCrashTheMainSyncRow(double invalid)
    {
        var (mount, job) = Settings();
        var status = new HostSyncStatus(mount.Id, job.Id, "Succeeded", "Completed", DateTimeOffset.Now,
            BytesTransferred: -1, ChecksCompleted: -1, TransfersCompleted: -1, ElapsedSeconds: invalid);
        var row = new SyncRow(mount, job, status);

        Assert.Same(status, row.TransferStatus);
        Assert.Equal("Completed", row.StatusPrimary);
        Assert.DoesNotContain("NaN", row.StatusSecondary, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", row.StatusSecondary, StringComparison.Ordinal);
        Assert.DoesNotContain("transferred", row.StatusSecondary, StringComparison.Ordinal);
        Assert.DoesNotContain(" in ", row.StatusSecondary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void UnknownTotalsKeepMeasuredBytesWithoutInventingATotalOrPercent(long total)
    {
        var (mount, job) = Settings();
        var status = new HostSyncStatus(mount.Id, job.Id, "Running", "Syncing", null,
            BytesTransferred: 1024, TotalBytes: total, SpeedBytesPerSecond: 256);
        var row = new SyncRow(mount, job, status);
        var model = new TransfersViewModel();
        model.Update([], false, [row], 1);
        var transfer = Assert.Single(model.Transfers);

        Assert.Same(status, row.TransferStatus);
        Assert.Equal("Syncing", row.StatusPrimary);
        Assert.Equal("1.00 KB · 256 B/s", row.StatusSecondary);
        Assert.Equal(row.StatusSecondary, transfer.ProgressText);
        Assert.Equal(Visibility.Collapsed, transfer.ProgressVisibility);
    }

    private static (MountSettings Mount, SyncJobSettings Job) Settings() =>
        (new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "fixture" },
            new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Backup", LocalPath = @"C:\Demo\Backup", Mode = nameof(SyncMode.CopyToRemote) });
}

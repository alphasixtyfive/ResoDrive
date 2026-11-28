using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class SyncStatusPresentationTests
{
    [Theory]
    [InlineData("future-state")]
    [InlineData("999")]
    [InlineData("-1")]
    public void UnknownLifecycleWaitsForFreshStatusWithoutOfferingADuplicateRun(string lifecycle)
    {
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "fixture" };
        var job = new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Backup", LocalPath = @"C:\Demo\Backup", Mode = nameof(SyncMode.CopyToRemote) };
        var row = new SyncRow(mount, job, null);
        Assert.True(row.CanAct);
        Assert.Equal("Run", row.ActionText);
        var unknown = new HostSyncStatus(mount.Id, job.Id, lifecycle, "Unknown state", null,
            BytesTransferred: 40, TotalBytes: 100, ProgressPercent: 40);
        row.ApplyStatus(unknown);
        var model = new TransfersViewModel();
        model.Update([], false, [row], 1);
        var transfer = Assert.Single(model.Transfers);

        Assert.Same(unknown, row.TransferStatus);
        Assert.Equal("Unknown state", row.StatusPrimary);
        Assert.False(row.IsBusy);
        Assert.False(row.CanAct);
        Assert.Equal("Waiting…", row.ActionText);
        Assert.Equal(System.Windows.Visibility.Collapsed, transfer.ProgressVisibility);
        Assert.Equal("Transfer status unavailable.", model.Summary);

        var recovered = unknown with { Lifecycle = nameof(SyncLifecycle.Running), ProgressPercent = 40.125 };
        row.ApplyStatus(recovered);
        model.Update([], false, [row], 1);
        Assert.Same(recovered, row.TransferStatus);
        Assert.True(row.IsBusy);
        Assert.True(row.CanAct);
        Assert.Equal("Stop", row.ActionText);
        Assert.Same(transfer, Assert.Single(model.Transfers));
        Assert.Equal(40.125, transfer.Percent);
    }

    [Fact]
    public void RawProgressUpdatesEvenWhenRoundedDisplayTextIsUnchanged()
    {
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud" };
        var job = new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Backup", LocalPath = @"C:\Demo\Backup", Mode = nameof(SyncMode.CopyToRemote) };
        var status = new HostSyncStatus(mount.Id, job.Id, nameof(SyncLifecycle.Running), "Syncing", null,
            BytesTransferred: 1048576, TotalBytes: 2097152, ProgressPercent: 50);
        var row = new SyncRow(mount, job, status);
        var display = row.StatusLine;
        var changes = new List<string?>();
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var next = status with { BytesTransferred = status.BytesTransferred + 1 };

        row.ApplyStatus(next);

        Assert.Equal(display, row.StatusLine);
        Assert.Same(next, row.TransferStatus);
        Assert.Contains(nameof(SyncRow.TransferStatus), changes);
    }

    [Fact]
    public void InterruptedJobRetainsItsSnapshotAndRecoversItsAvailability()
    {
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud" };
        var job = new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Backup", LocalPath = @"C:\Demo\Backup", Mode = nameof(SyncMode.CopyToRemote) };
        var status = new HostSyncStatus(mount.Id, job.Id, nameof(SyncLifecycle.Running), "Syncing", null,
            BytesTransferred: 10, TotalBytes: 100, ProgressPercent: 10);
        var row = new SyncRow(mount, job, status);

        row.MarkStatusUnavailable();

        Assert.True(row.StatusUnavailable);
        Assert.True(row.IsBusy);
        Assert.Same(status, row.TransferStatus);
        Assert.Empty(row.StatusSecondary);

        row.ApplyStatus(status);

        Assert.False(row.StatusUnavailable);
        Assert.True(row.IsBusy);
        Assert.Same(status, row.TransferStatus);
        Assert.Equal("Syncing 10%", row.StatusPrimary);
    }

    [Fact]
    public void SyncCountsRequireAnActualSuccessfulStatusSnapshot()
    {
        Assert.False(HostStatusPresentation.HasUsableSyncStatus(new HostResponse(true)));
        Assert.False(HostStatusPresentation.HasUsableSyncStatus(new HostResponse(false, SyncJobs: [])));
        Assert.False(HostStatusPresentation.HasUsableSyncStatus(new HostResponse(true, SyncJobs: [],
            InitializationErrorCode: "host.initialization_failed")));
        Assert.True(HostStatusPresentation.HasUsableSyncStatus(new HostResponse(true, SyncJobs: [],
            SyncStatusTruncated: true)));
    }

    [Fact]
    public void RunningStatus_UsesStructuredCountersWithoutMixingInTheRoute()
    {
        var status = new HostSyncStatus(
            Guid.NewGuid(),
            Guid.NewGuid(),
            nameof(SyncLifecycle.Running),
            "Syncing",
            null,
            BytesTransferred: 524288,
            TotalBytes: 1048576,
            ProgressPercent: 50,
            TransfersCompleted: 3,
            TotalTransfers: 8,
            SpeedBytesPerSecond: 262144,
            EtaSeconds: 12);

        var result = SyncStatusPresentation.Create(
            SyncMode.CopyToRemote,
            enabled: true,
            busy: true,
            SyncLifecycle.Running,
            status,
            recognized: true);

        Assert.Equal("Syncing 50%", result.Primary);
        Assert.Equal("3 of 8 files · 512 KB of 1.00 MB · 256 KB/s · 12s left", result.Secondary);
    }

    [Fact]
    public void CompletedNoChangeStatus_IsConcise()
    {
        var status = new HostSyncStatus(
            Guid.NewGuid(),
            Guid.NewGuid(),
            nameof(SyncLifecycle.Succeeded),
            "No changes",
            DateTimeOffset.Now,
            ChecksCompleted: 60,
            TotalChecks: 60);

        var result = SyncStatusPresentation.Create(
            SyncMode.CopyFromRemote,
            enabled: true,
            busy: false,
            SyncLifecycle.Succeeded,
            status,
            recognized: true);

        Assert.Equal("Up to date", result.Primary);
        Assert.Contains("60 checked", result.Secondary, StringComparison.Ordinal);
        Assert.Contains("Today", result.Secondary, StringComparison.Ordinal);
    }
}

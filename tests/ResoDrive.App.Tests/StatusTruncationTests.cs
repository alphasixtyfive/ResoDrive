using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class StatusTruncationTests
{
    [Fact]
    public void MissingRowsInPartialStatusRetainPreviouslyActiveWork()
    {
        var job = new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Documents", LocalPath = @"C:\Data", RemotePath = "documents", Mode = nameof(SyncMode.CopyToRemote) };
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud", Target = new() { DriveLetter = 'R' }, SyncJobs = [job] };
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0)],
            [new HostSyncStatus(mount.Id, job.Id, "Running", "Running", null)]);
        model.ApplyStatus([], statusTruncated: true);
        model.ApplySyncStatus([], statusTruncated: true);
        Assert.True(Assert.Single(model.Mounts).ShouldStop);
        Assert.True(model.Mounts[0].UploadStatus!.UploadStatusStale);
        Assert.True(Assert.Single(model.Jobs).IsBusy);
        Assert.Equal("Stop", model.Jobs[0].ActionText);
        Assert.Contains("unavailable", model.Jobs[0].StatusLine, StringComparison.Ordinal);
        model.ApplySyncStatus([new HostSyncStatus(mount.Id, job.Id, "Succeeded", "Completed", DateTimeOffset.UtcNow)]);
        Assert.False(model.Jobs[0].IsBusy);
        Assert.Equal("Run", model.Jobs[0].ActionText);
    }

    [Fact]
    public void NewlyOmittedSyncCannotBePresentedAsReadyToRun()
    {
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud", SyncJobs =
            [new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Documents", LocalPath = @"C:\Data", RemotePath = "documents", Mode = nameof(SyncMode.CopyToRemote) }] };
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [], syncStatusTruncated: true);
        Assert.False(Assert.Single(model.Jobs).CanAct);
        Assert.Equal("Waiting…", model.Jobs[0].ActionText);
    }

    [Fact]
    public void DisconnectedHostMarksSyncStatusUnavailableUntilFreshStatusArrives()
    {
        var job = new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Documents", LocalPath = @"C:\Data", RemotePath = "documents", Mode = nameof(SyncMode.CopyToRemote) };
        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud", SyncJobs = [job] };
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, null, hostUnavailable: true);
        Assert.False(Assert.Single(model.Jobs).CanAct);
        Assert.Contains("unavailable", model.Jobs[0].StatusLine, StringComparison.Ordinal);

        model.ApplySyncStatus([new HostSyncStatus(mount.Id, job.Id, "Running", "Syncing", null)]);
        model.ApplyHostUnavailable();
        Assert.True(model.Jobs[0].IsBusy);
        Assert.Contains("unavailable", model.Jobs[0].StatusLine, StringComparison.Ordinal);

        model.ApplySyncStatus([new HostSyncStatus(mount.Id, job.Id, "Succeeded", "Completed", DateTimeOffset.UtcNow)]);
        Assert.False(model.Jobs[0].IsBusy);
        Assert.True(model.Jobs[0].CanAct);
        Assert.DoesNotContain("unavailable", model.Jobs[0].StatusLine, StringComparison.Ordinal);
    }
}

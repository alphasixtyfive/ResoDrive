using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class ShellViewModelNotificationTests
{
    [Theory]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("WaitingToRestart")]
    public void RepeatedTransientObservationsDoNotRefreshAnUnchangedSummary(string lifecycle)
    {
        var mount = new MountSettings
        {
            Id = Guid.NewGuid(), DisplayName = "Cloud files", RemoteName = "cloud",
            Target = new MountTargetSettings { DriveLetter = 'R' },
        };
        var model = new ShellViewModel();
        var status = new HostMountStatus(mount.Id, lifecycle, lifecycle);
        model.Load(new ManagerSettings { Mounts = [mount] }, [status]);
        Assert.Equal("1 drive · 0 mounted · 1 in progress", model.MountSummary);
        var notifications = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.MountSummary)) notifications++;
        };

        for (var poll = 0; poll < 100; poll++)
            model.ApplyStatus([status with { UploadObservedAt = DateTimeOffset.UtcNow }]);

        Assert.Equal(0, notifications);
        model.ApplyStatus([status with { Lifecycle = "Mounted", Status = "Mounted" }]);
        Assert.Equal("1 drive · 1 mounted", model.MountSummary);
        Assert.Equal(1, notifications);
        model.Refresh();
        Assert.Equal(1, notifications);
    }
}

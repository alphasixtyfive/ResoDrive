using System.Windows;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class AutoMountStartupPresentationTests
{
    [Fact]
    public void MissingStartupSnapshotDoesNotOfferDuplicateManualMount()
    {
        var automatic = Mount("OnApplicationStart");
        var manual = Mount("Never");
        var model = new ShellViewModel();

        model.Load(new ManagerSettings { Mounts = [automatic, manual] }, []);

        var autoRow = Assert.Single(model.Mounts, row => row.Id == automatic.Id);
        Assert.Equal("Starting…", autoRow.ActionText);
        Assert.Equal("Preparing automatic mount", autoRow.StatusText);
        Assert.Equal(Visibility.Visible, autoRow.StatusVisibility);
        Assert.False(autoRow.CanAct);
        Assert.Contains("1 in progress", model.MountSummary, StringComparison.Ordinal);

        var manualRow = Assert.Single(model.Mounts, row => row.Id == manual.Id);
        Assert.Equal("Waiting…", manualRow.ActionText);
        Assert.False(manualRow.CanAct);

        model.ApplyStatus([new HostMountStatus(automatic.Id, "Starting", "Mount queued"),
            new HostMountStatus(manual.Id, "Stopped", "Not mounted")]);
        Assert.Equal("Starting…", autoRow.ActionText);
        Assert.False(autoRow.CanAct);
        Assert.Equal("Mount", manualRow.ActionText);
        Assert.True(manualRow.CanAct);

        model.ApplyStatus([new HostMountStatus(automatic.Id, "Mounted", "Mounted"),
            new HostMountStatus(manual.Id, "Stopped", "Not mounted")]);
        Assert.Equal("Unmount", autoRow.ActionText);
        Assert.True(autoRow.CanAct);
        Assert.Equal("2 drives · 1 mounted", model.MountSummary);
    }

    [Fact]
    public void StartupFailureOffersHostRetryUntilARealMountStatusArrives()
    {
        var mount = Mount("OnApplicationStart");
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [], hostUnavailable: true);
        var row = Assert.Single(model.Mounts);

        Assert.Equal("Retry", row.ActionText);
        Assert.Equal("Background host unavailable", row.StatusText);
        Assert.True(row.CanAct);
        Assert.True(row.NeedsHostRecovery);

        model.ApplyStatus([]);
        Assert.Equal("Starting…", row.ActionText);
        Assert.False(row.CanAct);

        model.ApplyHostUnavailable();
        Assert.Equal("Retry", row.ActionText);
        Assert.True(row.NeedsHostRecovery);

        model.ApplyStatus([new HostMountStatus(mount.Id, "Failed", "Mount failed")]);
        Assert.Equal("Retry", row.ActionText);
        Assert.True(row.CanAct);
        Assert.False(row.NeedsHostRecovery);
    }

    private static MountSettings Mount(string autoMount) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Cloud files",
        RemoteName = "cloud",
        Target = new MountTargetSettings { DriveLetter = 'R' },
        AutoMount = autoMount,
    };
}

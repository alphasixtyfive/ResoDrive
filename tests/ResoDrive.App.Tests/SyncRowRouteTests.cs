using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;

namespace ResoDrive.App.Tests;

public sealed class SyncRowRouteTests
{
    [Fact]
    public void Route_ShowsConnectionRootPathInsteadOfMountedDriveFolder()
    {
        var mount = new MountSettings
        {
            Id = Guid.NewGuid(),
            DisplayName = "Unimor Armeria",
            RemoteName = "Unimor",
            RemotePath = "/Armeria"
        };
        var job = new SyncJobSettings
        {
            Id = Guid.NewGuid(),
            DisplayName = "Reference",
            RemotePath = "/Fleet Reference",
            LocalPath = @"C:\Reference",
            Mode = nameof(SyncMode.CopyFromRemote)
        };

        var row = new SyncRow(mount, job, null);

        Assert.Equal("Unimor · /Fleet Reference  →  C:\\Reference", row.Route);
        Assert.Equal("Unimor Armeria", row.MountName);
    }
}

using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class RemoteWipeStatusPresentationTests
{
    [Fact]
    public void EnrollmentChangesDoNotAppearInDriveDetailsOrRefreshTheCard()
    {
        var mount = Mount();
        var status = new HostMountStatus(mount.Id, "Stopped", "Not mounted");
        var row = new MountRow(mount, status);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        foreach (var text in new[]
        {
            "Remote wipe setup pending — connect to Nextcloud",
            "Remote wipe configured",
            "Remote wipe needs attention — sign in with an app password",
        })
        {
            changed.Clear();
            row.ApplyStatus(status with { RemoteWipeStatus = text });

            Assert.DoesNotContain("Remote wipe", row.DetailText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(changed);
            Assert.Equal("Not mounted", row.StatusText);
            Assert.Equal("Mount", row.ActionText);
        }
    }

    [Fact]
    public async Task AnOlderHostWithoutEnrollmentStatusStillUpdatesTheDrive()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(
            mount.Id, "Mounted", "Mounted", RemoteWipeStatus: "Remote wipe configured"));
        await using var message = new MemoryStream();
        await HostProtocol.WriteAsync(message,
            new { mountId = mount.Id, lifecycle = "Stopped", status = "Not mounted" },
            CancellationToken.None);
        message.Position = 0;
        var oldStatus = await HostProtocol.ReadAsync<HostMountStatus>(message, CancellationToken.None);

        row.ApplyStatus(oldStatus);

        Assert.NotNull(oldStatus);
        Assert.Null(oldStatus.RemoteWipeStatus);
        Assert.DoesNotContain("Remote wipe", row.DetailText, StringComparison.Ordinal);
        Assert.False(row.IsMounted);
        Assert.Equal("Not mounted", row.StatusText);
    }

    [Fact]
    public void EnrollmentDoesNotAppearWhenLoadingOrRefreshingActiveTransfers()
    {
        var mount = Mount();
        var status = new HostMountStatus(mount.Id, "Mounted", "Mounted",
            UploadsQueued: 2, UploadsInProgress: 1, RemoteWipeStatus: "Remote wipe configured");
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [status]);
        var row = Assert.Single(model.Mounts);
        var uploads = row.UploadActivityText;

        Assert.DoesNotContain("Remote wipe configured", row.DetailText, StringComparison.Ordinal);
        Assert.True(row.IsMounted);
        Assert.Equal("Unmount", row.ActionText);
        Assert.Equal(uploads, row.UploadActivityText);

        model.ApplyStatus([status]);

        Assert.DoesNotContain("Remote wipe", row.DetailText, StringComparison.Ordinal);
        Assert.Equal(uploads, row.UploadActivityText);
    }

    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Cloud files",
        RemoteName = "cloud",
        ConnectionHost = "cloud.example.test",
        Target = new MountTargetSettings { DriveLetter = 'R' },
    };
}

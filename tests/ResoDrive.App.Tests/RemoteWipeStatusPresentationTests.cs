using System.Windows;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class RemoteWipeStatusPresentationTests
{
    [Fact]
    public void EnrollmentChangesUpdateAnIdleDriveWithoutAChangeToItsMountState()
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

            Assert.Equal(text, row.RemoteWipeStatus);
            Assert.Equal(Visibility.Visible, row.RemoteWipeStatusVisibility);
            Assert.Contains(text, row.DetailText, StringComparison.Ordinal);
            Assert.Contains(nameof(MountRow.RemoteWipeStatus), changed);
            Assert.Contains(nameof(MountRow.DetailText), changed);
            Assert.Equal("Not mounted", row.StatusText);
            Assert.Equal("Mount", row.ActionText);
        }
    }

    [Fact]
    public async Task AnOlderHostWithoutEnrollmentStatusClearsThePreviousLabel()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(
            mount.Id, "Mounted", "Mounted", RemoteWipeStatus: "Remote wipe configured"));
        await using var message = new MemoryStream();
        await HostProtocol.WriteAsync(message,
            new { mountId = mount.Id, lifecycle = "Mounted", status = "Mounted" },
            CancellationToken.None);
        message.Position = 0;
        var oldStatus = await HostProtocol.ReadAsync<HostMountStatus>(message, CancellationToken.None);

        row.ApplyStatus(oldStatus);

        Assert.NotNull(oldStatus);
        Assert.Null(oldStatus.RemoteWipeStatus);
        Assert.Empty(row.RemoteWipeStatus);
        Assert.Equal(Visibility.Collapsed, row.RemoteWipeStatusVisibility);
        Assert.DoesNotContain("Remote wipe", row.DetailText, StringComparison.Ordinal);
        Assert.True(row.IsMounted);
    }

    [Fact]
    public void InterruptedHostHidesEnrollmentWithoutDiscardingTransfersAndRefreshRestoresIt()
    {
        var mount = Mount();
        var status = new HostMountStatus(mount.Id, "Mounted", "Mounted",
            UploadsQueued: 2, UploadsInProgress: 1, RemoteWipeStatus: "Remote wipe configured");
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [status]);
        var row = Assert.Single(model.Mounts);
        var uploads = row.UploadActivityText;

        model.ClearRemoteWipeStatuses();

        Assert.Equal(Visibility.Collapsed, row.RemoteWipeStatusVisibility);
        Assert.DoesNotContain("Remote wipe configured", row.DetailText, StringComparison.Ordinal);
        Assert.True(row.IsMounted);
        Assert.Equal("Unmount", row.ActionText);
        Assert.Equal(uploads, row.UploadActivityText);

        model.ApplyStatus([status]);

        Assert.Equal("Remote wipe configured", row.RemoteWipeStatus);
        Assert.Equal(Visibility.Visible, row.RemoteWipeStatusVisibility);
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

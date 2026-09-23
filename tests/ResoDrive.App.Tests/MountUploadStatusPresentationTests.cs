using System.Windows;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class MountUploadStatusPresentationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, 0L)]
    [InlineData(2L, 1L)]
    public void UnavailableUploadStatisticsStayVisibleUntilFreshStatisticsArrive(long? queued, long? active)
    {
        var mount = Mount();
        var status = new HostMountStatus(mount.Id, "Mounted", "Mounted",
            UploadsQueued: queued, UploadsInProgress: active);
        var row = new MountRow(mount, status);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.ApplyStatus(status with { UploadStatusStale = true });

        Assert.Equal("Upload status unavailable", row.UploadActivityText);
        Assert.Equal(Visibility.Visible, row.ConnectionDetailVisibility);
        Assert.Contains("Upload status unavailable", row.UploadActivitySuffix, StringComparison.Ordinal);
        Assert.Contains(nameof(MountRow.UploadActivityText), changed);
        Assert.Contains(nameof(MountRow.ConnectionDetailVisibility), changed);
        Assert.True(row.IsMounted);
        Assert.Equal("Unmount", row.ActionText);

        row.ApplyStatus(status with { UploadsQueued = 0, UploadsInProgress = 0 });

        Assert.Empty(row.UploadActivityText);
        Assert.Empty(row.UploadActivitySuffix);
        Assert.Equal(Visibility.Collapsed, row.ConnectionDetailVisibility);

        row.ApplyStatus(status with { UploadsQueued = 2, UploadsInProgress = 1 });

        Assert.Equal("↑ 1 uploading · 2 queued", row.UploadActivityText);
    }

    [Fact]
    public void StoppingTheDriveClearsTheUnavailableUploadWarning()
    {
        var mount = Mount();
        var status = new HostMountStatus(mount.Id, "Mounted", "Mounted", UploadStatusStale: true);
        var row = new MountRow(mount, status);

        row.ApplyStatus(status with { Lifecycle = "Stopped", Status = "Not mounted" });

        Assert.Empty(row.UploadActivityText);
        Assert.Equal(Visibility.Collapsed, row.ConnectionDetailVisibility);
        Assert.Equal("Mount", row.ActionText);
    }

    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Cloud files",
        RemoteName = "cloud",
        Target = new MountTargetSettings { DriveLetter = 'R' },
    };
}

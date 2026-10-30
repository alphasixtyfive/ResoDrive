using System.Windows;
using System.Windows.Media;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class ConnectionWaitingPresentationTests
{
    [Fact]
    public void WaitingForTheNetworkIsCalmStoppableAndDoesNotPretendToBeConnecting()
    {
        var mount = Mount();
        var row = new MountRow(mount, Waiting(mount));
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.True(row.IsWaitingForConnection);
        Assert.True(row.ShouldStop);
        Assert.True(row.CanAct);
        Assert.False(row.IsTransient);
        Assert.False(row.IsMounted);
        Assert.False(row.CanOpen);
        Assert.Equal("Stop", row.ActionText);
        Assert.Equal("Waiting for connection", row.StatusLine);
        Assert.Equal(ColorOf(StatusPalette.Muted), ColorOf(row.StatusLineBrush));
        Assert.Equal(ColorOf(StatusPalette.Muted), ColorOf(row.StatusBrush));
        Assert.Equal(Visibility.Collapsed, row.ErrorVisibility);
        Assert.False(row.HasPendingUploads);
        Assert.Empty(model.Summary);
        Assert.Equal("Waiting for connection. Retrying automatically.", Assert.Single(model.Transfers).Detail);
    }

    [Fact]
    public void WaitingWithRetainedUploadsKeepsTheirIdentityButHidesOldTelemetry()
    {
        var mount = Mount();
        var status = Waiting(mount) with
        {
            UploadsInProgress = 1, UploadStatusStale = true,
            Uploads = [new MountUploadFile { RelativePath = "report.docx", State = MountUploadState.Uploading,
                BytesTransferred = 40, TotalBytes = 100, SpeedBytesPerSecond = 16 }],
        };
        var row = new MountRow(mount, status);
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.True(row.HasPendingUploads);
        Assert.True(row.UploadNeedsAttention);
        Assert.Contains("Uploads pending", row.StatusLine, StringComparison.Ordinal);
        Assert.Empty(model.Summary);
        var file = Assert.Single(model.Transfers, transfer => transfer.Key.StartsWith("file:", StringComparison.Ordinal));
        Assert.Equal("Waiting for connection", file.Detail);
        Assert.Equal(Visibility.Collapsed, file.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, file.ProgressTextVisibility);
        Assert.DoesNotContain("/s", file.ProgressText, StringComparison.Ordinal);

        row.ApplyStatus(status with { Lifecycle = nameof(MountLifecycle.Mounted), UploadStatusStale = false,
            Uploads = [status.Uploads[0] with { BytesTransferred = 50 }] });
        model.Update([row], false);
        Assert.Same(file, Assert.Single(model.Transfers));
        Assert.Equal(50, file.Percent);
        Assert.Equal(Visibility.Visible, file.ProgressVisibility);
        row.ApplyStatus(new HostMountStatus(mount.Id, "Stopped", "Not mounted", 0, 0));
        model.Update([row], false);
        Assert.Empty(model.Transfers);
        Assert.Equal("Mount", row.ActionText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRecoveryAndUploadErrorsRemainVisibleDuringNetworkWait(bool recovery)
    {
        var mount = Mount();
        var row = new MountRow(mount, Waiting(mount) with
        {
            UploadRecoveryRequired = recovery, UploadErrors = recovery ? 0 : 1,
        });
        var model = new TransfersViewModel();
        model.Update([row], false);

        Assert.Equal(ColorOf(StatusPalette.Warning), ColorOf(row.StatusLineBrush));
        Assert.Equal(ColorOf(StatusPalette.Warning), ColorOf(row.StatusBrush));
        Assert.Equal("Some transfers need attention.", model.Summary);
        Assert.Contains(recovery ? "Cache recovery required" : "1 upload error", row.StatusLine, StringComparison.Ordinal);
        Assert.Contains(model.Transfers, transfer => transfer.Detail.Contains(recovery ? "need recovery" : "upload error", StringComparison.Ordinal));
    }

    [Fact]
    public void HostLossIsNotMistakenForAnExpectedNetworkWait()
    {
        var mount = Mount();
        var row = new MountRow(mount, Waiting(mount));
        row.MarkHostUnavailable();
        var model = new TransfersViewModel();
        model.Update([row], true);

        Assert.False(row.IsWaitingForConnection);
        Assert.True(row.ShouldStop);
        Assert.Contains("Background host unavailable", row.StatusLine, StringComparison.Ordinal);
        Assert.Equal(ColorOf(StatusPalette.Warning), ColorOf(row.StatusLineBrush));
        Assert.Equal("Transfer status unavailable.", model.Summary);
        Assert.DoesNotContain(model.Transfers, transfer => transfer.Detail.Contains("Retrying automatically", StringComparison.Ordinal));
        row.ApplyStatus(Waiting(mount));
        Assert.True(row.IsWaitingForConnection);
    }

    [Fact]
    public void AuthenticationFailureStillOffersAnActionableErrorInsteadOfAQuietWait()
    {
        var mount = Mount();
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Failed", "Authentication failed. Check the saved credentials."));
        Assert.False(row.IsWaitingForConnection);
        Assert.Equal("Retry", row.ActionText);
        Assert.Equal(Visibility.Visible, row.ErrorVisibility);
        Assert.Contains("Check the saved credentials", row.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal(ColorOf(StatusPalette.Error), ColorOf(row.StatusLineBrush));
    }

    [Fact]
    public void RepeatedWaitObservationsReuseTheExistingCompactRow()
    {
        var mount = Mount();
        var status = Waiting(mount);
        var row = new MountRow(mount, status);
        var model = new TransfersViewModel();
        model.Update([row], false);
        var transfer = Assert.Single(model.Transfers);
        var changes = 0;
        transfer.PropertyChanged += (_, _) => changes++;
        model.Transfers.CollectionChanged += (_, _) => changes++;
        for (var observation = 0; observation < 50; observation++)
        {
            row.ApplyStatus(status);
            model.Update([row], false);
        }
        Assert.Equal(0, changes);
        Assert.Same(transfer, Assert.Single(model.Transfers));
    }

    private static HostMountStatus Waiting(MountSettings mount) =>
        new(mount.Id, "WaitingForConnection", "Waiting for connection", 0, 0);
    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(), DisplayName = "Shared files", RemoteName = "fixture",
        Target = new MountTargetSettings { DriveLetter = 'R' },
    };
    private static Color ColorOf(System.Windows.Media.Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
}

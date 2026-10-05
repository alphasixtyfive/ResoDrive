using System.Windows;
using System.Windows.Media;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class MountStatusLineTests
{
    [Theory]
    [InlineData("Mounted", true)]
    [InlineData("Stopped", true)]
    [InlineData("Stopped", false)]
    public void IdleChecksDoNotCreateAStatusRow(string lifecycle, bool enabled)
    {
        var row = new MountRow(Mount() with { Enabled = enabled }, Status(lifecycle));
        Assert.Equal(Visibility.Collapsed, row.StatusVisibility);
        row.ApplyStatus(row.UploadStatus! with { UploadStatusChecking = true, UploadStatusStale = true });
        Assert.Equal(Visibility.Collapsed, row.StatusVisibility);
        Assert.Empty(row.StatusLine);
    }

    [Fact]
    public void StatusRowPersistsThroughPendingChecksAndCollapsesOnFreshCompletion()
    {
        var mount = Mount();
        var model = new ShellViewModel();
        var idle = Status("Mounted") with { MountId = mount.Id };
        model.Load(new ManagerSettings { Mounts = [mount] }, [idle]);
        var row = Assert.Single(model.Mounts);
        var pending = idle with { UploadsInProgress = 1, UploadsQueued = 2 };
        model.ApplyStatus([pending]);
        Assert.Same(row, Assert.Single(model.Mounts));
        Assert.Equal("↑ 1 uploading · 2 queued", row.StatusLine);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);

        model.ApplyStatus([pending with { UploadStatusChecking = true, UploadStatusStale = true }]);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);
        Assert.Contains("Waiting for upload status", row.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain("uploading", row.StatusLine, StringComparison.Ordinal);

        model.ApplyStatus([idle]);
        Assert.Same(row, Assert.Single(model.Mounts));
        Assert.Equal(Visibility.Collapsed, row.StatusVisibility);
        model.ApplyStatus([idle with { UploadStatusChecking = true, UploadStatusStale = true }]);
        Assert.Equal(Visibility.Collapsed, row.StatusVisibility);
    }

    [Theory]
    [InlineData("Failed", "Mount failed", false)]
    [InlineData("Failed", "Mount failed", true)]
    [InlineData("future-state", "Unknown mount state", false)]
    [InlineData("99", "Unknown mount state", false)]
    public void HostLossPreservesAnExistingFailureOrUnknownState(string lifecycle, string reason, bool pending)
    {
        var row = new MountRow(Mount(), Status(lifecycle) with { UploadsQueued = pending ? 2 : 0 });
        row.MarkHostUnavailable();
        Assert.Equal($"{reason} · Background host unavailable" + (pending ? " · Uploads pending" : string.Empty), row.StatusLine);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);
        if (lifecycle == "Failed")
        {
            Assert.Equal(ColorOf(StatusPalette.Error), ColorOf(row.StatusLineBrush));
            Assert.Equal(ColorOf(StatusPalette.Error), ColorOf(row.StatusBrush));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoppedCacheRecoveryRemainsVisibleEvenWhenDisabled(bool enabled)
    {
        var row = new MountRow(Mount() with { Enabled = enabled }, Status("Stopped") with
        {
            UploadRecoveryRequired = true, UploadStatusChecking = true, UploadStatusStale = true,
        });
        Assert.Equal("Cache recovery required", row.StatusLine);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);
        Assert.Equal(ColorOf(StatusPalette.Warning), ColorOf(row.StatusLineBrush));
        row.MarkHostUnavailable();
        Assert.Equal("Background host unavailable · Cache recovery required", row.StatusLine);
    }

    [Fact]
    public void UploadErrorsOutrankRoutineChecking()
    {
        var row = new MountRow(Mount(), Status("Mounted") with
        {
            UploadStatusChecking = true, UploadStatusStale = true,
            Uploads = [new MountUploadFile { RelativePath = "report.pdf", State = MountUploadState.Retrying }],
        });
        Assert.Contains("1 upload error", row.StatusLine, StringComparison.Ordinal);
        Assert.Equal(ColorOf(StatusPalette.Warning), ColorOf(row.StatusLineBrush));
        row.MarkHostUnavailable();
        Assert.Equal("Background host unavailable · 1 upload error", row.StatusLine);
        Assert.Equal(ColorOf(StatusPalette.Warning), ColorOf(row.StatusBrush));
    }

    [Theory]
    [InlineData("Starting", "Mount queued")]
    [InlineData("Stopping", "Stopping…")]
    [InlineData("WaitingToRestart", "Reconnecting in 5 seconds")]
    [InlineData("Failed", "Mount failed")]
    public void ActualMountActivityAndFailuresHaveAStatusRow(string lifecycle, string detail)
    {
        var row = new MountRow(Mount(), Status(lifecycle) with { Status = detail });
        Assert.Equal(detail, row.StatusLine);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);
    }

    [Theory]
    [InlineData("future-state")]
    [InlineData("99")]
    public void UnknownStatesAreVisibleAndDoNotOfferAnUnsafeMountAction(string lifecycle)
    {
        var row = new MountRow(Mount(), Status(lifecycle));
        Assert.Equal("Unknown mount state", row.StatusLine);
        Assert.False(row.CanAct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoppedIdleDrivesStayQuietOnHostLoss(bool enabled)
    {
        var row = new MountRow(Mount() with { Enabled = enabled }, Status("Stopped"));
        row.MarkHostUnavailable();
        Assert.Equal(Visibility.Collapsed, row.StatusVisibility);
    }

    [Fact]
    public void TruncatedPendingDetailsRemainVisibleWithoutAggregateCounts()
    {
        var row = new MountRow(Mount(), Status("Mounted") with { UploadDetailsTruncated = true });
        Assert.Equal("Uploads pending", row.StatusLine);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);
    }

    [Fact]
    public void DegradedChecksShowTheReasonWithRawDetailInATooltip()
    {
        var row = new MountRow(Mount(), Status("Degraded") with
        {
            Status = "Checking uploads · Close open documents and wait for confirmation · Wait before disconnecting",
            UploadStatusChecking = true, UploadStatusStale = true,
        });
        Assert.Equal("Uploads could not be verified", row.StatusLine);
        Assert.Contains("Wait before disconnecting", row.StatusToolTip, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, row.StatusVisibility);
    }

    private static Color ColorOf(System.Windows.Media.Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static HostMountStatus Status(string lifecycle) => new(Guid.NewGuid(), lifecycle, lifecycle, 0, 0);

    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(), DisplayName = "Shared files", RemoteName = "cloud",
        Target = new MountTargetSettings { DriveLetter = 'S' },
    };
}

using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class MountRowNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualDeserializedObservationsDoNotRefreshTheCard(bool pending)
    {
        var mount = Mount();
        var status = Status(mount, pending);
        var row = new MountRow(mount, status);
        var notifications = 0;
        row.PropertyChanged += (_, _) => notifications++;

        for (var poll = 0; poll < 100; poll++)
        {
            var observation = status with
            {
                UploadObservedAt = DateTimeOffset.UtcNow,
                Uploads = status.Uploads.Select(file => file with { }).ToArray(),
                RemoteWipeStatus = $"Enrollment observation {poll}",
            };
            row.ApplyStatus(observation);
            Assert.Same(observation, row.UploadStatus);
        }

        Assert.Equal(0, notifications);
        Assert.Equal(pending, row.HasPendingUploads);
        Assert.Equal("Unmount", row.ActionText);
    }

    [Fact]
    public void ProgressErrorsAndRecoveryStillRefreshTheCard()
    {
        var mount = Mount();
        var row = new MountRow(mount, Status(mount, pending: true));
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        ApplyChange(row.UploadStatus! with
        {
            Uploads = [row.UploadStatus!.Uploads[0] with { BytesTransferred = 60 }],
            UploadBytesRemaining = 40,
        });
        Assert.Equal(60, Assert.Single(row.UploadStatus!.Uploads).BytesTransferred);

        ApplyChange(row.UploadStatus! with { UploadErrors = 1 });
        Assert.Contains("upload error", row.StatusLine, StringComparison.Ordinal);
        ApplyChange(row.UploadStatus! with { UploadRecoveryRequired = true });
        Assert.Contains("Cache recovery required", row.StatusLine, StringComparison.Ordinal);
        ApplyChange(row.UploadStatus! with
        {
            Lifecycle = "Failed", Status = "Authentication failed. Check the saved credentials.",
        });
        Assert.Equal("Retry", row.ActionText);
        Assert.Contains("Check the saved credentials", row.ErrorDetail, StringComparison.Ordinal);

        row.MarkHostUnavailable();
        changed.Clear();
        row.ApplyStatus(Status(mount, pending: false));
        Assert.NotEmpty(changed);
        Assert.Empty(row.StatusLine);
        Assert.Equal("Unmount", row.ActionText);
        Assert.Empty(row.ErrorDetail);
        Assert.False(row.HasPendingUploads);

        void ApplyChange(HostMountStatus observation)
        {
            changed.Clear();
            row.ApplyStatus(observation);
            Assert.Contains(nameof(MountRow.UploadStatus), changed);
            Assert.Contains(nameof(MountRow.UploadActivityText), changed);
        }
    }

    [Fact]
    public void CheckingObservationTimeOnlyRefreshesWhenRecentProgressChanges()
    {
        var mount = Mount();
        var status = Status(mount, pending: true) with
        {
            UploadStatusChecking = true, UploadStatusStale = true,
            UploadObservedAt = DateTimeOffset.UtcNow,
        };
        var row = new MountRow(mount, status);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.ApplyStatus(status with { UploadObservedAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Assert.Empty(changed);
        Assert.Contains("uploading", row.UploadActivityText, StringComparison.Ordinal);

        row.ApplyStatus(status with { UploadObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Contains(nameof(MountRow.UploadActivityText), changed);
        Assert.Equal("Waiting for upload status", row.UploadActivityText);

        changed.Clear();
        row.ApplyStatus(status with { UploadObservedAt = DateTimeOffset.UtcNow.AddMinutes(-2) });
        Assert.Empty(changed);

        row.ApplyStatus(status with { UploadObservedAt = DateTimeOffset.UtcNow });
        Assert.Contains(nameof(MountRow.UploadActivityText), changed);
        Assert.Contains("uploading", row.UploadActivityText, StringComparison.Ordinal);
    }

    [Fact]
    public void HostRecoveryRefreshesAnOtherwiseEqualObservation()
    {
        var mount = Mount();
        var status = Status(mount, pending: false) with { UploadStatusStale = true };
        var row = new MountRow(mount, status);
        row.MarkHostUnavailable();
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.ApplyStatus(status with { Uploads = [] });

        Assert.Contains(nameof(MountRow.StatusLine), changed);
        Assert.DoesNotContain("Background host unavailable", row.StatusLine, StringComparison.Ordinal);
    }

    private static HostMountStatus Status(MountSettings mount, bool pending) =>
        new(mount.Id, "Mounted", "Mounted", 0, pending ? 1 : 0)
        {
            UploadObservedAt = DateTimeOffset.UtcNow,
            UploadBytesRemaining = pending ? 50 : 0,
            Uploads = pending
                ? [new MountUploadFile { RelativePath = "report.docx", State = MountUploadState.Uploading,
                    BytesTransferred = 50, TotalBytes = 100 }]
                : [],
        };

    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(), DisplayName = "Cloud files", RemoteName = "cloud",
        Target = new MountTargetSettings { DriveLetter = 'R' },
    };
}

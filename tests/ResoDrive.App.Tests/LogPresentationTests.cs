using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class LogPresentationTests
{
    [Fact]
    public void LateEventsAreSortedByOccurrenceAndHistoryKeepsTheNewestHundred()
    {
        var model = new ShellViewModel();
        var start = DateTimeOffset.Parse("2026-10-05T09:00:00+01:00", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 110; i >= 0; i--)
            model.AddLogEntry($"Event {i}", "Detail", occurredAt: start.AddSeconds(i));
        Assert.Equal(100, model.Log.Count);
        Assert.Equal("Event 110", model.Log[0].Title);
        Assert.Equal("Event 11", model.Log[^1].Title);
        model.AddLogEntry("Historical sync result", "Older", occurredAt: start.AddDays(-1));
        Assert.DoesNotContain(model.Log, entry => entry.Title == "Historical sync result");
    }

    [Fact]
    public void EventPreservesFullDetailsAndAccessibleAbsoluteTime()
    {
        var model = new ShellViewModel();
        var occurred = DateTimeOffset.Parse("2026-10-05T09:00:03+01:00", System.Globalization.CultureInfo.InvariantCulture);
        model.AddLogEntry("Drive failed", "First line\r\nSecond\tline", LogSeverity.Error, occurred);
        var entry = Assert.Single(model.Log);
        Assert.Equal("First line\r\nSecond\tline", entry.Detail);
        Assert.Equal(occurred, entry.OccurredAt);
        Assert.Equal(LogSeverity.Error, entry.Severity);
        Assert.Contains("Error · Drive failed", entry.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(entry.Detail, entry.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(occurred.LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture),
            entry.Time, StringComparison.Ordinal);
        Assert.Contains(entry.FullTime, entry.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void MountRequestsBecomeEventsOnlyWhenObservedAndCountdownsStayQuiet()
    {
        var mount = Mount();
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [Status(mount, "Stopped")]);
        model.ApplyStatus([Status(mount, "Starting")]);
        Assert.Empty(model.Log);
        model.ApplyStatus([Status(mount, "Mounted")]);
        model.ApplyStatus([Status(mount, "Mounted")]);
        Assert.Equal(LogSeverity.Success, Assert.Single(model.Log).Severity);
        model.ApplyStatus([Status(mount, "WaitingToRestart", "Reconnecting in 10 seconds")]);
        model.ApplyStatus([Status(mount, "WaitingToRestart", "Reconnecting in 9 seconds")]);
        Assert.Equal(2, model.Log.Count);
        model.ApplyStatus([Status(mount, "Mounted")]);
        model.ApplyStatus([Status(mount, "Stopping")]);
        model.ApplyStatus([Status(mount, "Stopped")]);
        Assert.Equal(4, model.Log.Count);
        Assert.StartsWith("Stopped", model.Log[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void PollingAndStaleChecksDoNotRepeatAnUploadErrorEpisode()
    {
        var mount = Mount();
        var model = new ShellViewModel();
        var mounted = Status(mount, "Mounted");
        model.Load(new ManagerSettings { Mounts = [mount] }, [mounted]);
        model.ApplyStatus([mounted with { UploadsInProgress = 1, UploadStatusChecking = true }]);
        Assert.Empty(model.Log);
        model.ApplyStatus([mounted with { UploadErrors = 1 }]);
        model.ApplyStatus([mounted with { UploadStatusChecking = true, UploadStatusStale = true }]);
        model.ApplyStatus([mounted with { UploadErrors = 2 }]);
        Assert.Equal(LogSeverity.Error, Assert.Single(model.Log).Severity);
        model.ApplyStatus([mounted]);
        model.ApplyStatus([mounted with { UploadErrors = 1 }]);
        Assert.Equal(2, model.Log.Count);
        model.ApplyStatus([mounted with { UploadErrors = 1, UploadRecoveryRequired = true }]);
        model.ApplyStatus([mounted with { UploadErrors = 1, UploadRecoveryRequired = true }]);
        Assert.Equal(3, model.Log.Count);
        Assert.Equal(LogSeverity.Warning, model.Log[0].Severity);
    }

    [Fact]
    public void UnconfirmedMountsDoNotBecomeDisconnectedEventsOnReset()
    {
        var mount = Mount();
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [Status(mount, "Starting")]);
        model.ApplyStatus([Status(mount, "Stopped")]);
        Assert.Empty(model.Log);
        model.ApplyStatus([Status(mount, "Failed")]);
        model.ApplyStatus([Status(mount, "Stopped")]);
        Assert.StartsWith("Drive failed", Assert.Single(model.Log).Title, StringComparison.Ordinal);
        model.ApplyStatus([Status(mount, "WaitingToRestart")]);
        model.ApplyStatus([Status(mount, "Stopped")]);
        Assert.Equal(3, model.Log.Count);
        Assert.StartsWith("Stopped", model.Log[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void SpecificDegradedWarningsAreNotRepeatedOrDuplicatedByCheckingSnapshots()
    {
        var mount = Mount();
        var model = new ShellViewModel();
        var recovery = Status(mount, "Degraded") with { UploadRecoveryRequired = true };
        model.Load(new ManagerSettings { Mounts = [mount] }, [recovery]);
        Assert.StartsWith("Cache recovery", Assert.Single(model.Log).Title, StringComparison.Ordinal);
        model.ApplyStatus([recovery with { UploadRecoveryRequired = false, UploadStatusChecking = true }]);
        model.ApplyStatus([recovery]);
        Assert.Single(model.Log);
        model.ApplyStatus([Status(mount, "Mounted")]);
        model.ApplyStatus([Status(mount, "Degraded") with { UploadErrors = 1 }]);
        Assert.Equal(3, model.Log.Count); // Recovery, confirmed mounted, specific error.
        Assert.StartsWith("Upload error", model.Log[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeCompletedJobCataloguesDoNotEvictAndRelogEveryPoll()
    {
        var jobs = Enumerable.Range(0, 300).Select(index => new SyncJobSettings
            { Id = Guid.NewGuid(), DisplayName = $"Job {index}", LocalPath = @"C:\Data", Mode = "CopyFromRemote" }).ToArray();
        var mount = Mount() with { SyncJobs = jobs };
        var completed = DateTimeOffset.Now.AddHours(-1);
        var statuses = jobs.Select((job, index) => new HostSyncStatus(mount.Id, job.Id, "Succeeded", "Up to date",
            completed.AddSeconds(index), ChecksCompleted: 10)).ToArray();
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = [mount] }, [], statuses);
        var before = model.Log.ToArray();
        model.ApplySyncStatus(statuses.Reverse().ToArray());
        Assert.Equal(before.Length, model.Log.Count);
        for (var index = 0; index < before.Length; index++) Assert.Same(before[index], model.Log[index]);
        Assert.Equal(statuses[^1].CompletedAt, model.Log[0].OccurredAt);
        var next = statuses[0] with { CompletedAt = DateTimeOffset.Now };
        model.ApplySyncStatus([next]);
        Assert.Equal(next.CompletedAt, model.Log[0].OccurredAt);
        Assert.Equal(LogSeverity.Success, model.Log[0].Severity);
    }

    private static MountSettings Mount() => new()
    {
        Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud",
        Target = new MountTargetSettings { DriveLetter = 'S' },
    };

    private static HostMountStatus Status(MountSettings mount, string lifecycle, string? detail = null) =>
        new(mount.Id, lifecycle, detail ?? lifecycle, 0, 0);
}

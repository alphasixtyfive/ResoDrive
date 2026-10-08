using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App;

public sealed partial class ShellViewModel
{
    private readonly Dictionary<Guid, SyncLogObservation> _loggedSyncRuns = [];
    private readonly Dictionary<Guid, MountLogObservation> _mountLogObservations = [];
    private readonly TimeProvider _clock;
    public ShellViewModel() : this(TimeProvider.System) { }
    internal ShellViewModel(TimeProvider clock) => _clock = clock;
    public void AddLogEntry(
        string title,
        string detail,
        LogSeverity severity = LogSeverity.Information,
        DateTimeOffset? occurredAt = null)
    {
        var entry = new LogRow(title, detail, occurredAt ?? DateTimeOffset.Now, severity);
        var index = 0;
        while (index < Log.Count && Log[index].OccurredAt > entry.OccurredAt) index++;
        Log.Insert(index, entry);
        while (Log.Count > 100)
            Log.RemoveAt(Log.Count - 1);
    }

    private void ObserveMountStatus(MountSettings mount, HostMountStatus? status)
    {
        if (status is null || !Enum.TryParse<MountLifecycle>(status.Lifecycle, true, out var lifecycle) ||
            !Enum.IsDefined(lifecycle))
        {
            if (_mountLogObservations.TryGetValue(mount.Id, out var interrupted))
                _ = interrupted.Attention.Observe(attention: false, confirmedHealthy: false);
            return;
        }
        if (!_mountLogObservations.TryGetValue(mount.Id, out var observation))
            _mountLogObservations[mount.Id] = observation = new(_clock);
        var previous = observation.Lifecycle;
        var location = mount.Target.DriveLetter is char letter ? $"{letter}:" : mount.RemoteName;
        var detail = $"{location} · {status.Status}";
        var errors = UploadPresentation.Errors(status) > 0;
        var recovery = status.UploadRecoveryRequired;
        var confirmed = !status.UploadStatusChecking && !status.UploadStatusStale;
        var transition = observation.Attention.Observe(
            lifecycle is MountLifecycle.Degraded or MountLifecycle.Failed or MountLifecycle.WaitingToRestart || errors || recovery,
            confirmed && lifecycle == MountLifecycle.Mounted && !UploadPresentation.HasPending(status));
        if (transition == AttentionTransition.Recovered) observation.ClearReportedIssues();
        if (transition == AttentionTransition.Began && !errors && !recovery)
        {
            var outcome = lifecycle switch
            {
                MountLifecycle.Failed => ("Drive failed", LogSeverity.Error),
                MountLifecycle.Degraded => ("Drive needs attention", LogSeverity.Warning),
                MountLifecycle.WaitingToRestart => ("Reconnecting", LogSeverity.Warning),
                _ => (string.Empty, LogSeverity.Information)
            };
            if (outcome.Item1.Length > 0)
                AddLogEntry($"{outcome.Item1} · {mount.DisplayName}", detail, outcome.Item2);
        }
        else if (lifecycle == MountLifecycle.Failed && !observation.FailureReported)
            AddLogEntry($"Drive failed · {mount.DisplayName}", detail, LogSeverity.Error);
        if (lifecycle == MountLifecycle.Failed) observation.FailureReported = true;
        if (lifecycle == MountLifecycle.Mounted && confirmed && !errors && !recovery &&
            (transition == AttentionTransition.Recovered ||
                (!observation.Attention.IsActive && previous is not null && previous != MountLifecycle.Mounted)))
            AddLogEntry($"Mounted · {mount.DisplayName}", detail, LogSeverity.Success);
        var explicitStop = previous == MountLifecycle.Stopping && confirmed && !errors && !recovery;
        if (lifecycle == MountLifecycle.Stopped &&
            previous is MountLifecycle.Mounted or MountLifecycle.Degraded or MountLifecycle.Stopping or MountLifecycle.WaitingToRestart &&
            (!observation.Attention.IsActive || explicitStop))
        {
            AddLogEntry($"Stopped · {mount.DisplayName}", detail);
            if (explicitStop)
            {
                observation.Attention.Reset();
                observation.ClearReportedIssues();
            }
        }
        if (recovery && !observation.RecoveryReported)
        {
            AddLogEntry($"Cache recovery · {mount.DisplayName}",
                $"{location} · Cached uploads need attention. Open Transfers to review them.", severity: LogSeverity.Warning);
            observation.RecoveryReported = true;
        }
        else if (errors && !observation.UploadErrorReported && !recovery)
        {
            AddLogEntry($"Upload error · {mount.DisplayName}",
                $"{location} · Open Transfers for affected files and retry details.", severity: LogSeverity.Error);
            observation.UploadErrorReported = true;
        }
        observation.Lifecycle = lifecycle;
    }

    private sealed class MountLogObservation(TimeProvider clock)
    {
        internal MountLifecycle? Lifecycle { get; set; }
        internal AttentionEpisode Attention { get; } = new(clock);
        internal bool FailureReported { get; set; }
        internal bool RecoveryReported { get; set; }
        internal bool UploadErrorReported { get; set; }
        internal void ClearReportedIssues() => FailureReported = RecoveryReported = UploadErrorReported = false;
    }

    private sealed record SyncLogObservation(DateTimeOffset CompletedAt, bool FailureActive);

    private static bool IsTerminalSyncStatus(HostSyncStatus status) =>
        status.CompletedAt is not null &&
        Enum.TryParse(status.Lifecycle, true, out SyncLifecycle lifecycle) &&
        lifecycle is SyncLifecycle.Succeeded or SyncLifecycle.Failed or SyncLifecycle.Cancelled;

    private void AddSyncOutcome(HostSyncStatus status)
    {
        if (status.CompletedAt is not { } completedAt ||
            (_loggedSyncRuns.TryGetValue(status.SyncJobId, out var previous) && previous.CompletedAt >= completedAt))
        {
            return;
        }
        var job = Jobs.FirstOrDefault(item => item.Id == status.SyncJobId);
        if (job is null || !Enum.TryParse(status.Lifecycle, true, out SyncLifecycle lifecycle))
        {
            return;
        }
        _loggedSyncRuns[status.SyncJobId] = new(completedAt,
            lifecycle == SyncLifecycle.Failed || (previous?.FailureActive == true && lifecycle != SyncLifecycle.Succeeded));
        if (lifecycle == SyncLifecycle.Failed && previous?.FailureActive == true) return;

        var title = lifecycle switch
        {
            SyncLifecycle.Succeeded => $"Sync completed · {job.Name}",
            SyncLifecycle.Failed => $"Sync failed · {job.Name}",
            SyncLifecycle.Cancelled => $"Sync cancelled · {job.Name}",
            _ => job.Name
        };
        var details = new List<string> { job.MountName };
        if (!string.IsNullOrWhiteSpace(status.Status))
            details.Add(status.Status);
        if (status.TransfersCompleted is > 0)
            details.Add($"{status.TransfersCompleted} file{(status.TransfersCompleted == 1 ? string.Empty : "s")}");
        else if (status.ChecksCompleted is > 0)
            details.Add($"{status.ChecksCompleted} checked");
        if (status.BytesTransferred is > 0)
            details.Add($"{DisplayFormatting.Bytes(status.BytesTransferred.Value)} transferred");
        if (status.Errors is > 0)
            details.Add($"{status.Errors} error{(status.Errors == 1 ? string.Empty : "s")}");

        AddLogEntry(
            title,
            string.Join(" · ", details),
            lifecycle switch
            {
                SyncLifecycle.Succeeded => LogSeverity.Success,
                SyncLifecycle.Failed => LogSeverity.Error,
                _ => LogSeverity.Information
            },
            completedAt);
    }

}

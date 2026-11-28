using System.Collections.ObjectModel;
using System.Windows;
using ResoDrive.Core.Domain;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed record TransferRowContent(
    string Key, string Name, string Detail, long? Bytes = null, long? Total = null,
    double? Speed = null, bool ShowProgress = false, string? ProgressText = null, double? ProgressPercent = null);

internal sealed class TransferRow(string key) : NotifyBase
{
    internal string Key { get; } = key;
    private string _name = string.Empty;
    private string _detail = string.Empty;
    private string _progress = string.Empty;
    private double _percent;
    private bool _showProgress;
    private bool _hasPercent;
    private TransferRowContent? _lastContent;
    public string Name => _name;
    public string Detail => _detail;
    public string ProgressText => _progress;
    public double Percent => _percent;
    public Visibility ProgressVisibility => _showProgress && _hasPercent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressTextVisibility => _showProgress && _progress.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal void Update(TransferRowContent row)
    {
        if (row == _lastContent) return;
        _lastContent = row;
        var progress = row.ProgressText ?? UploadPresentation.Progress(row.Bytes, row.Total, row.Speed);
        var observedPercent = row.ProgressPercent is >= 0 && double.IsFinite(row.ProgressPercent.Value)
            ? row.ProgressPercent : null;
        var measuredTotal = row.Total is > 0 && row.Bytes is >= 0;
        var percent = observedPercent is { } value ? Math.Clamp(value, 0, 100)
            : measuredTotal ? Math.Clamp(100d * row.Bytes!.Value / row.Total!.Value, 0, 100) : 0;
        var hasPercent = observedPercent is not null || measuredTotal;
        if (_name == row.Name && _detail == row.Detail && _progress == progress && _percent == percent &&
            _showProgress == row.ShowProgress && _hasPercent == hasPercent) return;
        _name = row.Name;
        _detail = row.Detail;
        _progress = progress;
        _percent = percent;
        _showProgress = row.ShowProgress;
        _hasPercent = hasPercent;
        Changed(string.Empty);
    }
}

internal sealed class TransfersViewModel : NotifyBase
{
    private readonly TimeProvider _clock;
    internal TransfersViewModel(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    private string _summary = "No transfers detected.";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public ObservableCollection<TransferRow> Transfers { get; } = [];

    internal void Update(IReadOnlyList<MountRow> mounts, bool hostUnavailable,
        IReadOnlyList<SyncRow>? syncJobs = null, int activeSyncJobs = 0,
        bool powerProtectionUnavailable = false, bool syncStatusAvailable = true)
    {
        var content = new TransferListContent(hostUnavailable || !syncStatusAvailable);
        foreach (var mount in mounts)
            AddUploadRows(content, mount, hostUnavailable);
        AddSyncRows(content, syncJobs ?? [], activeSyncJobs, syncStatusAvailable);
        if (powerProtectionUnavailable)
            content.Rows.Add(new("power:unavailable", "Windows power protection",
                "Windows could not apply all power protection. Check transfers before shutting down."));

        ReconcileRows(content.Rows);
        Summary = SummaryFor(content, powerProtectionUnavailable);
    }

    private void AddUploadRows(TransferListContent content, MountRow mount, bool hostUnavailable)
    {
        var rows = content.Rows;
        var status = mount.UploadStatus;
        var changingMount = mount.IsTransient;
        var waitingForConnection = mount.IsWaitingForConnection;
        if (!mount.ShouldStop && status?.UploadRecoveryRequired != true && !changingMount) return;

        var checking = status?.UploadStatusChecking == true || changingMount;
        content.Observing |= checking;
        content.StatusUnavailable |= !waitingForConnection && !checking && (status is null || status.UploadStatusStale);
        content.Pending |= waitingForConnection;
        content.NeedsAttention |= UploadPresentation.Errors(status) > 0 || status?.UploadRecoveryRequired == true;
        if (status?.UploadRecoveryRequired == true)
            rows.Add(new($"{mount.Id}:status", mount.Name,
                "Cached files need recovery. Restore this drive to finish uploading."));
        else if (waitingForConnection)
            rows.Add(new($"{mount.Id}:status", mount.Name,
                "Waiting for connection. Retrying automatically."));
        else if (!checking && (status is null || status.UploadStatusStale))
            rows.Add(new($"{mount.Id}:status", mount.Name,
                "Waiting for upload status. Checking again automatically."));
        if (status is null) return;

        content.Pending |= UploadPresentation.HasPending(status);
        var files = status.Uploads ?? [];
        var visibleFiles = files.OrderBy(file => file.State == MountUploadState.Uploading ? 0 : 1).Take(5).ToArray();
        var fileStatusUnavailable = hostUnavailable || status.UploadStatusStale || status.UploadStatusChecking ||
            status.UploadRecoveryRequired || changingMount || waitingForConnection;
        var retainProgress = !hostUnavailable && !changingMount && !waitingForConnection && !status.UploadRecoveryRequired &&
            UploadPresentation.HasRecentProgress(status, _clock.GetUtcNow());
        if (!waitingForConnection && files.Count == 0 &&
            (status.UploadsQueued > 0 || status.UploadsInProgress > 0 || status.UploadsDirty > 0))
            rows.Add(new($"{mount.Id}:activity", mount.Name, fileStatusUnavailable
                ? "Uploads pending. Waiting for upload status." : UploadPresentation.Activity(status)));
        foreach (var file in visibleFiles)
        {
            var detail = UploadPresentation.FileState(file);
            if (retainProgress && file.State == MountUploadState.Uploading)
                detail += " · updating status";
            else if (waitingForConnection)
                detail = "Waiting for connection";
            else if (fileStatusUnavailable)
                detail = "Waiting for upload status";
            var showProgress = (!fileStatusUnavailable || retainProgress) && file.State == MountUploadState.Uploading;
            rows.Add(new($"file:{mount.Id}:{file.RelativePath}", $"{mount.Name} · {file.RelativePath}", detail,
                file.BytesTransferred, file.TotalBytes, fileStatusUnavailable ? null : file.SpeedBytesPerSecond, showProgress));
        }

        if (status.UploadDetailsTruncated || files.Count > visibleFiles.Length)
            rows.Add(new($"{mount.Id}:more", mount.Name, "More files pending"));
        var errors = UploadPresentation.Errors(status);
        if (errors > 0 && !visibleFiles.Any(file => file.State == MountUploadState.Retrying))
            rows.Add(new($"{mount.Id}:errors", mount.Name,
                $"{UploadPresentation.ErrorCount(errors)}. Keep the cached files and check the connection."));
    }

    private static void AddSyncRows(TransferListContent content, IReadOnlyList<SyncRow> jobs,
        int activeSyncJobs, bool syncStatusAvailable)
    {
        var rows = content.Rows;
        var freshActive = jobs.Count(job => job.IsBusy && !job.StatusUnavailable);
        var retainUnavailable = !syncStatusAvailable || activeSyncJobs > freshActive;
        var representedSyncs = 0;
        foreach (var job in jobs)
        {
            var status = job.TransferStatus;
            var recognized = Enum.TryParse(status?.Lifecycle, true, out SyncLifecycle lifecycle) && Enum.IsDefined(lifecycle);
            if (!job.IsBusy && (status is null || recognized)) continue;
            if (job.StatusUnavailable && !retainUnavailable) continue;
            var unavailable = !syncStatusAvailable || job.StatusUnavailable || !recognized;
            content.StatusUnavailable |= unavailable;
            content.Pending = true;
            representedSyncs++;
            var key = $"sync:{job.MountId}:{job.Id}";
            var name = $"{job.MountName} · {job.Name}";
            if (unavailable || status is null)
            {
                rows.Add(new(key, name, "Waiting for sync status. Checking again automatically."));
                continue;
            }
            status = SyncProgressPresentation.Normalize(status);
            var hasMode = Enum.TryParse<SyncMode>(job.Settings.Mode, true, out var parsedMode) && parsedMode.IsSupported();
            // Active status formatting is independent of direction. An invalid
            // edited mode must not hide work still running with its old settings.
            var text = SyncStatusPresentation.Create(hasMode ? parsedMode : SyncMode.CopyToRemote,
                enabled: true, busy: true, lifecycle, status with
                {
                    BytesTransferred = null, SpeedBytesPerSecond = null, EtaSeconds = null,
                }, recognized: true, transferStarted: job.HasTransferred);
            var detail = $"{(hasMode ? job.ModeLabel : "Sync")} · {text.Primary}";
            var hasFileCounts = status.TotalTransfers > 0 || status.TotalChecks > 0;
            if (hasFileCounts) detail += $" · {text.Secondary}";
            if (status.Errors > 0)
            {
                content.NeedsAttention = true;
                detail += $" · {status.Errors} transfer {(status.Errors == 1 ? "error" : "errors")}";
            }
            var progress = SyncProgressText(status);
            if (progress.Length == 0 && !hasFileCounts && status.ProgressPercent is null)
                progress = text.Secondary;
            rows.Add(new(key, name, detail, status.BytesTransferred, status.TotalBytes,
                status.SpeedBytesPerSecond, lifecycle == SyncLifecycle.Running,
                progress, status.ProgressPercent));
        }
        var missingSyncs = Math.Max(0, activeSyncJobs) - representedSyncs;
        if (missingSyncs > 0)
        {
            content.Pending = true;
            content.StatusUnavailable = true;
            rows.Add(new("sync:unrepresented", "Sync jobs",
                $"{missingSyncs} active sync {(missingSyncs == 1 ? "job" : "jobs")}. Waiting for transfer details."));
        }
    }

    private void ReconcileRows(List<TransferRowContent> rows)
    {
        var keys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        for (var index = Transfers.Count - 1; index >= 0; index--)
            if (!keys.Contains(Transfers[index].Key)) Transfers.RemoveAt(index);
        var existing = Transfers.ToDictionary(row => row.Key, StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!existing.TryGetValue(row.Key, out var target))
            {
                target = new TransferRow(row.Key);
                Transfers.Insert(index, target);
            }
            else if (!ReferenceEquals(Transfers[index], target))
                Transfers.Move(Transfers.IndexOf(target), index);
            target.Update(row);
        }
    }

    private static string SummaryFor(TransferListContent content, bool powerProtectionUnavailable)
    {
        if (powerProtectionUnavailable) return "Windows power protection needs attention.";
        if (content.NeedsAttention) return "Some transfers need attention.";
        if (content.StatusUnavailable) return "Transfer status unavailable.";
        if (content.Pending) return string.Empty;
        if (content.Rows.Count > 0) return "Some transfers need attention.";
        return content.Observing ? "No transfers detected." : "No active transfers.";
    }

    private sealed class TransferListContent(bool statusUnavailable)
    {
        public List<TransferRowContent> Rows { get; } = [];
        public bool StatusUnavailable { get; set; } = statusUnavailable;
        public bool NeedsAttention { get; set; }
        public bool Pending { get; set; }
        public bool Observing { get; set; }
    }

    private static string SyncProgressText(HostSyncStatus status)
    {
        var progress = UploadPresentation.Progress(status.BytesTransferred,
            status.BytesTransferred is null ? null : status.TotalBytes, status.SpeedBytesPerSecond);
        if (status.EtaSeconds is >= 1 and < 31_536_000)
        {
            var remaining = $"{DisplayFormatting.Duration(status.EtaSeconds.Value)} left";
            progress = progress.Length == 0 ? remaining : $"{progress} · {remaining}";
        }
        return progress;
    }

}

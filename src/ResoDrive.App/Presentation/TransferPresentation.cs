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
    public string Name => _name;
    public string Detail => _detail;
    public string ProgressText => _progress;
    public double Percent => _percent;
    public Visibility ProgressVisibility => _showProgress && _hasPercent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressTextVisibility => _showProgress && _progress.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal void Update(TransferRowContent row)
    {
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
    private string _summary = "Checking transfers…";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public ObservableCollection<TransferRow> Transfers { get; } = [];

    internal void Update(IReadOnlyList<MountRow> mounts, bool hostUnavailable,
        IReadOnlyList<SyncRow>? syncJobs = null, int activeSyncJobs = 0,
        bool powerProtectionUnavailable = false, bool syncStatusAvailable = true)
    {
        var rows = new List<TransferRowContent>();
        var unknown = hostUnavailable || !syncStatusAvailable;
        var needsAttention = false;
        var pending = false;
        foreach (var mount in mounts)
        {
            var status = mount.UploadStatus;
            var changingMount = mount.IsTransient;
            if (!mount.ShouldStop && status?.UploadRecoveryRequired != true && !changingMount) continue;
            unknown |= status is null || status.UploadStatusStale || status.UploadStatusChecking || changingMount;
            needsAttention |= status?.UploadErrors > 0 || status?.UploadRecoveryRequired == true;
            if (status is null || status.UploadStatusStale || status.UploadStatusChecking || status.UploadRecoveryRequired || changingMount)
                rows.Add(new($"{mount.Id}:status", mount.Name, status?.UploadStatusChecking == true
                    ? "Checking changed files. Keep the PC running while uploads are checked."
                    : changingMount ? "Checking drive state. Keep the PC running while the drive connects or disconnects."
                    : status is null || status.UploadStatusStale
                    ? "Upload status unavailable. Keep ResoDrive running until it reconnects."
                    : RcloneErrorMessage.Clean(status.Status,
                        "Cached files need recovery. Restore this drive to finish uploading.")));
            if (status is null) continue;
            pending |= UploadPresentation.HasPending(status);
            var files = status.Uploads ?? [];
            var visibleFiles = files.OrderBy(file => file.State == MountUploadState.Uploading ? 0 : 1).Take(5).ToArray();
            var fileStatusUnavailable = hostUnavailable || status.UploadStatusStale || status.UploadStatusChecking ||
                status.UploadRecoveryRequired || changingMount;
            if (files.Count == 0 && !fileStatusUnavailable &&
                (status.UploadsQueued > 0 || status.UploadsInProgress > 0 || status.UploadsDirty > 0))
                rows.Add(new($"{mount.Id}:activity", mount.Name, UploadPresentation.Activity(status)));
            foreach (var file in visibleFiles)
                rows.Add(new($"file:{mount.Id}:{file.RelativePath}", $"{mount.Name} · {file.RelativePath}",
                    fileStatusUnavailable ? "Waiting for upload status" : UploadPresentation.FileState(file),
                    file.BytesTransferred, file.TotalBytes, fileStatusUnavailable ? null : file.SpeedBytesPerSecond,
                    !fileStatusUnavailable && file.State == MountUploadState.Uploading));
            if (status.UploadDetailsTruncated || files.Count > visibleFiles.Length)
                rows.Add(new($"{mount.Id}:more", mount.Name, "More files pending"));
            if (status.UploadErrors > 0 && !visibleFiles.Any(file => file.State == MountUploadState.Retrying))
                rows.Add(new($"{mount.Id}:errors", mount.Name, $"{UploadPresentation.ErrorCount(status.UploadErrors.Value)}. Keep the cached files and check the connection."));
        }
        var jobs = syncJobs ?? [];
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
            unknown |= unavailable;
            pending = true;
            representedSyncs++;
            var key = $"sync:{job.MountId}:{job.Id}";
            var name = $"{job.MountName} · {job.Name}";
            if (unavailable || status is null)
            {
                rows.Add(new(key, name, "Sync status unavailable. Keep ResoDrive running until it reconnects."));
                continue;
            }
            status = SanitizeProgress(status);
            var hasMode = Enum.TryParse<SyncMode>(job.Settings.Mode, true, out var parsedMode) && parsedMode.IsSupported();
            // Active status formatting is independent of direction. An invalid
            // edited mode must not hide work still running with its old settings.
            var text = SyncStatusPresentation.Create(hasMode ? parsedMode : SyncMode.CopyToRemote,
                enabled: true, busy: true, lifecycle, status with
                {
                    BytesTransferred = null, SpeedBytesPerSecond = null, EtaSeconds = null,
                }, recognized: true);
            var detail = $"{(hasMode ? job.ModeLabel : "Sync")} · {text.Primary}";
            var hasFileCounts = status.TotalTransfers > 0 || status.TotalChecks > 0;
            if (hasFileCounts) detail += $" · {text.Secondary}";
            if (status.Errors > 0)
            {
                needsAttention = true;
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
            pending = true;
            unknown = true;
            rows.Add(new("sync:unrepresented", "Sync jobs",
                $"{missingSyncs} active sync {(missingSyncs == 1 ? "job" : "jobs")}. Waiting for transfer details."));
        }
        if (powerProtectionUnavailable)
            rows.Add(new("power:unavailable", "Windows power protection", "Windows could not apply all power protection. Check transfers before shutting down."));
        var keys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in Transfers.Where(row => !keys.Contains(row.Key)).ToArray()) Transfers.Remove(obsolete);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var target = Transfers.FirstOrDefault(item => item.Key.Equals(row.Key, StringComparison.Ordinal));
            if (target is null) { target = new TransferRow(row.Key); Transfers.Insert(index, target); }
            else if (Transfers.IndexOf(target) != index) Transfers.Move(Transfers.IndexOf(target), index);
            target.Update(row);
        }
        Summary = unknown ? "Checking transfers…"
            : powerProtectionUnavailable ? "Windows power protection needs attention."
            : needsAttention ? "Some transfers need attention."
            : pending ? string.Empty
            : rows.Count > 0 ? "Some transfers need attention."
            : "No active transfers.";
    }

    private static HostSyncStatus SanitizeProgress(HostSyncStatus status) => status with
    {
        BytesTransferred = Nonnegative(status.BytesTransferred),
        TotalBytes = status.TotalBytes is > 0 ? status.TotalBytes : null,
        ProgressPercent = Nonnegative(status.ProgressPercent),
        ChecksCompleted = Nonnegative(status.ChecksCompleted),
        TotalChecks = status.TotalChecks is > 0 ? status.TotalChecks : null,
        TransfersCompleted = Nonnegative(status.TransfersCompleted),
        TotalTransfers = status.TotalTransfers is > 0 ? status.TotalTransfers : null,
        Errors = Nonnegative(status.Errors),
        SpeedBytesPerSecond = Nonnegative(status.SpeedBytesPerSecond),
        EtaSeconds = Nonnegative(status.EtaSeconds),
    };

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

    private static long? Nonnegative(long? value) => value is >= 0 ? value : null;
    private static double? Nonnegative(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
}

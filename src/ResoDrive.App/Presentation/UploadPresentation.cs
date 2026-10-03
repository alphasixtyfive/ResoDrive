using System.Collections.ObjectModel;
using System.Windows;
using ResoDrive.Core.Domain;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class UploadPresentation
{
    internal static bool HasPending(HostMountStatus? status) => status is not null &&
        (status.UploadsQueued > 0 || status.UploadsInProgress > 0 || status.UploadsDirty > 0 ||
         status.UploadErrors > 0 || status.UploadRecoveryRequired);

    internal static string Activity(HostMountStatus? status)
    {
        if (status is null) return string.Empty;
        if (status.UploadStatusChecking) return "Checking uploads…";
        if (status.UploadStatusStale) return "Upload status unavailable";
        return string.Join(" · ", new[]
        {
            status.UploadsInProgress > 0 ? $"{status.UploadsInProgress} uploading" : null,
            status.UploadsQueued > 0 ? $"{status.UploadsQueued} queued" : null,
            status.UploadsDirty > 0 ? $"{status.UploadsDirty} waiting for close" : null,
            status.UploadErrors > 0 ? $"{status.UploadErrors} upload errors" : null,
            status.UploadRecoveryRequired ? "Cache recovery required" : null,
        }.Where(value => value is not null));
    }

    internal static string Progress(long? bytes, long? total, double? speed = null)
    {
        var amount = total is >= 0
            ? $"{DisplayFormatting.Bytes(bytes ?? 0)} of {DisplayFormatting.Bytes(total.Value)}"
            : bytes is > 0 ? DisplayFormatting.Bytes(bytes.Value) : string.Empty;
        if (speed is not > 0) return amount;
        var rate = $"{DisplayFormatting.Bytes(speed.Value)}/s";
        return amount.Length > 0 ? $"{amount} · {rate}" : rate;
    }

    internal static string FileState(MountUploadFile file) => file.State switch
    {
        MountUploadState.WaitingForClose => "Waiting for the application to close this file",
        MountUploadState.Uploading => file.TotalBytes is >= 0 && file.BytesTransferred >= file.TotalBytes
            ? "Waiting for server confirmation" : "Uploading",
        MountUploadState.Retrying => "Upload failed · waiting to retry",
        _ => "Queued for upload",
    };
}

internal sealed class UploadFileRow(string key) : NotifyBase
{
    internal string Key { get; } = key;
    private string _name = string.Empty;
    private string _detail = string.Empty;
    private string _progress = string.Empty;
    private double _percent;
    private bool _showProgress;
    private bool _totalKnown;
    public string Name => _name;
    public string Detail => _detail;
    public string ProgressText => _progress;
    public double Percent => _percent;
    public Visibility ProgressVisibility => _showProgress && _totalKnown ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressTextVisibility => _showProgress && _progress.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal void Update(string name, string detail, long? bytes, long? total, double? speed, bool showProgress)
    {
        var progress = UploadPresentation.Progress(bytes, total, speed);
        var percent = total is > 0 ? Math.Clamp(100d * (bytes ?? 0) / total.Value, 0, 100) : 0;
        var totalKnown = total is > 0;
        if (_name == name && _detail == detail && _progress == progress && _percent == percent &&
            _showProgress == showProgress && _totalKnown == totalKnown) return;
        _name = name;
        _detail = detail;
        _progress = progress;
        _percent = percent;
        _showProgress = showProgress;
        _totalKnown = totalKnown;
        Changed(string.Empty);
    }
}

internal sealed class UploadsViewModel : NotifyBase
{
    private string _summary = "Checking upload status…";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public ObservableCollection<UploadFileRow> Files { get; } = [];

    internal void Update(IReadOnlyList<MountRow> mounts, bool hostUnavailable,
        bool otherTransfersActive = false, bool powerProtectionUnavailable = false)
    {
        var rows = new List<(string Key, string Name, string Detail, long? Bytes, long? Total, double? Speed, bool ShowProgress)>();
        var unknown = hostUnavailable;
        long pending = 0;
        foreach (var mount in mounts)
        {
            var status = mount.UploadStatus;
            var changingMount = mount.IsTransient;
            if (!mount.ShouldStop && status?.UploadRecoveryRequired != true && !changingMount) continue;
            unknown |= status is null || status.UploadStatusStale || status.UploadStatusChecking || changingMount;
            if (status is null || status.UploadStatusStale || status.UploadStatusChecking || status.UploadRecoveryRequired || changingMount)
                rows.Add(($"{mount.Id}:status", mount.Name, status?.UploadStatusChecking == true
                    ? "Checking changed files. Keep the PC running while uploads are checked."
                    : changingMount ? "Checking drive state. Keep the PC running while the drive connects or disconnects."
                    : status is null || status.UploadStatusStale
                    ? "Upload status unavailable. Keep ResoDrive running until it reconnects."
                    : RcloneErrorMessage.Clean(status.Status,
                        "Cached files need recovery. Restore this drive to finish uploading."), null, null, null, false));
            if (status is null) continue;
            pending += Math.Max(status.UploadsQueued ?? 0, 0) + Math.Max(status.UploadsInProgress ?? 0, 0) + Math.Max(status.UploadsDirty ?? 0, 0);
            var files = status.Uploads ?? [];
            var visibleFiles = files.OrderBy(file => file.State == MountUploadState.Uploading ? 0 : 1).Take(5).ToArray();
            var fileStatusUnavailable = hostUnavailable || status.UploadStatusStale || status.UploadStatusChecking ||
                status.UploadRecoveryRequired || changingMount;
            foreach (var file in visibleFiles)
                rows.Add(($"file:{mount.Id}:{file.RelativePath}", $"{mount.Name} · {file.RelativePath}",
                    fileStatusUnavailable ? "Waiting for upload status" : UploadPresentation.FileState(file),
                    file.BytesTransferred, file.TotalBytes, fileStatusUnavailable ? null : file.SpeedBytesPerSecond,
                    !fileStatusUnavailable && file.State == MountUploadState.Uploading));
            if (status.UploadDetailsTruncated || files.Count > visibleFiles.Length)
                rows.Add(($"{mount.Id}:more", mount.Name, "More files pending", null, null, null, false));
            if (status.UploadErrors > 0 && !(status.Uploads ?? []).Any(file => file.State == MountUploadState.Retrying))
                rows.Add(($"{mount.Id}:errors", mount.Name, $"{status.UploadErrors} upload errors. Keep the cached files and check the connection.", null, null, null, false));
        }
        if (otherTransfersActive)
            rows.Add(("sync:active", "Sync jobs", "A configured sync job is still running. Open Sync to check its progress.", null, null, null, false));
        if (powerProtectionUnavailable)
            rows.Add(("power:unavailable", "Windows upload protection", "Windows could not apply all upload protection. Wait for uploads before shutting down.", null, null, null, false));
        var keys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in Files.Where(row => !keys.Contains(row.Key)).ToArray()) Files.Remove(obsolete);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var target = Files.FirstOrDefault(item => item.Key.Equals(row.Key, StringComparison.Ordinal));
            if (target is null) { target = new UploadFileRow(row.Key); Files.Insert(index, target); }
            else if (Files.IndexOf(target) != index) Files.Move(Files.IndexOf(target), index);
            target.Update(row.Name, row.Detail, row.Bytes, row.Total, row.Speed, row.ShowProgress);
        }
        Summary = unknown ? "Checking uploads…"
            : pending > 0 || otherTransfersActive ? "Uploads pending. Keep ResoDrive running."
            : powerProtectionUnavailable ? "Uploads checked. Windows power protection needs attention."
            : rows.Count > 0 ? "Some uploads need attention."
            : "Uploads complete. You can safely exit ResoDrive.";
    }
}

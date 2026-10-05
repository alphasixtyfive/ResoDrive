using ResoDrive.Core.Domain;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class UploadPresentation
{
    internal static bool HasPending(HostMountStatus? status) => status is not null &&
        (status.UploadsQueued > 0 || status.UploadsInProgress > 0 || status.UploadsDirty > 0 ||
         status.UploadErrors > 0 || status.UploadRecoveryRequired || status.Uploads?.Count > 0 ||
         status.UploadDetailsTruncated);

    internal static long PendingCount(IReadOnlyList<MountRow> mounts)
    {
        long total = 0;
        foreach (var mount in mounts)
        {
            var status = mount.UploadStatus;
            if (status is null) continue;
            Add(ObservedCount(status, MountUploadState.Queued, status.UploadsQueued));
            Add(ObservedCount(status, MountUploadState.Uploading, status.UploadsInProgress));
            Add(ObservedCount(status, MountUploadState.WaitingForClose, status.UploadsDirty));
        }
        return total;

        void Add(long? count)
        {
            if (count is > 0)
                total = count.Value > long.MaxValue - total ? long.MaxValue : total + count.Value;
        }
    }

    internal static string Activity(HostMountStatus? status)
    {
        if (status is null) return string.Empty;
        if (status.UploadStatusChecking) return HasPending(status) ? "Waiting for upload status" : string.Empty;
        if (status.UploadStatusStale) return "Upload status unavailable";
        var uploading = ObservedCount(status, MountUploadState.Uploading, status.UploadsInProgress);
        var queued = ObservedCount(status, MountUploadState.Queued, status.UploadsQueued);
        var dirty = ObservedCount(status, MountUploadState.WaitingForClose, status.UploadsDirty);
        var errors = Errors(status);
        return string.Join(" · ", new[]
        {
            uploading > 0 ? $"{uploading} uploading" : null,
            queued > 0 ? $"{queued} queued" : null,
            dirty > 0 ? $"{dirty} waiting for close" : null,
            errors > 0 ? ErrorCount(errors) : null,
            status.UploadRecoveryRequired ? "Cache recovery required" : null,
        }.Where(value => value is not null));
    }

    private static long ObservedCount(HostMountStatus status, MountUploadState state, long? aggregate) =>
        Math.Max(Math.Max(0, aggregate ?? 0), status.Uploads?.LongCount(file =>
            file.State == state || (state == MountUploadState.Queued && file.State == MountUploadState.Retrying)) ?? 0);

    internal static long Errors(HostMountStatus? status) => status is null ? 0
        : ObservedCount(status, MountUploadState.Retrying, status.UploadErrors);

    internal static string ErrorCount(long count) => $"{count} upload {(count == 1 ? "error" : "errors")}";

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

using ResoDrive.Core.Domain;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class UploadPresentation
{
    internal static bool HasPending(HostMountStatus? status) => status is not null &&
        (status.UploadsQueued > 0 || status.UploadsInProgress > 0 || status.UploadsDirty > 0 ||
         status.UploadErrors > 0 || status.UploadRecoveryRequired);

    internal static long PendingCount(IReadOnlyList<MountRow> mounts)
    {
        long total = 0;
        foreach (var mount in mounts)
        {
            var status = mount.UploadStatus;
            Add(status?.UploadsQueued);
            Add(status?.UploadsInProgress);
            Add(status?.UploadsDirty);
            if (status?.UploadStatusChecking == true || mount.IsTransient) Add(1);
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
        if (status.UploadStatusChecking) return "Checking uploads…";
        if (status.UploadStatusStale) return "Upload status unavailable";
        return string.Join(" · ", new[]
        {
            status.UploadsInProgress > 0 ? $"{status.UploadsInProgress} uploading" : null,
            status.UploadsQueued > 0 ? $"{status.UploadsQueued} queued" : null,
            status.UploadsDirty > 0 ? $"{status.UploadsDirty} waiting for close" : null,
            status.UploadErrors > 0 ? ErrorCount(status.UploadErrors.Value) : null,
            status.UploadRecoveryRequired ? "Cache recovery required" : null,
        }.Where(value => value is not null));
    }

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

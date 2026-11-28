using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class SyncProgressPresentation
{
    // Keep the host observation intact. Both the main row and flyout format a
    // display copy so unavailable measurements never become zero-byte progress.
    internal static HostSyncStatus Normalize(HostSyncStatus status) => status with
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
        ElapsedSeconds = DisplayableDuration(status.ElapsedSeconds),
    };

    private static long? Nonnegative(long? value) => value is >= 0 ? value : null;
    private static double? Nonnegative(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    private static double? DisplayableDuration(double? value) =>
        Nonnegative(value) is { } seconds && seconds < TimeSpan.MaxValue.TotalSeconds ? seconds : null;
}

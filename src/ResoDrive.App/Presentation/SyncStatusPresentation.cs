using System.Globalization;
using ResoDrive.Core.Domain;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed record SyncStatusText(string Primary, string Secondary);

internal static class SyncStatusPresentation
{
    internal static SyncStatusText Create(
        SyncMode? mode,
        bool enabled,
        bool busy,
        SyncLifecycle lifecycle,
        HostSyncStatus? status,
        bool recognized,
        bool transferStarted = false)
    {
        if (mode is null)
            return new("Invalid mode", string.Empty);
        if (!enabled && !busy)
            return new("Disabled", string.Empty);
        if (status is not null && !recognized)
            return new("Unknown state", string.Empty);
        if (status is not null)
            status = SyncProgressPresentation.Normalize(status);

        return lifecycle switch
        {
            SyncLifecycle.Queued => new("Waiting to start", string.Empty),
            SyncLifecycle.Running => new(RunningTitle(status), RunningDetail(status, transferStarted)),
            SyncLifecycle.Succeeded => new(
                string.Equals(status?.Status, "No changes", StringComparison.OrdinalIgnoreCase)
                    ? "Up to date"
                    : "Completed",
                CompletionDetail(status)),
            SyncLifecycle.Failed => new("Failed", FailureDetail(status)),
            SyncLifecycle.Cancelled => new("Cancelled", DisplayFormatting.Timestamp(status?.CompletedAt)),
            _ => new(string.Empty, string.Empty)
        };
    }

    private static string RunningTitle(HostSyncStatus? status) =>
        status?.ProgressPercent is { } progress
            ? $"Syncing {Math.Clamp(progress, 0d, 100d):0}%"
            : "Syncing";

    private static string RunningDetail(HostSyncStatus? status, bool transferStarted)
    {
        if (status is null)
            return "Preparing files";

        var parts = new List<string>(4);
        if (status.TotalTransfers is > 0)
            parts.Add($"{status.TransfersCompleted ?? 0} of {status.TotalTransfers} files");
        else if (status.TotalChecks is > 0)
            parts.Add($"{status.ChecksCompleted ?? 0} of {status.TotalChecks} checked");
        if (status.BytesTransferred is > 0)
            parts.Add(status.TotalBytes is > 0
                ? $"{DisplayFormatting.Bytes(status.BytesTransferred.Value)} of {DisplayFormatting.Bytes(status.TotalBytes.Value)}"
                : DisplayFormatting.Bytes(status.BytesTransferred.Value));
        if (status.SpeedBytesPerSecond is > 0)
            parts.Add($"{DisplayFormatting.Bytes(status.SpeedBytesPerSecond.Value)}/s");
        if (status.EtaSeconds is >= 1 and < 31_536_000)
            parts.Add($"{DisplayFormatting.Duration(status.EtaSeconds.Value)} left");
        return parts.Count > 0 ? string.Join(" · ", parts)
            : status.Errors > 0 ? "Waiting to retry automatically"
            : transferStarted ? "Waiting for transfer progress" : "Preparing files";
    }

    private static string CompletionDetail(HostSyncStatus? status)
    {
        if (status is null)
            return string.Empty;
        var parts = new List<string>(3);
        if (status.TransfersCompleted is > 0)
            parts.Add($"{status.TransfersCompleted} file{(status.TransfersCompleted == 1 ? string.Empty : "s")}");
        else if (status.ChecksCompleted is > 0)
            parts.Add($"{status.ChecksCompleted} checked");
        if (status.BytesTransferred is > 0)
            parts.Add($"{DisplayFormatting.Bytes(status.BytesTransferred.Value)} transferred");
        if (status.ElapsedSeconds is >= 1)
            parts.Add($"in {DisplayFormatting.Duration(status.ElapsedSeconds.Value)}");
        var completed = DisplayFormatting.Timestamp(status.CompletedAt);
        if (completed.Length > 0)
            parts.Add(completed);
        return string.Join(" · ", parts);
    }

    private static string FailureDetail(HostSyncStatus? status)
    {
        var message = status?.Status?.Trim();
        var completed = DisplayFormatting.Timestamp(status?.CompletedAt);
        if (string.IsNullOrWhiteSpace(message) || message.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            return completed;
        return completed.Length == 0 ? message : $"{message} · {completed}";
    }
}

internal static class DisplayFormatting
{
    internal static string Timestamp(DateTimeOffset? value) =>
        value is null ? string.Empty : Timestamp(value.Value.LocalDateTime);

    internal static string Timestamp(DateTime value)
    {
        var today = DateTime.Today;
        if (value.Date == today)
            return $"Today, {value:t}";
        if (value.Date == today.AddDays(-1))
            return $"Yesterday, {value:t}";
        return value.ToString("g", CultureInfo.CurrentCulture);
    }

    internal static string Bytes(double bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        var scaled = Math.Max(0d, bytes);
        var suffix = 0;
        while (scaled >= 1024d && suffix < suffixes.Length - 1)
        {
            scaled /= 1024d;
            suffix++;
        }
        var format = suffix == 0 || scaled >= 100 ? "0" : scaled >= 10 ? "0.0" : "0.00";
        return scaled.ToString(format, CultureInfo.CurrentCulture) + " " + suffixes[suffix];
    }

    internal static string Duration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        if (duration.TotalHours >= 1)
            return $"{Math.Ceiling(duration.TotalHours):0}h";
        if (duration.TotalMinutes >= 1)
            return $"{Math.Ceiling(duration.TotalMinutes):0}m";
        return $"{Math.Ceiling(duration.TotalSeconds):0}s";
    }
}

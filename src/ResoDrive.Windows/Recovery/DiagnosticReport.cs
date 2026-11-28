using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;

namespace ResoDrive.Windows;

public static partial class DiagnosticReport
{
    public static string Create(ManagerSettings settings, HostResponse status,
        string? applicationVersion, string? rcloneVersion, string? winFspVersion,
        IEnumerable<string> recentLogLines)
    {
        ArgumentNullException.ThrowIfNull(recentLogLines);
        // Compatibility entry point for callers that already hold a small excerpt.
        var lines = recentLogLines.Take(1001).ToArray();
        return Create(settings, status, applicationVersion, rcloneVersion, winFspVersion,
            new DiagnosticLogExcerpt(lines.Take(1000).ToArray(), Truncated: lines.Length > 1000),
            new DiagnosticLogExcerpt([], Unavailable: true));
    }

    public static string Create(ManagerSettings settings, HostResponse status,
        string? applicationVersion, string? rcloneVersion, string? winFspVersion,
        DiagnosticLogExcerpt uiLogs, DiagnosticLogExcerpt hostLogs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(uiLogs);
        ArgumentNullException.ThrowIfNull(hostLogs);
        var now = DateTimeOffset.UtcNow;
        var report = new StringBuilder();
        report.AppendLine("ResoDrive diagnostic report");
        report.AppendLine("Contains versions, numeric performance options, health, states and event references.");
        report.AppendLine("Names, addresses, paths, credentials and raw log messages are omitted.");
        report.AppendLine("Review this report before sharing it.");
        report.AppendLine(CultureInfo.InvariantCulture, $"Created (UTC): {now:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"ResoDrive: {VersionText(applicationVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Source commit: {DiagnosticIdentity.CommitText(applicationVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture, $".NET runtime: {DiagnosticIdentity.RuntimeText}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Process architecture: {DiagnosticIdentity.ProcessArchitecture}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Windows architecture: {DiagnosticIdentity.OperatingSystemArchitecture}");
        AppendUiProcessContext(report, now);
        report.AppendLine(CultureInfo.InvariantCulture, $"rclone: {VersionText(rcloneVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"rclone in host identity: {(status.Succeeded ? VersionText(status.ReportedRcloneVersion) : "Unknown")}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Host rclone identity check: {IdentityCheckText(status)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"WinFsp: {VersionText(winFspVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.Version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Host responded: {status.Succeeded}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Host response category: {HostResponseCategory(status)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Host initialization failed: {KnownBoolean(status.Succeeded, status.InitializationErrorCode is not null || status.InitializationErrorMessage is not null)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Host power protection error present: {KnownBoolean(status.Succeeded, status.SessionProtectionError is not null)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Active sync jobs: {(status.Succeeded ? Number(status.ActiveSyncJobs) : "Unknown")}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Host status truncated: mounts={status.MountStatusTruncated}, sync={status.SyncStatusTruncated}");

        // Bound both matching work and output, including malformed in-memory inputs.
        var mountStatusKnown = status.Succeeded && status.Mounts is not null;
        var syncStatusKnown = status.Succeeded && status.SyncJobs is not null;
        var mounts = mountStatusKnown ? status.Mounts!.Take(1024).ToArray() : [];
        var syncs = syncStatusKnown ? status.SyncJobs!.Take(1024).ToArray() : [];
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Status input limit reached: mounts={status.Mounts?.Count > mounts.Length && status.Succeeded}, sync={status.SyncJobs?.Count > syncs.Length && status.Succeeded}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Recovery required in reported mounts: {(mountStatusKnown ? mounts.Count(item => item.UploadRecoveryRequired).ToString(CultureInfo.InvariantCulture) : "Unknown")}");
        var index = 0;
        foreach (var mount in settings.Mounts.Take(128))
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"Drive {++index}: enabled={mount.Enabled}, networkMode={RcloneMountOptions.HasOption(mount.Arguments, "--network-mode")}");
            var state = mounts.FirstOrDefault(item => item.MountId == mount.Id);
            report.AppendLine(CultureInfo.InvariantCulture, $"State: {Lifecycle<MountLifecycle>(state?.Lifecycle)}");
            AppendUploads(report, state, now);
            var options = new RcloneMountOptions(mount.Arguments);
            report.AppendLine(CultureInfo.InvariantCulture, $"Cache mode: {(options.CacheMode is "off" or "minimal" or "writes" or "full" ? options.CacheMode : "Unknown")}");
            foreach (var name in NumericOptions)
            {
                var value = RcloneMountOptions.Value(mount.Arguments, name);
                if (value is not null)
                    report.AppendLine(CultureInfo.InvariantCulture, $"{name}={(value.Length <= 64 && NumericValue().IsMatch(value) ? value : "<omitted>")}");
            }
            report.AppendLine(CultureInfo.InvariantCulture, $"Sync jobs: {mount.SyncJobs.Length}");
            var driveSyncs = syncs.Where(job => job.MountId == mount.Id).ToArray();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"Failed sync jobs: {(syncStatusKnown ? driveSyncs.Count(job => job.Lifecycle == nameof(SyncLifecycle.Failed)).ToString(CultureInfo.InvariantCulture) : "Unknown")}");
            var syncIndex = 0;
            foreach (var sync in driveSyncs.Take(16))
                AppendSync(report, ++syncIndex, sync, now);
            report.AppendLine(CultureInfo.InvariantCulture, $"Sync detail limit reached: {driveSyncs.Length > 16}");
        }
        report.AppendLine(CultureInfo.InvariantCulture, $"Drive detail limit reached: {settings.Mounts.Count > 128}");
        AppendEvents(report, "UI", uiLogs);
        AppendEvents(report, "Host", hostLogs);
        return report.ToString();
    }

    private static void AppendUiProcessContext(StringBuilder report, DateTimeOffset now)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"UI logical processors: {Environment.ProcessorCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"UI managed memory bytes: {Number(GC.GetTotalMemory(forceFullCollection: false))}");
        string uptime = "Unknown", workingSet = "Unknown";
        try
        {
            using var process = Process.GetCurrentProcess();
            uptime = Age(process.StartTime.ToUniversalTime(), now);
            workingSet = Number(process.WorkingSet64);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
            InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Resource counters are optional; a restricted process still exports health.
        }
        report.AppendLine(CultureInfo.InvariantCulture, $"UI uptime seconds: {uptime}");
        report.AppendLine(CultureInfo.InvariantCulture, $"UI working set bytes: {workingSet}");
    }

    private static void AppendUploads(StringBuilder report, HostMountStatus? state, DateTimeOffset now)
    {
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Uploads: queued={Number(state?.UploadsQueued)}, active={Number(state?.UploadsInProgress)}, open={Number(state?.UploadsDirty)}, errors={Number(state?.UploadErrors)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Upload observation: checking={KnownBoolean(state is not null, state?.UploadStatusChecking == true)}, stale={KnownBoolean(state is not null, state?.UploadStatusStale == true)}, recoveryRequired={KnownBoolean(state is not null, state?.UploadRecoveryRequired == true)}, ageSeconds={Age(state?.UploadObservedAt, now)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Upload transfer: remainingBytes={Number(state?.UploadBytesRemaining)}, bytesPerSecond={Number(state?.UploadSpeedBytesPerSecond)}, etaSeconds={Number(state?.UploadEtaSeconds)}, detailsTruncated={KnownBoolean(state is not null, state?.UploadDetailsTruncated == true)}");
    }

    private static void AppendSync(StringBuilder report, int index, HostSyncStatus state, DateTimeOffset now)
    {
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Sync {index}: state={Lifecycle<SyncLifecycle>(state.Lifecycle)}, completedAgeSeconds={Age(state.CompletedAt, now)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  bytes={Number(state.BytesTransferred)}, totalBytes={Number(state.TotalBytes)}, progressPercent={Number(state.ProgressPercent is <= 100 ? state.ProgressPercent : null)}, checks={Number(state.ChecksCompleted)}, totalChecks={Number(state.TotalChecks)}, transfers={Number(state.TransfersCompleted)}, totalTransfers={Number(state.TotalTransfers)}, errors={Number(state.Errors)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  bytesPerSecond={Number(state.SpeedBytesPerSecond)}, etaSeconds={Number(state.EtaSeconds)}, elapsedSeconds={Number(state.ElapsedSeconds)}");
    }

    private static string Lifecycle<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var lifecycle) && Enum.IsDefined(lifecycle) ? lifecycle.ToString() : "Unknown";

    private static string KnownBoolean(bool known, bool value) => known ? value.ToString() : "Unknown";
    private static string Number(long? value) => value is >= 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : "Unknown";
    private static string Number(double? value) => value is >= 0 && double.IsFinite(value.Value)
        ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "Unknown";
    private static string Age(DateTimeOffset? observed, DateTimeOffset now) =>
        observed.HasValue ? Number((now - observed.Value).TotalSeconds) : "Unknown";

    private static string HostResponseCategory(HostResponse status) => status.Succeeded ? "Available" : status.ErrorCode switch
    {
        "host.unavailable" => "Unavailable",
        "host.response_timeout" => "ResponseTimeout",
        "host.connection_lost" => "ConnectionLost",
        "host.access_denied" => "AccessDenied",
        "host.identity_unavailable" => "IdentityUnavailable",
        "host.different_installation" => "DifferentInstallation",
        "host.invalid_response" => "InvalidResponse",
        _ => "Unknown"
    };

    private static string VersionText(string? value) =>
        value is { Length: <= 160 } && ReportVersion().IsMatch(value)
            ? DiagnosticIdentity.VersionText(value) : "Unknown";

    private static string IdentityCheckText(HostResponse status) =>
        !status.Succeeded ? "Unavailable" : status.RcloneIdentityErrorCode switch
        {
            "rclone.not_installed" or "rclone.version_timeout" or "rclone.invalid" or
                "rclone.version_format" => status.RcloneIdentityErrorCode,
            null when status.ReportedRcloneVersion is not null => "Verified",
            _ => "Unknown"
        };

    private static readonly string[] NumericOptions = [
        "--vfs-cache-max-size", "--vfs-cache-max-age", "--buffer-size", "--vfs-read-ahead",
        "--vfs-read-chunk-size", "--vfs-read-chunk-size-limit", "--vfs-read-chunk-streams",
        "--dir-cache-time", "--poll-interval", "--transfers", "--checkers", "--timeout",
        "--contimeout", "--vfs-write-back", "--retries", "--low-level-retries"
    ];

    [GeneratedRegex(@"\A(?:off|\d+(?:\.\d+)?(?:[kKmMgGtTpP](?:i?[bB])?|ms|s|m|h|d)?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericValue();
    [GeneratedRegex(@"\Av?\d{1,5}\.\d{1,5}(?:\.\d{1,10}){0,2}(?:-(?:alpha|beta|rc|dev|preview)(?:[.-]?\d{1,10})*)?(?:\+[A-Fa-f0-9]{7,64})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReportVersion();
}

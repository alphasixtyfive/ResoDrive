using System.Globalization;
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
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(recentLogLines);
        var report = new StringBuilder();
        report.AppendLine("ResoDrive diagnostic report");
        report.AppendLine("Contains versions, numeric performance options, states and error references.");
        report.AppendLine("Names, addresses, paths, credentials and raw log messages are omitted.");
        report.AppendLine("Review this report before sharing it.");
        report.AppendLine(CultureInfo.InvariantCulture, $"Created (UTC): {DateTimeOffset.UtcNow:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"ResoDrive: {VersionText(applicationVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"rclone: {VersionText(rcloneVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"rclone in host identity: {(status.Succeeded ? VersionText(status.ReportedRcloneVersion) : "Unknown")}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Host rclone identity check: {IdentityCheckText(status)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"WinFsp: {VersionText(winFspVersion)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.Version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Host responded: {status.Succeeded}");
        var index = 0;
        foreach (var mount in settings.Mounts)
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"Drive {++index}: enabled={mount.Enabled}, networkMode={RcloneMountOptions.HasOption(mount.Arguments, "--network-mode")}");
            var state = status.Mounts?.FirstOrDefault(item => item.MountId == mount.Id);
            report.AppendLine(CultureInfo.InvariantCulture, $"State: {(Enum.TryParse<MountLifecycle>(state?.Lifecycle, out var lifecycle) && Enum.IsDefined(lifecycle) ? lifecycle.ToString() : "Unknown")}");
            var options = new RcloneMountOptions(mount.Arguments);
            report.AppendLine(CultureInfo.InvariantCulture, $"Cache mode: {(options.CacheMode is "off" or "minimal" or "writes" or "full" ? options.CacheMode : "Unknown")}");
            foreach (var name in NumericOptions)
            {
                var value = RcloneMountOptions.Value(mount.Arguments, name);
                if (value is not null)
                    report.AppendLine(CultureInfo.InvariantCulture, $"{name}={(NumericValue().IsMatch(value) ? value : "<omitted>")}");
            }
            report.AppendLine(CultureInfo.InvariantCulture, $"Sync jobs: {mount.SyncJobs.Length}");
            report.AppendLine(CultureInfo.InvariantCulture, $"Failed sync jobs: {status.SyncJobs?.Count(job => job.MountId == mount.Id && job.Lifecycle == nameof(SyncLifecycle.Failed)) ?? 0}");
        }
        report.AppendLine();
        report.AppendLine("Recent UI errors (references only; details remain on this PC):");
        foreach (var line in recentLogLines.TakeLast(1000))
        {
            var match = ErrorReference().Match(line);
            if (match.Success && DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
                report.AppendLine(CultureInfo.InvariantCulture, $"{time:O} errorId={match.Groups[2].Value}");
        }
        return report.ToString();
    }

    private static string VersionText(string? value) =>
        value is not null && VersionPattern().IsMatch(value) ? value : "Unknown";

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
    [GeneratedRegex(@"\Av?\d+\.\d+(?:\.\d+){0,2}(?:-[A-Za-z0-9][A-Za-z0-9._-]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A(\d{4}-\d{2}-\d{2}T[\d:.+Z-]+) level=ERROR event=[a-z._]+ errorId=([A-F0-9]{8})(?: |$)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorReference();
}

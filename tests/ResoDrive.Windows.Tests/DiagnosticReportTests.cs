using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class DiagnosticReportTests
{
    [Fact]
    public void ExportsOnlyAllowlistedInformationEvenWhenInputsContainSecrets()
    {
        var id = Guid.NewGuid();
        var settings = new ManagerSettings
        {
            Mounts = [new MountSettings
            {
                Id = id, DisplayName = "private name", RemoteName = "private remote", ConnectionHost = "private.example.com",
                RemotePath = "private-folder", Arguments = ["--vfs-cache-mode=full", "--buffer-size=16M",
                    "--vfs-read-ahead=SECRET", "--password=SECRET", "--log-file=C:\\private\\file.log"]
            }]
        };
        var report = DiagnosticReport.Create(settings,
            new HostResponse(true, Mounts: [new(id, "Degraded", "SECRET error private.example.com")]),
            "0.3.7", "v1.75.0", "2.1.0",
            ["2026-09-19T12:00:00.0000000+00:00 level=ERROR event=startup.failed errorId=ABCDEF12 detail=\"SECRET C:\\private\\file\""]);
        Assert.Contains("--buffer-size=16M", report, StringComparison.Ordinal);
        Assert.Contains("State: Degraded", report, StringComparison.Ordinal);
        Assert.Contains("rclone in host identity: Unknown", report, StringComparison.Ordinal);
        Assert.Contains("Host rclone identity check: Unknown", report, StringComparison.Ordinal);
        Assert.Contains("errorId=ABCDEF12", report, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", report, StringComparison.Ordinal);
        Assert.DoesNotContain("private", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(id.ToString(), report, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparatesTheUiComponentVersionFromTheHostsReportedVersion()
    {
        var report = DiagnosticReport.Create(new ManagerSettings(),
            new HostResponse(true, ReportedRcloneVersion: null, RcloneIdentityErrorCode: "rclone.version_timeout"),
            "0.3.14", "v1.75.1", "2.1.0", []);

        Assert.Contains("rclone: v1.75.1", report, StringComparison.Ordinal);
        Assert.Contains("rclone in host identity: Unknown", report, StringComparison.Ordinal);
        Assert.Contains("Host rclone identity check: rclone.version_timeout", report, StringComparison.Ordinal);
    }
}

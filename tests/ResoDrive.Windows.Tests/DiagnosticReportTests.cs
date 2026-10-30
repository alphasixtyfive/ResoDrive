using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class DiagnosticReportTests
{
    [Fact]
    public void CapturesHealthRecoveryTransfersAndRuntimeHistoryWithoutPrivateInputs()
    {
        var mountId = Guid.NewGuid();
        var syncId = Guid.NewGuid();
        var settings = new ManagerSettings
        {
            Mounts = [new MountSettings
            {
                Id = mountId, DisplayName = "PRIVATE_ACCOUNT", RemoteName = "PRIVATE_REMOTE",
                ConnectionHost = "PRIVATE_HOST", Arguments = ["--vfs-cache-mode=full"],
                SyncJobs = [new SyncJobSettings { Id = syncId, DisplayName = "PRIVATE_JOB", LocalPath = "C:\\PRIVATE_FOLDER" }]
            }]
        };
        var status = new HostResponse(true,
            Mounts: [new(mountId, "WaitingForConnection", "PRIVATE_REASON", 3, 0, true)
            {
                UploadsDirty = 1, UploadErrors = 2, UploadStatusChecking = true,
                UploadObservedAt = DateTimeOffset.UtcNow.AddSeconds(-30), UploadBytesRemaining = 4096,
                UploadSpeedBytesPerSecond = 1024.5, UploadEtaSeconds = 4,
                UploadDetailsTruncated = true, UploadRecoveryRequired = false
            }],
            SyncJobs: [new(mountId, syncId, "Failed", "PRIVATE_SYNC", DateTimeOffset.UtcNow.AddMinutes(-5),
                BytesTransferred: 8192, TotalBytes: 16384, ProgressPercent: 50, Errors: 1)],
            ReportedRcloneVersion: "v1.75.1", SessionProtectionError: "PRIVATE_POWER_ERROR",
            ActiveSyncJobs: 1, MountStatusTruncated: true);
        var ui = new DiagnosticLogExcerpt([
            Record("process.started", "version=0.3.45+abcdef1 commit=abcdef1 runtime=10.0.13 processArchitecture=X64 osArchitecture=X64 windows=10.0.26200.0"),
            Record("startup.fatal", "exceptionType=System.IO.FileNotFoundException hresult=0x80070002 message=PRIVATE_EXCEPTION frame=PRIVATE_METHOD", "ERROR", "ABCDEF12"),
            Record("process.started", "version=0.3.46+abcdef2 commit=abcdef2 runtime=10.0.14 processArchitecture=X64 osArchitecture=X64 windows=10.0.26200.0")
        ], Truncated: true);
        var host = new DiagnosticLogExcerpt([
            Record("process.started", "version=0.3.45+abcdef1 commit=abcdef1 runtime=10.0.13 processArchitecture=X64 osArchitecture=X64 windows=10.0.26200.0"),
            Record("host.worker.scheduler_io_failed", "PRIVATE_PATH", "WARN"),
            Record("host.exception", "exceptionType=System.IO.IOException hresult=0x80070020 message=PRIVATE_HOST_MESSAGE", "ERROR", "1234ABCD")
        ]);

        var report = DiagnosticReport.Create(settings, status, "0.3.46+abcdef2", "v1.75.1", "2.1.0", ui, host);

        Assert.Contains("Host power protection error present: True", report, StringComparison.Ordinal);
        Assert.Contains("Active sync jobs: 1", report, StringComparison.Ordinal);
        Assert.Contains("Host status truncated: mounts=True, sync=False", report, StringComparison.Ordinal);
        Assert.Contains("State: WaitingForConnection", report, StringComparison.Ordinal);
        Assert.Contains("queued=3, active=0, open=1, errors=2", report, StringComparison.Ordinal);
        Assert.Contains("checking=True, stale=True, recoveryRequired=False, ageSeconds=", report, StringComparison.Ordinal);
        Assert.Contains("remainingBytes=4096, bytesPerSecond=1024.5, etaSeconds=4", report, StringComparison.Ordinal);
        Assert.Contains("Sync 1: state=Failed", report, StringComparison.Ordinal);
        Assert.Contains("bytes=8192, totalBytes=16384, progressPercent=50", report, StringComparison.Ordinal);
        Assert.Contains("runtime=10.0.13", report, StringComparison.Ordinal);
        Assert.Contains("runtime=10.0.14", report, StringComparison.Ordinal);
        Assert.Contains("exception=FileNotFound hresult=0x80070002", report, StringComparison.Ordinal);
        Assert.Contains("event=host.worker.scheduler_io_failed", report, StringComparison.Ordinal);
        Assert.Contains("errorId=1234ABCD", report, StringComparison.Ordinal);
        Assert.Contains("excerpt truncated: True", report, StringComparison.Ordinal);
        Assert.Contains("UI logical processors:", report, StringComparison.Ordinal);
        Assert.Contains("UI uptime seconds:", report, StringComparison.Ordinal);
        Assert.Contains("UI working set bytes:", report, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", report, StringComparison.Ordinal);
        Assert.DoesNotContain(mountId.ToString(), report, StringComparison.Ordinal);
        Assert.DoesNotContain(syncId.ToString(), report, StringComparison.Ordinal);
        Assert.DoesNotContain("pid=", report, StringComparison.Ordinal);
        Assert.DoesNotContain("session=", report, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.FileNotFoundException", report, StringComparison.Ordinal);

        // Optional review artifact contains only the same synthetic, allowlisted report.
        var capturePath = Environment.GetEnvironmentVariable("RESODRIVE_DIAGNOSTIC_CAPTURE_PATH");
        if (!string.IsNullOrEmpty(capturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            File.WriteAllText(capturePath, report);
        }
    }

    [Fact]
    public void RejectsHostileEventMetadataRatherThanExportingArbitraryIdentifiers()
    {
        var logs = new DiagnosticLogExcerpt([
            Record("process.started", "version=1.2.3-PRIVATE_SUFFIX commit=PRIVATE_COMMIT runtime=PRIVATE_RUNTIME processArchitecture=PRIVATE_CPU osArchitecture=PRIVATE_OS windows=PRIVATE_WINDOWS"),
            Record("ui.action.failed", "exceptionType=PRIVATE.Customer.Exception hresult=0x80004005 message=PRIVATE_MESSAGE frame=PRIVATE_METHOD", "ERROR", "ABCDEF12"),
            Record("private.customer", "PRIVATE_OTHER_MESSAGE", "ERROR", "99887766"),
            Record("startup.failed", "message=exceptionType=System.IO.FileNotFoundException hresult=0x80070002 PRIVATE_FAKE_METADATA", "ERROR", "11223344")
        ]);
        var report = DiagnosticReport.Create(new ManagerSettings(),
            new HostResponse(false, "PRIVATE_CODE", "PRIVATE_ERROR", InitializationErrorCode: "PRIVATE_INIT"),
            "1.2.3-PRIVATE_VERSION", null, null, logs, new DiagnosticLogExcerpt([]));

        Assert.Contains("Host response category: Unknown", report, StringComparison.Ordinal);
        Assert.Contains("version=Unknown commit=Unknown runtime=Unknown processArchitecture=Unknown osArchitecture=Unknown windows=Unknown", report, StringComparison.Ordinal);
        Assert.Contains("exception=Other hresult=0x80004005", report, StringComparison.Ordinal);
        Assert.Contains("errorId=11223344", report, StringComparison.Ordinal);
        Assert.DoesNotContain("exception=FileNotFound", report, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", report, StringComparison.Ordinal);
        Assert.DoesNotContain("private.customer", report, StringComparison.Ordinal);
        Assert.DoesNotContain("99887766", report, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidAndUnavailableTelemetryRemainsUnknown()
    {
        var id = Guid.NewGuid();
        var settings = new ManagerSettings { Mounts = [new MountSettings { Id = id, DisplayName = "drive", RemoteName = "remote" }] };
        var status = new HostResponse(true,
            Mounts: [new(id, "Mounted", "ignored", -1, -2)
            {
                UploadsDirty = -3, UploadErrors = -4, UploadBytesRemaining = -5,
                UploadSpeedBytesPerSecond = double.NaN, UploadEtaSeconds = double.PositiveInfinity,
                UploadObservedAt = DateTimeOffset.UtcNow.AddDays(1)
            }],
            SyncJobs: [new(id, Guid.NewGuid(), "Running", "ignored", null,
                BytesTransferred: -1, TotalBytes: -2, ProgressPercent: 101, Errors: -1,
                SpeedBytesPerSecond: double.NegativeInfinity, EtaSeconds: double.NaN, ElapsedSeconds: -5)], ActiveSyncJobs: -1);
        var report = DiagnosticReport.Create(settings, status, null, null, null, []);

        Assert.Contains("Active sync jobs: Unknown", report, StringComparison.Ordinal);
        Assert.Contains("queued=Unknown, active=Unknown, open=Unknown, errors=Unknown", report, StringComparison.Ordinal);
        Assert.Contains("ageSeconds=Unknown", report, StringComparison.Ordinal);
        Assert.Contains("remainingBytes=Unknown, bytesPerSecond=Unknown, etaSeconds=Unknown", report, StringComparison.Ordinal);
        Assert.Contains("bytes=Unknown, totalBytes=Unknown, progressPercent=Unknown", report, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", report, StringComparison.Ordinal);

        var unavailable = DiagnosticReport.Create(settings, status with { Succeeded = false, ErrorCode = "host.response_timeout" }, null, null, null, []);
        Assert.Contains("Host response category: ResponseTimeout", unavailable, StringComparison.Ordinal);
        Assert.Contains("State: Unknown", unavailable, StringComparison.Ordinal);
        Assert.Contains("checking=Unknown, stale=Unknown, recoveryRequired=Unknown", unavailable, StringComparison.Ordinal);
        Assert.Contains("Failed sync jobs: Unknown", unavailable, StringComparison.Ordinal);
        Assert.DoesNotContain("Sync 1:", unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsEventOutputAndKeepsRolesSeparate()
    {
        var logs = new DiagnosticLogExcerpt(Enumerable.Repeat(Record("ui.action.failed", "ignored", "ERROR", "ABCDEF12"), 1200).ToArray());
        var report = DiagnosticReport.Create(new ManagerSettings(), new HostResponse(true), null, null, null,
            logs, new DiagnosticLogExcerpt([Record("ui.action.failed", "ignored", "ERROR", "12345678")], Unavailable: true));

        Assert.Contains("Events shown: 80; event limit reached: True", report, StringComparison.Ordinal);
        Assert.Contains("Sources incomplete or unavailable: True", report, StringComparison.Ordinal);
        Assert.Contains("excerpt truncated: True", report, StringComparison.Ordinal);
        Assert.Equal(80, report.Split("errorId=ABCDEF12", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("12345678", report, StringComparison.Ordinal);
        Assert.True(report.Length < 25_000);
    }

    [Fact]
    public void RuntimeHistorySurvivesLaterWarningBurstsWithinTheEventLimit()
    {
        var startup = Record("process.started", "version=0.3.45+abcdef1 commit=abcdef1 runtime=10.0.13 processArchitecture=X64 osArchitecture=X64 windows=10.0.26200.0");
        var warnings = Enumerable.Repeat(Record("tray.action_failed", "PRIVATE_MESSAGE", "WARN"), 100);
        var report = DiagnosticReport.Create(new ManagerSettings(), new HostResponse(true), null, null, null,
            new DiagnosticLogExcerpt(new[] { startup }.Concat(warnings).ToArray()), new DiagnosticLogExcerpt([]));

        Assert.Contains("runtime=10.0.13", report, StringComparison.Ordinal);
        Assert.Contains("Events shown: 80; event limit reached: True; events omitted: 21", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("event=process.started", StringComparison.Ordinal) < report.IndexOf("event=tray.action_failed", StringComparison.Ordinal));
        Assert.Equal(79, report.Split("event=tray.action_failed", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("PRIVATE", report, StringComparison.Ordinal);
    }

    private static string Record(string eventName, string detail, string level = "INFO", string? errorId = null) =>
        $"2026-10-30T09:07:00.0000000+00:00 level={level} event={eventName}{(errorId is null ? "" : " errorId=" + errorId)} pid=123 session=0123456789abcdef0123456789abcdef detail=\"{detail}\"";

    [Theory]
    [InlineData("0.3.31+0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0.3.31+abcdef1", "abcdef1")]
    public void PreservesValidatedSourceIdentity(string version, string commit)
    {
        var report = DiagnosticReport.Create(new ManagerSettings(), new HostResponse(true), version, null, null, []);
        Assert.Contains($"ResoDrive: {version}", report, StringComparison.Ordinal);
        Assert.Contains($"Source commit: {commit}", report, StringComparison.Ordinal);
        Assert.Contains($".NET runtime: {Environment.Version}", report, StringComparison.Ordinal);
        Assert.Contains("Process architecture:", report, StringComparison.Ordinal);
        Assert.Contains("Windows architecture:", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.3.31+token-private-value")]
    [InlineData("0.3.31+abcdef1\nsecret")]
    [InlineData("0.3.31+abcdef")]
    public void RejectsUnvalidatedBuildMetadata(string version)
    {
        var report = DiagnosticReport.Create(new ManagerSettings(), new HostResponse(true), version, null, null, []);
        Assert.Contains("ResoDrive: Unknown", report, StringComparison.Ordinal);
        Assert.Contains("Source commit: Unknown", report, StringComparison.Ordinal);
        Assert.DoesNotContain(version, report, StringComparison.Ordinal);
    }

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

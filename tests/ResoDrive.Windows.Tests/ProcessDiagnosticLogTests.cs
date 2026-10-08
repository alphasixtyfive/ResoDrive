using System.Diagnostics;

namespace ResoDrive.Windows.Tests;

public sealed class ProcessDiagnosticLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-crash-log-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StartupRetainsExactBuildAndRuntimeMetadata()
    {
        var log = new ProcessDiagnosticLog(new ApplicationPaths(_root), "ui");
        log.StartSession("0.3.31+0123456789abcdef0123456789abcdef01234567");
        var contents = File.ReadAllText(log.LogFile);
        Assert.Contains("version=0.3.31+0123456789abcdef0123456789abcdef01234567", contents, StringComparison.Ordinal);
        Assert.Contains($"runtime={Environment.Version}", contents, StringComparison.Ordinal);
        Assert.Contains($"windows={Environment.OSVersion.Version}", contents, StringComparison.Ordinal);
        Assert.Contains($"session={log.SessionId}", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalExceptionsHaveReferencesAndSanitizedStacksButDoNotEnterSharedReport()
    {
        var log = new ProcessDiagnosticLog(new ApplicationPaths(_root), "ui");
        var id = log.Exception("startup.failed", CaptureFailure());
        var contents = File.ReadAllText(log.LogFile);
        Assert.Contains($"errorId={id}", contents, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", contents, StringComparison.Ordinal);
        Assert.Contains("frame=ResoDrive.Windows.Tests.ProcessDiagnosticLogTests.CaptureFailure", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", contents, StringComparison.Ordinal);
        var report = DiagnosticReport.Create(new Core.Settings.ManagerSettings(), new HostResponse(true),
            "0.3.31", null, null, File.ReadLines(log.LogFile));
        Assert.Contains($"errorId={id}", report, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", report, StringComparison.Ordinal);
        Assert.DoesNotContain("<redacted>", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveAndUnreadableWipeMarkersFailClosedWithoutRecreatingLogs()
    {
        var paths = new ApplicationPaths(_root);
        var oldLog = new ProcessDiagnosticLog(paths, "ui");
        Assert.True(oldLog.Information("before.wipe"));
        var state = new RemoteWipeState(Guid.NewGuid(), RemoteWipePhase.Requested,
            new RemoteWipeRegistration(Guid.NewGuid(), "https://example.test/dav", "https://example.test/", "user", "token"));
        await new RemoteWipeStateStore(paths).SaveAsync(state);
        RemoteWipeCleanup.DeleteAccountData(paths);
        Assert.False(oldLog.Information("after.wipe"));
        Assert.False(new ProcessDiagnosticLog(paths, "host").Information("during.wipe"));
        Assert.Empty(Directory.GetFiles(paths.Logs));

        File.WriteAllText(paths.RemoteWipeStateFile, "unreadable-protected-state");
        Assert.False(new ProcessDiagnosticLog(paths, "host").Information("unreadable.wipe"));
        Assert.Empty(Directory.GetFiles(paths.Logs));
    }

    [Fact]
    public async Task OldGenerationCannotResumeLoggingAfterWipeCompletes()
    {
        var paths = new ApplicationPaths(_root);
        var log = new ProcessDiagnosticLog(paths, "ui");
        paths.EnsureCreated();
        await new RemoteWipeStateStore(paths).SaveAsync(new(Guid.NewGuid(), RemoteWipePhase.Completed, null));
        Assert.False(log.Information("old.session"));
        Assert.True(new ProcessDiagnosticLog(paths, "ui").Information("new.session"));
    }

    [Fact]
    public void LockedAccountLeaseDropsDiagnosticsPromptly()
    {
        var paths = new ApplicationPaths(_root);
        paths.EnsureCreated();
        var log = new ProcessDiagnosticLog(paths, "host");
        using var held = new FileStream(Path.Combine(paths.Root, ".account-data.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var elapsed = Stopwatch.StartNew();
        Assert.False(log.Information("lease.blocked"));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(log.LogFile));
    }

    [Theory]
    [InlineData(@"\\192.0.2.1\unreachable-share\resodrive-test")]
    [InlineData(@"\\?\UNC\192.0.2.1\unreachable-share\resodrive-test")]
    [InlineData(@"\\.\UNC\192.0.2.1\unreachable-share\resodrive-test")]
    public void OfflineNetworkDestinationsAreRefusedBeforeGuardOrFileAccess(string root)
    {
        // TEST-NET-1 is not a real share; no filesystem availability probes belong
        // here. Include construction: the account guard itself reads a wipe marker.
        var elapsed = Stopwatch.StartNew();
        var paths = new ApplicationPaths(root);
        var accountLog = new ProcessDiagnosticLog(paths, "host");
        Assert.False(accountLog.Information("network.refused"));
        accountLog.StartSession();
        Assert.Matches("^[A-F0-9]{8}$", accountLog.Exception("fatal.failed", new BrokenException()));
        Assert.False(new ProcessDiagnosticLog(paths.UiLogFile).Information("network.refused"));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Remote diagnostics took {elapsed.Elapsed}.");
    }

    [Fact]
    public void LocalDeviceNamespaceIsRefusedWithoutWriting()
    {
        var deviceRoot = @"\\?\" + _root;
        var paths = new ApplicationPaths(deviceRoot);
        Assert.False(new ProcessDiagnosticLog(paths, "ui").Information("device.refused"));
        Assert.False(new ProcessDiagnosticLog(paths.UiLogFile).Information("device.refused"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AncestorJunctionIsRefusedBeforeAccessingItsDescendants()
    {
        var outside = _root + "-outside";
        var junction = Path.Combine(_root, "redirected");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, outside })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);

            var elapsed = Stopwatch.StartNew();
            var paths = new ApplicationPaths(Path.Combine(junction, "account"));
            Assert.False(new ProcessDiagnosticLog(paths, "host").Information("junction.refused"));
            Assert.False(new ProcessDiagnosticLog(paths.UiLogFile).Information("junction.refused"));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
            Assert.Empty(Directory.GetDirectories(outside));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task MissingLogDirectoryStillRejectsRedirectedAccountLease()
    {
        var outside = _root + "-outside";
        var paths = new ApplicationPaths(_root);
        var redirectedLease = Path.Combine(paths.Root, ".account-data.lock");
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", redirectedLease, outside })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.False(Directory.Exists(paths.Logs));

            var log = new ProcessDiagnosticLog(paths, "host");
            // Repair the bad lock after construction. A logger whose account guard
            // could not safely initialize must remain disabled for that session.
            Directory.Delete(redirectedLease);
            Assert.False(log.Information("redirected.lease"));
            Assert.False(File.Exists(log.LogFile));
            Assert.False(File.Exists(redirectedLease));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.Single(Directory.GetFiles(outside));
            Assert.True(new ProcessDiagnosticLog(paths, "host").Information("safe.lease"));
        }
        finally
        {
            if (Directory.Exists(redirectedLease)) Directory.Delete(redirectedLease);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void RotationAndOversizedEventsRemainWithinTwoBoundedFiles()
    {
        var log = new ProcessDiagnosticLog(new ApplicationPaths(_root), "host", maximumBytes: 512);
        for (var index = 0; index < 20; index++)
            Assert.True(log.Information("rotation.test", new string('X', 50_000)));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(log.LogFile)!).Length);
        Assert.InRange(new FileInfo(log.LogFile).Length, 1, 512);
        Assert.InRange(new FileInfo(log.LogFile + ".1").Length, 1, 512);
    }

    [Fact]
    public async Task IndependentInstancesSerializeAppendingWithoutPartialLines()
    {
        var paths = new ApplicationPaths(_root);
        var logs = new[] { new ProcessDiagnosticLog(paths, "host"), new ProcessDiagnosticLog(paths, "host") };
        var writes = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => logs[index % 2].Information("parallel.write", $"index={index}"))));
        Assert.Contains(true, writes);
        var lines = File.ReadAllLines(logs[0].LogFile);
        Assert.Equal(writes.Count(static written => written), lines.Length);
        Assert.All(lines, line =>
        {
            Assert.StartsWith("20", line, StringComparison.Ordinal);
            Assert.Contains("event=parallel.write", line, StringComparison.Ordinal);
            Assert.Equal(1, line.Split("event=parallel.write", StringSplitOptions.None).Length - 1);
        });
    }

    [Fact]
    public void DiskAndExceptionFormattingFailuresDoNotMaskTheOriginalFailure()
    {
        var paths = new ApplicationPaths(_root);
        paths.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(paths.Logs, "resodrive-host.log"));
        var log = new ProcessDiagnosticLog(paths, "host");
        var id = log.Exception("fatal.failed", new BrokenException());
        Assert.Matches("^[A-F0-9]{8}$", id);
        Assert.False(log.Information("disk.failed"));
    }

    [Fact]
    public void InjectedEventAndReferenceCannotForgeLogRecords()
    {
        var log = new ProcessDiagnosticLog(new ApplicationPaths(_root), "host");
        Assert.True(log.Write("ERROR\nsecret", "startup.failed\nforged", "line1\nline2", "ABCDEF12\nforged"));
        var lines = File.ReadAllLines(log.LogFile);
        Assert.Single(lines);
        Assert.Contains("event=diagnostic.event", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("errorId=", lines[0], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class BrokenException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("Broken exception formatter");
    }

    private static InvalidOperationException CaptureFailure()
    {
        try
        {
            throw new InvalidOperationException(
                "token=secret-value password=another-secret https://private.example/path in C:\\Users\\someone\\private");
        }
        catch (InvalidOperationException exception) { return exception; }
    }
}

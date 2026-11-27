using System.Collections.Concurrent;

namespace ResoDrive.Windows.Tests;

[Collection(RcloneRuntimeMutationTestGroup.Name)]
public sealed class RcloneUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_ReportsLatestStableAndIgnoresBeta()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = "v1.76.0" };
        var service = CreateService(package, runner);

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("v1.75.0", result.Value!.CurrentVersion);
        Assert.Equal("v1.76.0", result.Value.AvailableVersion);
        Assert.True(result.Value.UpdateAvailable);
        Assert.Single(runner.Calls, call => call.ExecutablePath == package.ExecutablePath &&
            call.Arguments.SequenceEqual(["version"]));
        Assert.Single(runner.Calls, call => call.Arguments.SequenceEqual(["version", "--check"]));
    }

    [Fact]
    public async Task CheckAsync_ReverifiesCanonicalVersionOnEveryCall()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = "v1.76.0" };
        var service = CreateService(package, runner);

        var first = await service.CheckAsync();
        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.Equal("v1.75.0", first.Value!.CurrentVersion);
        File.WriteAllText(package.ExecutablePath, "v1.76.0");

        var second = await service.CheckAsync();

        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.Equal("v1.76.0", second.Value!.CurrentVersion);
        Assert.False(second.Value.UpdateAvailable);
        Assert.Equal(2, runner.Calls.Count(call => call.ExecutablePath == package.ExecutablePath &&
            call.Arguments.SequenceEqual(["version"])));
        Assert.Equal(2, runner.Calls.Count(call => call.Arguments.SequenceEqual(["version", "--check"])));
    }

    [Theory]
    [InlineData("v1..76.0")]
    [InlineData("v1.76.0.")]
    [InlineData("v.1.76.0")]
    [InlineData("v2147483648.0.0")]
    public async Task CheckAsync_RejectsMalformedLatestWithoutThrowing(string latest)
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = latest };

        var result = await CreateService(package, runner).CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_check_invalid", result.Error!.Code);
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
    }

    [Theory]
    [InlineData("v1..75.0")]
    [InlineData("v1.75.0.")]
    [InlineData("v.1.75.0")]
    [InlineData("v2147483648.0.0")]
    public async Task CheckAsync_RejectsMalformedInstalledVersionWithoutThrowing(string installed)
    {
        using var package = new TestPackage(installed);
        var runner = new FakeRunner();

        var result = await CreateService(package, runner).CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_current_invalid", result.Error!.Code);
        Assert.Equal(installed, File.ReadAllText(package.ExecutablePath));
    }

    [Fact]
    public async Task CheckAsync_ReturnsTransientFailureOnTimeout()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { CheckTimesOut = true };

        var result = await CreateService(package, runner).CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_check_timeout", result.Error!.Code);
        Assert.True(result.Error.IsTransient);
    }

    [Fact]
    public async Task CheckAsync_ReturnsTransientFailureWhenOffline()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { CheckExitCode = 1 };

        var result = await CreateService(package, runner).CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_check_failed", result.Error!.Code);
        Assert.True(result.Error.IsTransient);
    }

    [Fact]
    public async Task CheckAsync_AcceptsVersionCheckOutputFromStandardError()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner
        {
            LatestVersion = "v1.76.0",
            WriteCheckToStandardError = true
        };

        var result = await CreateService(package, runner).CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("v1.76.0", result.Value!.AvailableVersion);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotDownloadWhenAlreadyCurrent()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = "v1.75.0" };

        var result = await CreateService(package, runner).UpdateAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.Updated);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("--output"));
    }

    [Fact]
    public async Task UpdateAsync_DoesNotDowngradeANewerManagedVersion()
    {
        using var package = new TestPackage("v1.77.0");
        var runner = new FakeRunner { LatestVersion = "v1.76.0" };

        var result = await CreateService(package, runner).UpdateAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.Updated);
        Assert.Equal("v1.77.0", result.Value.CurrentVersion);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("--output"));
    }

    [Fact]
    public async Task UpdateAsync_StagesVerifiesReplacesAndCleansBackup()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = "v1.76.0", StagedVersion = "v1.76.0" };

        var result = await CreateService(package, runner).UpdateAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Updated);
        Assert.Equal("v1.75.0", result.Value.PreviousVersion);
        Assert.Equal("v1.76.0", result.Value.CurrentVersion);
        Assert.Null(result.Value.CleanupPath);
        Assert.Equal("v1.76.0", File.ReadAllText(package.ExecutablePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(package.ExecutablePath)!, ".rclone-*"));
    }

    [Fact]
    public async Task UpdateAsync_RejectsUnexpectedStagedVersionWithoutChangingCurrent()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = "v1.76.0", StagedVersion = "v9.0.0" };

        var result = await CreateService(package, runner).UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_staged_invalid", result.Error!.Code);
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(package.ExecutablePath)!, ".rclone-*"));
    }

    [Fact]
    public async Task UpdateAsync_ExplainsReadOnlyPortablePackage()
    {
        using var package = new TestPackage("v1.75.0");
        File.SetAttributes(package.ExecutablePath, FileAttributes.ReadOnly);
        var runner = new FakeRunner { LatestVersion = "v1.76.0" };

        var result = await CreateService(package, runner).UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_read_only", result.Error!.Code);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("--output"));
    }

    [Fact]
    public async Task UpdateAsync_ReportsLockedExecutableAndPreservesIt()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LatestVersion = "v1.76.0", StagedVersion = "v1.76.0" };
        await using var held = new FileStream(package.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await CreateService(package, runner).UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("rclone.file_locked", result.Error!.Code);
        Assert.True(result.Error.IsTransient);
        Assert.Contains("still locked", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
        Assert.Equal("v1.76.0", File.ReadAllText(Assert.Single(
            Directory.GetFiles(package.ComponentDirectory, ".rclone-update-*.exe"))));
        Assert.Empty(Directory.GetFiles(package.ComponentDirectory, ".rclone-old-*.exe"));
    }

    [Fact]
    public async Task UpdateAsync_RetriesRealReplacementLockAndCompletesAfterRelease()
    {
        using var package = new TestPackage("v1.75.0");
        FileStream? held = null;
        var runner = new FakeRunner
        {
            AfterStagedInspection = () => held = new FileStream(package.ExecutablePath,
                FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        var waiting = false;
        try
        {
            var result = await CreateService(package, runner).UpdateAsync(new InlineProgress(_ =>
            {
                waiting = true;
                Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
                held!.Dispose();
            }));
            Assert.True(waiting);
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("v1.76.0", File.ReadAllText(package.ExecutablePath));
            Assert.Empty(Directory.GetFiles(package.ComponentDirectory, ".rclone-*.exe"));
            Assert.Single(runner.Calls, call => call.Arguments.Contains("--output"));
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task UpdateAsync_CancellationWhileWaitingForCommitLeavesCurrentRuntimeAndReleasesLock()
    {
        using var package = new TestPackage("v1.75.0");
        using var cancellation = new CancellationTokenSource();
        FileStream? held = null;
        var runner = new FakeRunner
        {
            AfterStagedInspection = () => held = new FileStream(package.ExecutablePath,
                FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(package, runner).UpdateAsync(
                new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
            Assert.NotNull(held);
            Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
            Assert.Empty(Directory.GetFiles(package.ComponentDirectory, ".rclone-*.exe"));
            held.Dispose();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var retry = await CreateService(package, new FakeRunner()).UpdateAsync(cancellationToken: deadline.Token);
            Assert.True(retry.Succeeded, retry.Error?.Message);
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_RealStagedReadLockIsRetriedAndPersistentFailureKeepsPayload(bool release)
    {
        using var package = new TestPackage("v1.75.0");
        FileStream? held = null;
        string? staged = null;
        var runner = new FakeRunner
        {
            AfterDownload = path =>
            {
                staged = path;
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        };
        var waiting = false;
        try
        {
            var result = await CreateService(package, runner).UpdateAsync(new InlineProgress(_ =>
            {
                waiting = true;
                if (release) held!.Dispose();
            }));
            Assert.True(waiting);
            Assert.Equal(release, result.Succeeded);
            if (!release) Assert.Equal("rclone.file_locked", result.Error!.Code);
            Assert.Single(runner.Calls, call => call.Arguments.Contains("--output"));
            held!.Dispose();
            Assert.Equal(release ? "v1.76.0" : "v1.75.0", File.ReadAllText(package.ExecutablePath));
            Assert.Equal(!release, File.Exists(staged));
            if (!release) Assert.Equal("v1.76.0", File.ReadAllText(staged!));
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalReadLockCannotRestoreOlderBackupOrDiscardRecoveryArtifacts(bool update)
    {
        using var package = new TestPackage("v1.75.0");
        var backup = package.WriteArtifact($".rclone-old-{Guid.NewGuid():N}.exe", "v1.74.0");
        var staged = package.WriteArtifact($".rclone-update-{Guid.NewGuid():N}.exe", "pending payload");
        var runner = new FakeRunner();
        var service = CreateService(package, runner);
        using (var held = new FileStream(package.ExecutablePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = update ? (ResoDrive.Core.Results.OperationResult)await service.UpdateAsync()
                : await service.CheckAsync();
            Assert.False(result.Succeeded);
            Assert.Equal("rclone.file_locked", result.Error!.Code);
            Assert.Equal(6, runner.Calls.Count);
            Assert.All(runner.Calls, call => Assert.Equal(package.ExecutablePath, call.ExecutablePath));
            Assert.Equal("v1.74.0", File.ReadAllText(backup));
            Assert.Equal("pending payload", File.ReadAllText(staged));
        }
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
    }

    [Fact]
    public async Task UpdateAsync_PostCommitLaunchLockKeepsNewImageAndBackupUntilUnlockedVerification()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { LockUpdatedCanonicalRuntime = true };
        var service = CreateService(package, runner);
        var result = await service.UpdateAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("rclone.file_locked", result.Error!.Code);
        Assert.Equal("v1.76.0", File.ReadAllText(package.ExecutablePath));
        Assert.Equal("v1.75.0", File.ReadAllText(Assert.Single(
            Directory.GetFiles(package.ComponentDirectory, ".rclone-old-*.exe"))));
        runner.LockUpdatedCanonicalRuntime = false;
        var verified = await service.CheckAsync();
        Assert.True(verified.Succeeded, verified.Error?.Message);
        Assert.Equal("v1.76.0", verified.Value!.CurrentVersion);
        Assert.Empty(Directory.GetFiles(package.ComponentDirectory, ".rclone-*.exe"));
        Assert.Single(runner.Calls, call => call.Arguments.Contains("--output"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(577)]
    [InlineData(1260)]
    public async Task UpdateAsync_DoesNotRetryStagedPermissionOrPolicyFailures(int nativeError)
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { StagedLaunchError = nativeError };
        var result = await CreateService(package, runner).UpdateAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("rclone.update_staged_invalid", result.Error!.Code);
        Assert.Single(runner.Calls, call => Path.GetFileName(call.ExecutablePath).StartsWith(".rclone-update-", StringComparison.Ordinal));
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
        Assert.Empty(Directory.GetFiles(package.ComponentDirectory, ".rclone-*.exe"));
    }

    [Fact]
    public async Task CheckAsync_ValidCanonicalCleansOnlyStrictUpdaterArtifacts()
    {
        using var package = new TestPackage("v1.75.0");
        var transaction = Guid.NewGuid().ToString("N");
        var staged = package.WriteArtifact($".rclone-update-{transaction}.exe", "staged");
        var backup = package.WriteArtifact($".rclone-old-{transaction}.exe", "v1.74.0");
        var malformed = new[]
        {
            package.WriteArtifact(".rclone-old-not-a-transaction.exe", "keep"),
            package.WriteArtifact($".rclone-old-{new string('g', 32)}.exe", "keep"),
            package.WriteArtifact($".rclone-update-{new string('a', 31)}.exe", "keep"),
            package.WriteArtifact($".rclone-update-{new string('a', 33)}.exe", "keep")
        };
        var bootstrapDownload = package.WriteArtifact($".download-{transaction}.zip", "keep");
        var bootstrapStage = package.WriteArtifact($".rclone-{transaction}.exe", "keep");
        var bootstrapBackup = package.WriteArtifact($".previous-{transaction}.exe", "keep");

        var result = await CreateService(
            package,
            new FakeRunner { LatestVersion = "v1.75.0" }).CheckAsync();

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.False(File.Exists(staged));
        Assert.False(File.Exists(backup));
        Assert.All(malformed, path => Assert.True(File.Exists(path)));
        Assert.True(File.Exists(bootstrapDownload));
        Assert.True(File.Exists(bootstrapStage));
        Assert.True(File.Exists(bootstrapBackup));
    }

    [ElevatedSymlinkFact]
    public async Task CanonicalFileLinkIsRejectedBeforeAnyExecutionOrRecoveryCleanup()
    {
        using var package = new TestPackage("v1.75.0");
        var target = package.WriteArtifact("linked-target.exe", "v1.75.0");
        var backup = package.WriteArtifact($".rclone-old-{Guid.NewGuid():N}.exe", "v1.74.0");
        var staged = package.WriteArtifact($".rclone-update-{Guid.NewGuid():N}.exe", "pending");
        File.Delete(package.ExecutablePath);
        File.CreateSymbolicLink(package.ExecutablePath, target);
        try
        {
            var runner = new FakeRunner();
            var service = CreateService(package, runner);
            var check = await service.CheckAsync();
            var update = await service.UpdateAsync();
            Assert.Equal("rclone.update_unsafe_path", check.Error?.Code);
            Assert.Equal("rclone.update_unsafe_path", update.Error?.Code);
            Assert.Empty(runner.Calls);
            Assert.Equal("v1.75.0", File.ReadAllText(target));
            Assert.Equal("v1.74.0", File.ReadAllText(backup));
            Assert.Equal("pending", File.ReadAllText(staged));
        }
        finally { File.Delete(package.ExecutablePath); }
    }

    [Fact]
    public async Task UpdateAsync_MissingCanonicalRestoresNewestValidBackupAndCleansUpdaterArtifacts()
    {
        using var package = new TestPackage("v1.75.0");
        File.Delete(package.ExecutablePath);
        var olderValid = package.WriteArtifact(
            $".rclone-old-{Guid.NewGuid():N}.exe",
            "v1.74.0",
            DateTime.UtcNow.AddMinutes(-2));
        var newestInvalid = package.WriteArtifact(
            $".rclone-old-{Guid.NewGuid():N}.exe",
            "invalid",
            DateTime.UtcNow.AddMinutes(-1));
        var staged = package.WriteArtifact(
            $".rclone-update-{Guid.NewGuid():N}.exe",
            "interrupted");

        var result = await CreateService(
            package,
            new FakeRunner { LatestVersion = "v1.74.0" }).UpdateAsync();

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.False(result.Value!.Updated);
        Assert.Equal("v1.74.0", File.ReadAllText(package.ExecutablePath));
        Assert.False(File.Exists(olderValid));
        Assert.False(File.Exists(newestInvalid));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task CheckAsync_InvalidCanonicalRestoresNewestValidBackup()
    {
        using var package = new TestPackage("invalid");
        package.WriteArtifact(
            $".rclone-old-{Guid.NewGuid():N}.exe",
            "v1.73.0",
            DateTime.UtcNow.AddMinutes(-2));
        package.WriteArtifact(
            $".rclone-old-{Guid.NewGuid():N}.exe",
            "v1.74.0",
            DateTime.UtcNow.AddMinutes(-1));

        var runner = new FakeRunner { LatestVersion = "v1.74.0" };
        var result = await CreateService(package, runner).CheckAsync();

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal("v1.74.0", result.Value!.CurrentVersion);
        Assert.Equal("v1.74.0", File.ReadAllText(package.ExecutablePath));
        Assert.Empty(Directory.EnumerateFiles(package.ComponentDirectory, ".rclone-old-*.exe"));
        // Invalid entry, copied-backup verification, and the fresh post-recovery inspection.
        Assert.Equal(3, runner.Calls.Count(call => call.ExecutablePath == package.ExecutablePath &&
            call.Arguments.SequenceEqual(["version"])));
    }

    [Fact]
    public async Task CheckAsync_PropagatesCancellation()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { ArtificialDelay = TimeSpan.FromSeconds(5) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(package, runner).CheckAsync(cancellation.Token));
    }

    [Fact]
    public async Task UpdateOperations_AreSerializedAcrossServiceInstances()
    {
        using var firstPackage = new TestPackage("v1.75.0");
        using var secondPackage = new TestPackage("v1.75.0");
        var runner = new FakeRunner
        {
            LatestVersion = "v1.75.0",
            ArtificialDelay = TimeSpan.FromMilliseconds(35)
        };

        await Task.WhenAll(
            CreateService(firstPackage, runner).UpdateAsync(),
            CreateService(secondPackage, runner).UpdateAsync());

        Assert.Equal(1, runner.MaximumConcurrency);
    }

    private static RcloneUpdateService CreateService(TestPackage package, FakeRunner runner)
    {
        var locator = new RcloneRuntimeLocator(new ApplicationPaths(package.DirectoryPath), runner);
        return new RcloneUpdateService(
            locator,
            runner,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateAsync_RestoresPreviousRuntimeWhenWindowsBlocksCommittedExecutable()
    {
        using var package = new TestPackage("v1.75.0");
        var runner = new FakeRunner { BlockUpdatedCanonicalRuntime = true };
        var result = await CreateService(package, runner).UpdateAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
    }

    private sealed class FakeRunner : IRcloneProcessRunner
    {
        private int _concurrency;
        private int _maximumConcurrency;

        public string LatestVersion { get; init; } = "v1.76.0";
        public string? StagedVersion { get; init; }
        public bool BlockUpdatedCanonicalRuntime { get; init; }
        public bool LockUpdatedCanonicalRuntime { get; set; }
        public int? StagedLaunchError { get; init; }
        public Action? AfterStagedInspection { get; init; }
        public Action<string>? AfterDownload { get; init; }
        public bool CheckTimesOut { get; init; }
        public int CheckExitCode { get; init; }
        public bool WriteCheckToStandardError { get; init; }
        public TimeSpan ArtificialDelay { get; init; }
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public ConcurrentQueue<Call> Calls { get; } = new();

        public async Task<ProcessRunResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string>? standardErrorLineReceived = null)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(concurrency);
            try
            {
                Calls.Enqueue(new Call(executablePath, [.. arguments]));
                if (ArtificialDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ArtificialDelay, cancellationToken);
                }

                if (arguments.SequenceEqual(["version"]))
                {
                    var isStaged = Path.GetFileName(executablePath).StartsWith(".rclone-update-", StringComparison.Ordinal);
                    if (isStaged && StagedLaunchError is { } error) throw new System.ComponentModel.Win32Exception(error);
                    var version = File.ReadAllText(executablePath);
                    if (BlockUpdatedCanonicalRuntime && version == LatestVersion && Path.GetFileName(executablePath) == "rclone.exe")
                        throw new System.ComponentModel.Win32Exception(5);
                    if (LockUpdatedCanonicalRuntime && version == LatestVersion && Path.GetFileName(executablePath) == "rclone.exe")
                        throw new System.ComponentModel.Win32Exception(32);
                    if (isStaged) AfterStagedInspection?.Invoke();
                    return new ProcessRunResult(0, $"rclone {version}\n- os/version: Microsoft Windows 11", "", false);
                }

                if (arguments.SequenceEqual(["version", "--check"]))
                {
                    var checkOutput = $"yours:  1.75.0\nlatest: {LatestVersion.TrimStart('v')} (released 2026-08-01)\nbeta: 9.0.0-beta.1";
                    return CheckTimesOut
                        ? new ProcessRunResult(-1, "", "", true)
                        : new ProcessRunResult(
                            CheckExitCode,
                            WriteCheckToStandardError ? "" : checkOutput,
                            WriteCheckToStandardError ? checkOutput : "",
                            false);
                }

                var outputIndex = Array.IndexOf([.. arguments], "--output");
                Assert.True(outputIndex >= 0);
                File.WriteAllText(arguments[outputIndex + 1], StagedVersion ?? LatestVersion);
                AfterDownload?.Invoke(arguments[outputIndex + 1]);
                return new ProcessRunResult(0, "", "", false);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrency, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed record Call(string ExecutablePath, IReadOnlyList<string> Arguments);

    private sealed class InlineProgress(Action<RcloneBootstrapProgress> report) : IProgress<RcloneBootstrapProgress>
    {
        public void Report(RcloneBootstrapProgress value) => report(value);
    }

    private sealed class ElevatedSymlinkFactAttribute : FactAttribute
    {
        public ElevatedSymlinkFactAttribute()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                Skip = "File symlink creation requires an elevated disposable Windows runner; no UAC is requested locally.";
        }
    }

    private sealed class TestPackage : IDisposable
    {
        public TestPackage(string version)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "rdrive-update-tests", Guid.NewGuid().ToString("N"));
            var paths = new ApplicationPaths(DirectoryPath);
            paths.EnsureCreated();
            ExecutablePath = paths.RcloneExecutable;
            File.WriteAllText(ExecutablePath, version);
        }

        public string DirectoryPath { get; }
        public string ExecutablePath { get; }
        public string ComponentDirectory => Path.GetDirectoryName(ExecutablePath)!;

        public string WriteArtifact(string name, string content, DateTime? lastWriteTimeUtc = null)
        {
            var path = Path.Combine(ComponentDirectory, name);
            File.WriteAllText(path, content);
            if (lastWriteTimeUtc is { } timestamp)
                File.SetLastWriteTimeUtc(path, timestamp);
            return path;
        }

        public void Dispose()
        {
            if (File.Exists(ExecutablePath))
            {
                File.SetAttributes(ExecutablePath, FileAttributes.Normal);
            }

            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

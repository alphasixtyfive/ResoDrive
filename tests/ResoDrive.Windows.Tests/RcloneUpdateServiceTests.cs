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
        Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(["version", "--check"]));
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
        Assert.Equal("rclone.update_locked", result.Error!.Code);
        Assert.True(result.Error.IsTransient);
        Assert.Equal("v1.75.0", File.ReadAllText(package.ExecutablePath));
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

        var result = await CreateService(
            package,
            new FakeRunner { LatestVersion = "v1.74.0" }).CheckAsync();

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal("v1.74.0", result.Value!.CurrentVersion);
        Assert.Equal("v1.74.0", File.ReadAllText(package.ExecutablePath));
        Assert.Empty(Directory.EnumerateFiles(package.ComponentDirectory, ".rclone-old-*.exe"));
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
                    var version = File.ReadAllText(executablePath);
                    if (BlockUpdatedCanonicalRuntime && version == LatestVersion && Path.GetFileName(executablePath) == "rclone.exe")
                        throw new System.ComponentModel.Win32Exception(5);
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

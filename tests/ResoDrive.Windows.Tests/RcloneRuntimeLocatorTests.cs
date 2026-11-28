using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class RcloneRuntimeLocatorTests : IDisposable
{
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public async Task RetriesExecutableSharingViolations(int error)
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.RcloneExecutable, "fixture");
        var runner = new LockedRunner(error, failures: 1);
        var result = await new RcloneRuntimeLocator(_paths, runner).InspectAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task PersistentExecutableLockReturnsClearErrorWithoutChangingRuntime()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.RcloneExecutable, "fixture");
        var runner = new LockedRunner(32, failures: int.MaxValue);
        var result = await new RcloneRuntimeLocator(_paths, runner).InspectAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("rclone.file_locked", result.Error!.Code);
        Assert.Contains("rclone executable", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(6, runner.Calls);
        Assert.Equal("fixture", File.ReadAllText(_paths.RcloneExecutable));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(577)]
    [InlineData(1260)]
    public async Task DoesNotRetryPermissionOrPolicyFailures(int error)
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.RcloneExecutable, "fixture");
        var runner = new LockedRunner(error, failures: int.MaxValue);
        var result = await new RcloneRuntimeLocator(_paths, runner).InspectAsync();
        Assert.False(result.Succeeded);
        Assert.Equal("rclone.invalid", result.Error!.Code);
        Assert.Equal(1, runner.Calls);
    }

    private sealed class LockedRunner(int error, int failures) : IRcloneProcessRunner
    {
        internal int Calls { get; private set; }
        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null)
        {
            if (++Calls <= failures) throw new System.ComponentModel.Win32Exception(error);
            return Task.FromResult(new ProcessRunResult(0, "rclone v1.75.0", "", false));
        }
    }

    private readonly ApplicationPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "resodrive-runtime-locator-tests", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData("rclone v1.75.1", "v1.75.1")]
    [InlineData("rclone v1.75.1-DEV", "v1.75.1-DEV")]
    [InlineData("rclone v1.75.1-beta.2", "v1.75.1-beta.2")]
    public async Task AcceptsReportableVersions(string output, string expected)
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.RcloneExecutable, "test fixture");

        var result = await new RcloneRuntimeLocator(_paths, new FakeRunner(output)).InspectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Value!.Version);
    }

    [Fact]
    public async Task LongDevelopmentVersionDoesNotInvalidateAnInstalledEngine()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.RcloneExecutable, "test fixture");

        var version = "v1.75.1-" + new string('a', 25);
        var result = await new RcloneRuntimeLocator(_paths, new FakeRunner("rclone " + version)).InspectAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(version, result.Value!.Version);
        Assert.Equal(ClientUserAgent.Value, ClientUserAgent.WithRcloneVersion(result.Value.Version));
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    private sealed class FakeRunner(string output) : IRcloneProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? standardErrorLineReceived = null)
        {
            Assert.Equal(["version"], arguments);
            return Task.FromResult(new ProcessRunResult(0, output, string.Empty, false));
        }
    }
}

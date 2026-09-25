using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class RcloneRuntimeLocatorTests : IDisposable
{
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

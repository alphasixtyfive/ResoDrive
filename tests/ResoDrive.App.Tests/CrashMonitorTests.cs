namespace ResoDrive.App.Tests;

public sealed class CrashMonitorTests
{
    private static readonly string[] HostObserverArguments =
        ["--observe", "123", "134000000000000000", "--role", "host"];
    [Fact]
    public void Observer_UsesArgumentListAndDoesNotPassSupervisionToChildren()
    {
        var start = CrashMonitor.CreateObserverStartInfo(@"C:\Program Files\rdrive\resodrive-launcher.exe",
            123, 134000000000000000, "host");
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(HostObserverArguments, start.ArgumentList);
        Assert.False(start.Environment.ContainsKey(ResoDrive.Windows.ApplicationLauncher.SupervisedEnvironmentVariable));
    }

    [Theory]
    [InlineData(0, 1, "ui")]
    [InlineData(1, 0, "ui")]
    [InlineData(1, 1, "helper")]
    public void Observer_RejectsInvalidProcessIdentity(int pid, long creation, string role) =>
        Assert.Throws<ArgumentException>(() => CrashMonitor.CreateObserverStartInfo("observer.exe", pid, creation, role));
}

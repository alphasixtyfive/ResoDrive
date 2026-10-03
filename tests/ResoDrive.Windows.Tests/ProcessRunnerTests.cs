using System.Diagnostics;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class ProcessRunnerTests
{
    [Theory]
    [InlineData(-2000)]
    [InlineData(4294967295)]
    public async Task InvalidTimeoutIsRejectedBeforeTryingToLaunchAProcess(long milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProcessRunner.RunAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            [], TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task OversizedLogLineIsBoundedAndFollowingProgressStillReachesObserver()
    {
        var progress = "{\"stats\":{\"bytes\":123}}";
        var input = new string('x', 1024 * 1024) + "\r\n" + progress + "\nlast line";
        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input)));
        var lines = new List<string>();

        var captured = await ProcessRunner.ReadBoundedLinesAsync(reader, lines.Add, CancellationToken.None);

        Assert.Equal(3, lines.Count);
        Assert.Equal(ProcessRunner.MaximumLogLineCharacters + " [truncated]".Length, lines[0].Length);
        Assert.EndsWith(" [truncated]", lines[0], StringComparison.Ordinal);
        Assert.Equal(progress, lines[1]);
        Assert.Equal("last line", lines[2]);
        Assert.True(captured.Length <= 256 * 1024);
    }

    [Fact]
    public async Task ThrowingObserverDoesNotPreventSubsequentLogDrain()
    {
        using var reader = new StreamReader(new MemoryStream("first\rsecond\nthird"u8.ToArray()));
        var observed = new List<string>();

        var captured = await ProcessRunner.ReadBoundedLinesAsync(reader, line =>
        {
            observed.Add(line);
            if (line == "first") throw new InvalidOperationException("observer failed");
        }, CancellationToken.None);

        Assert.Equal(["first", "second", "third"], observed);
        Assert.Contains("third", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TimesOutAndTerminatesProcessTreeWithinBound()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync(
            "cmd.exe",
            ["/d", "/s", "/c", "ping 127.0.0.1 -n 20 > nul"],
            TimeSpan.FromMilliseconds(100));

        Assert.True(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8));
    }
}

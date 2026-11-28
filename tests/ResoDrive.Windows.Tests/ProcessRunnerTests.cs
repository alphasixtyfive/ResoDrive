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

    [Fact]
    public async Task ExitedParentWithInheritedOutputStillHonorsTimeoutAndRetainsOutput()
    {
        using var fixture = new InheritedOutputFixture();
        var run = fixture.RunAsync(TimeSpan.FromSeconds(8));
        await fixture.WaitForParentExitAsync();
        Assert.False(run.IsCompleted); // A live descendant still owns both output pipes.

        var result = await run.WaitAsync(TimeSpan.FromSeconds(12));

        Assert.True(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("parent finished", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("parent diagnostic", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitedParentWithInheritedOutputStillHonorsCallerCancellation()
    {
        using var fixture = new InheritedOutputFixture();
        using var cancellation = new CancellationTokenSource();
        var run = fixture.RunAsync(TimeSpan.FromMinutes(1), cancellation.Token);
        await fixture.WaitForParentExitAsync();
        Assert.False(run.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(8)));
    }

    private sealed class InheritedOutputFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-pipe-test-" + Guid.NewGuid().ToString("N"));
        private string ParentExecutable => Path.Combine(_root, "parent.exe");
        private string ChildExecutable => Path.Combine(_root, "child.exe");
        private string ParentExit => Path.Combine(_root, "parent-exit.txt");

        public InheritedOutputFixture()
        {
            Directory.CreateDirectory(_root);
            // Native command shells avoid two cold PowerShell startups competing
            // with the execution deadline on a busy hosted runner. Both binaries
            // live in this fixture's unique folder for exact cleanup ownership.
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ParentExecutable);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ChildExecutable);
            File.WriteAllText(Path.Combine(_root, "child.cmd"), """
                @echo off
                echo child ready
                >"%~dp0child-ready.txt" echo ready
                ping 127.0.0.1 -n 60 >nul
                """);
            File.WriteAllText(Path.Combine(_root, "parent.cmd"), """
                @echo off
                start "" /b "%~dp0child.exe" /d /q /c ""%~dp0child.cmd""
                :wait
                if exist "%~dp0child-ready.txt" goto ready
                ping 127.0.0.1 -n 2 >nul
                goto wait
                :ready
                echo parent finished
                echo parent diagnostic 1>&2
                >"%~dp0parent-exit.txt" echo ready
                exit /b 0
                """);
        }

        public Task<ProcessRunResult> RunAsync(TimeSpan timeout, CancellationToken cancellation = default) =>
            ProcessRunner.RunAsync(ParentExecutable, ["/d", "/q", "/c", Path.Combine(_root, "parent.cmd")],
                timeout, cancellation);

        public async Task WaitForParentExitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(ParentExit)) await Task.Delay(20, timeout.Token);
            // Observe the actual parent exit. A fixed delay could cancel before
            // exit on a busy runner and miss the inherited-pipe regression.
            using var parent = FindFixtureProcess(ParentExecutable);
            if (parent is not null) await parent.WaitForExitAsync(timeout.Token);
            using var child = FindFixtureProcess(ChildExecutable);
            Assert.NotNull(child);
            Assert.False(child.HasExited);
        }

        private static Process? FindFixtureProcess(string executable)
        {
            var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
            Process? found = null;
            try
            {
                foreach (var process in candidates)
                {
                    try
                    {
                        if (!process.HasExited && process.MainModule is { } module &&
                            MountOwnershipStore.IsSameExecutablePath(executable, module.FileName))
                            return found = process;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    { } // Exited or unavailable; never treat a name alone as ownership.
                }
                return null;
            }
            finally
            {
                foreach (var process in candidates)
                    if (!ReferenceEquals(process, found)) process.Dispose();
            }
        }

        public void Dispose()
        {
            using (var child = FindFixtureProcess(ChildExecutable))
            {
                if (child is not null && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(3000);
                }
            }
            var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The process fixture left the temporary directory.");
            Directory.Delete(_root, recursive: true);
        }
    }
}

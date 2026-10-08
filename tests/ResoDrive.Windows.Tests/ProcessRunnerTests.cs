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
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(8)));
    }

    private sealed class InheritedOutputFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-pipe-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        private string ChildIdentity => Path.Combine(_root, "child.txt");
        private string ParentExit => Path.Combine(_root, "parent-exit.txt");

        public InheritedOutputFixture()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "child.ps1"), """
                param([string]$Identity)
                $self = [Diagnostics.Process]::GetCurrentProcess()
                [IO.File]::WriteAllText($Identity + '.tmp', "$PID,$($self.StartTime.ToUniversalTime().Ticks)")
                [IO.File]::Move($Identity + '.tmp', $Identity)
                [Console]::WriteLine('child ready')
                Start-Sleep -Seconds 60
                """);
            File.WriteAllText(Path.Combine(_root, "parent.ps1"), """
                param([string]$Root)
                $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.Arguments = '-NoProfile -File "' + (Join-Path $Root 'child.ps1') + '" -Identity "' + (Join-Path $Root 'child.txt') + '"'
                $child = [Diagnostics.Process]::Start($start)
                try {
                    while (-not (Test-Path (Join-Path $Root 'child.txt'))) { Start-Sleep -Milliseconds 20 }
                    [Console]::WriteLine('parent finished')
                    [Console]::Error.WriteLine('parent diagnostic')
                    $parent = [Diagnostics.Process]::GetCurrentProcess()
                    $marker = Join-Path $Root 'parent-exit.txt'
                    [IO.File]::WriteAllText($marker + '.tmp', "$PID,$($parent.StartTime.ToUniversalTime().Ticks)")
                    [IO.File]::Move($marker + '.tmp', $marker)
                } finally { $child.Dispose() }
                """);
        }

        public Task<ProcessRunResult> RunAsync(TimeSpan timeout, CancellationToken cancellation = default) =>
            ProcessRunner.RunAsync(_powershell, ["-NoProfile", "-File", Path.Combine(_root, "parent.ps1"), "-Root", _root],
                timeout, cancellation);

        public async Task WaitForParentExitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(ParentExit)) await Task.Delay(20, timeout.Token);
            // Observe the actual parent exit. A fixed delay could cancel before
            // exit on a busy runner and miss the inherited-pipe regression.
            var identity = File.ReadAllText(ParentExit).Split(',');
            try
            {
                using var parent = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
                if (!parent.HasExited && parent.StartTime.ToUniversalTime().Ticks ==
                    long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture))
                    await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { } // The original parent already exited.
        }

        public void Dispose()
        {
            if (File.Exists(ChildIdentity))
            {
                var identity = File.ReadAllText(ChildIdentity).Split(',');
                try
                {
                    using var child = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
                    if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture))
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(3000);
                    }
                }
                catch (ArgumentException) { } // The fixture child already exited.
            }
            var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The process fixture left the temporary directory.");
            Directory.Delete(_root, recursive: true);
        }
    }
}

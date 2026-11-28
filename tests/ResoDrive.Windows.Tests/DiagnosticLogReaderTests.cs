using System.Diagnostics;
using System.Text;

namespace ResoDrive.Windows.Tests;

public sealed class DiagnosticLogReaderTests : IDisposable
{
    private static readonly string[] CurrentRecords = ["first", "second", "last"];
    private static readonly string[] RotatedAndCurrentRecords = ["previous-1", "previous-2", "current-1", "current-2"];
    private static readonly string[] HugeFileTailRecords = ["recent-1", "recent-2"];
    private static readonly string[] ValidUtf8Records = ["before", "after"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-log-reader-tests", Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_root, "resodrive-host.log");

    public DiagnosticLogReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task MissingSourcesAreUnavailableAndDoNotCreateDirectoriesOrFiles()
    {
        var path = Path.Combine(_root, "missing", "host.log");
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(path);
        Assert.Empty(excerpt.Lines);
        Assert.True(excerpt.Unavailable);
        Assert.False(excerpt.Truncated);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task MissingRotationIsNormalAndCurrentRecordsKeepTheirOrder()
    {
        await File.WriteAllTextAsync(LogPath, "first\r\nsecond\nlast");
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal(CurrentRecords, excerpt.Lines);
        Assert.False(excerpt.Unavailable);
        Assert.False(excerpt.Truncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyReadableCurrentFileIsNotAnUnavailableSource(bool bom)
    {
        await File.WriteAllTextAsync(LogPath, "", new UTF8Encoding(bom));
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Empty(excerpt.Lines);
        Assert.False(excerpt.Unavailable);
        Assert.False(excerpt.Truncated);
    }

    [Fact]
    public async Task PreviousRecordsComeBeforeCurrentRecordsAndUtf8BomIsNotPartOfTheFirstLine()
    {
        await File.WriteAllTextAsync(LogPath + ".1", "previous-1\nprevious-2\n", new UTF8Encoding(true));
        await File.WriteAllTextAsync(LogPath, "current-1\ncurrent-2\n");
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal(RotatedAndCurrentRecords, excerpt.Lines);
        Assert.False(excerpt.Unavailable);
    }

    [Fact]
    public async Task MissingCurrentPreservesPreviousRecordsButMarksIncompleteSources()
    {
        await File.WriteAllTextAsync(LogPath + ".1", "previous\n");
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal("previous", Assert.Single(excerpt.Lines));
        Assert.True(excerpt.Unavailable);
    }

    [Fact]
    public async Task EachFileRetainsItsOwnMostRecent250Lines()
    {
        await File.WriteAllLinesAsync(LogPath + ".1", Enumerable.Range(0, 400).Select(index => $"previous-{index}"));
        await File.WriteAllLinesAsync(LogPath, Enumerable.Range(0, 400).Select(index => $"current-{index}"));
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal(500, excerpt.Lines.Count);
        Assert.Equal("previous-150", excerpt.Lines[0]);
        Assert.Equal("previous-399", excerpt.Lines[249]);
        Assert.Equal("current-150", excerpt.Lines[250]);
        Assert.Equal("current-399", excerpt.Lines[^1]);
        Assert.True(excerpt.Truncated);
        Assert.False(excerpt.Unavailable);
    }

    [Fact]
    public async Task HugeFileReadsOnlyItsBoundedTailAndDropsThePartialLeadingRecord()
    {
        await using (var stream = new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(64L * 1024 * 1024);
            stream.Position = stream.Length;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("partial\nrecent-1\nrecent-2\n"));
        }
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal(HugeFileTailRecords, excerpt.Lines);
        Assert.True(excerpt.Truncated);
        Assert.False(excerpt.Unavailable);
        Assert.Equal(64L * 1024 * 1024 + Encoding.UTF8.GetByteCount("partial\nrecent-1\nrecent-2\n"), new FileInfo(LogPath).Length);
    }

    [Fact]
    public async Task OversizedSingleRecordCannotEscapeTheByteBudget()
    {
        await File.WriteAllTextAsync(LogPath, new string('x', 128 * 1024));
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Empty(excerpt.Lines);
        Assert.True(excerpt.Truncated);
        Assert.False(excerpt.Unavailable);
    }

    [Fact]
    public async Task MalformedUtf8RecordIsOmittedWithoutLosingReadableRecords()
    {
        await File.WriteAllBytesAsync(LogPath, [.. Encoding.UTF8.GetBytes("before\n"), 0xFF, (byte)'\n', .. Encoding.UTF8.GetBytes("after\n")]);
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal(ValidUtf8Records, excerpt.Lines);
        Assert.True(excerpt.Unavailable);
        Assert.False(excerpt.Truncated);
    }

    [Theory]
    [InlineData(@"\\server.invalid\share\host.log")]
    [InlineData(@"\\?\C:\host.log")]
    [InlineData(@"\\.\pipe\host.log")]
    [InlineData(@"relative\host.log")]
    [InlineData(@"C:host.log")]
    [InlineData("")]
    public async Task NonlocalDeviceAndRelativePathsAreUnavailableWithoutBeingFollowed(string path)
    {
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(path);
        Assert.Empty(excerpt.Lines);
        Assert.True(excerpt.Unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedSourceDoesNotHideReadableRecordsFromTheOtherFile(bool lockPrevious)
    {
        await File.WriteAllTextAsync(LogPath + ".1", "previous\n");
        await File.WriteAllTextAsync(LogPath, "current\n");
        await using var held = new FileStream(lockPrevious ? LogPath + ".1" : LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Equal(lockPrevious ? "current" : "previous", Assert.Single(excerpt.Lines));
        Assert.True(excerpt.Unavailable);
    }

    [Fact]
    public async Task DirectoryInPlaceOfALogFileIsUnavailable()
    {
        Directory.CreateDirectory(LogPath);
        var excerpt = await DiagnosticReport.ReadRecentLogsAsync(LogPath);
        Assert.Empty(excerpt.Lines);
        Assert.True(excerpt.Unavailable);
    }

    [Fact]
    public async Task CancellationRemainsCancellationInsteadOfAnUnavailableExcerpt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticReport.ReadRecentLogsAsync(LogPath, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task JunctionAncestorIsRejectedBeforeReadingItsDescendants()
    {
        var outside = _root + "-outside";
        var junction = Path.Combine(_root, "redirected");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "host.log"), "must-not-be-read\n");
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
            var excerpt = await DiagnosticReport.ReadRecentLogsAsync(Path.Combine(junction, "host.log"));
            Assert.Empty(excerpt.Lines);
            Assert.True(excerpt.Unavailable);
            Assert.Equal("must-not-be-read\n", await File.ReadAllTextAsync(Path.Combine(outside, "host.log")));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

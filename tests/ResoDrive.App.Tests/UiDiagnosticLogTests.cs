namespace ResoDrive.App.Tests;

public sealed class UiDiagnosticLogTests
{
    [Fact]
    public void StartupReportingStillReturnsAReferenceWhenLoggerResolutionFails()
    {
        var original = new InvalidOperationException("Original startup failure");
        var id = UiDiagnosticLog.ReportException("startup.fatal", original,
            () => throw new FileNotFoundException("Simulated unavailable runtime component"));
        Assert.Matches("^[A-F0-9]{8}$", id);
        Assert.Equal("Original startup failure", original.Message);
    }

    [Fact]
    public void Exception_WritesStableErrorIdAndSanitizesSecrets()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "resodrive-ui-log-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "resodrive-ui.log");
        var log = new UiDiagnosticLog(path);

        var errorId = log.Exception(
            "startup.failed",
            new InvalidOperationException("token=abc123 in C:\\Users\\alexey\\private"));

        var contents = File.ReadAllText(path);
        Assert.Equal(8, errorId.Length);
        Assert.Contains($"errorId={errorId}", contents);
        Assert.Contains("<redacted>", contents);
        Assert.Contains("<user>", contents);
        Assert.DoesNotContain("abc123", contents);
        Assert.DoesNotContain("alexey", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Information_RotatesOversizedLog()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "resodrive-ui-log-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "resodrive-ui.log");
        var log = new UiDiagnosticLog(path, maximumBytes: 100);

        log.Information("first", new string('a', 80));
        log.Information("second", new string('b', 80));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.Contains("event=second", File.ReadAllText(path));
        Assert.Contains("event=first", File.ReadAllText(path + ".1"));
    }
}

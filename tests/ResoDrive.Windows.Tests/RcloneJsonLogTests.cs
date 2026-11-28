using System.Text.Json;
using System.Globalization;

namespace ResoDrive.Windows.Tests;

public sealed class RcloneJsonLogTests
{
    [Fact]
    public void TryParse_ReadsOfficialStructuredStats()
    {
        const string line = """
            {"time":"2026-08-27T08:06:09.9128331+02:00","level":"info","msg":"stats","stats":{"bytes":524288,"totalBytes":1048576,"checks":18,"totalChecks":20,"transfers":3,"totalTransfers":8,"errors":0,"speed":262144,"eta":12,"elapsedTime":4.5}}
            """;

        Assert.True(RcloneJsonLogParser.TryParse(line, out var parsed));
        var stats = Assert.IsType<RcloneSyncStats>(parsed?.Stats);
        Assert.Equal(524288, stats.Bytes);
        Assert.Equal(1048576, stats.TotalBytes);
        Assert.Equal(18, stats.Checks);
        Assert.Equal(20, stats.TotalChecks);
        Assert.Equal(3, stats.Transfers);
        Assert.Equal(8, stats.TotalTransfers);
        Assert.Equal(262144, stats.Speed);
        Assert.Equal(12, stats.EtaSeconds);
        Assert.Equal(4.5, stats.ElapsedSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void TryParse_RejectsNonEvents(string line)
    {
        Assert.False(RcloneJsonLogParser.TryParse(line, out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"bytes\":\"unknown\"}")]
    [InlineData("{\"bytes\":-1}")]
    [InlineData("{\"bytes\":null}")]
    [InlineData("{\"bytes\":9223372036854775808}")]
    public void EmptyOrMalformedStatisticsAreNotZeroProgressMeasurements(string stats)
    {
        Assert.True(RcloneJsonLogParser.TryParse("{\"level\":\"info\",\"stats\":" + stats + "}", out var parsed));
        Assert.Null(parsed!.Stats);
    }

    [Fact]
    public void StructuredWriter_ProducesOneValidJsonObjectPerLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sync.jsonl");
        try
        {
            using (var writer = new RcloneStructuredLogWriter(path))
            {
                writer.Write(new RcloneJsonLogEvent(
                    DateTimeOffset.Parse("2026-08-27T08:06:09+02:00", CultureInfo.InvariantCulture),
                    "info",
                    "line one\nline two",
                    "accounting/stats.go",
                    new RcloneSyncStats(0, 0, 60, 60, 0, 0, 0, 0, null, 0.47)));
            }

            var line = Assert.Single(File.ReadAllLines(path));
            using var document = JsonDocument.Parse(line);
            Assert.Equal(60, document.RootElement.GetProperty("stats").GetProperty("checks").GetInt64());
            Assert.Equal("line one\nline two", document.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

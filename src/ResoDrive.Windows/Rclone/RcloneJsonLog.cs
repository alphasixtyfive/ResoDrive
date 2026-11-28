using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResoDrive.Windows;

internal sealed record RcloneSyncStats(
    long Bytes,
    long TotalBytes,
    long Checks,
    long TotalChecks,
    long Transfers,
    long TotalTransfers,
    long Errors,
    double Speed,
    double? EtaSeconds,
    double ElapsedSeconds);

internal sealed record RcloneJsonLogEvent(
    DateTimeOffset? Time,
    string Level,
    string Message,
    string? Source,
    RcloneSyncStats? Stats)
{
    internal static RcloneJsonLogEvent PlainText(string message) =>
        new(DateTimeOffset.UtcNow, "warning", message, null, null);
}

/// <summary>Parses the newline-delimited JSON emitted by rclone's --use-json-log option.</summary>
internal static class RcloneJsonLogParser
{
    internal static bool TryParse(string line, out RcloneJsonLogEvent? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            value = new RcloneJsonLogEvent(
                ReadTimestamp(root, "time"),
                ReadString(root, "level") ?? "info",
                ReadString(root, "msg") ?? string.Empty,
                ReadString(root, "source"),
                root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object
                    ? ParseStats(stats)
                    : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static RcloneSyncStats? ParseStats(JsonElement stats) =>
        // A diagnostic event with an empty/invalid stats object is not a new
        // zero-byte measurement. Preserve the last genuine transfer observation.
        !stats.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number ||
            !bytes.TryGetInt64(out var count) || count < 0
            ? null : new(
        ReadNonNegativeInt64(stats, "bytes"),
        ReadNonNegativeInt64(stats, "totalBytes"),
        ReadNonNegativeInt64(stats, "checks"),
        ReadNonNegativeInt64(stats, "totalChecks"),
        ReadNonNegativeInt64(stats, "transfers"),
        ReadNonNegativeInt64(stats, "totalTransfers"),
        ReadNonNegativeInt64(stats, "errors"),
        ReadNonNegativeDouble(stats, "speed"),
        ReadNullableNonNegativeDouble(stats, "eta"),
        ReadNonNegativeDouble(stats, "elapsedTime"));

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement parent, string name) =>
        ReadString(parent, name) is { } value && DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;

    private static long ReadNonNegativeInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? Math.Max(0, result)
            : 0;

    private static double ReadNonNegativeDouble(JsonElement parent, string name) =>
        ReadNullableNonNegativeDouble(parent, name) ?? 0d;

    private static double? ReadNullableNonNegativeDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) ||
            !double.IsFinite(result))
        {
            return null;
        }

        return Math.Max(0d, result);
    }
}

/// <summary>Writes bounded, normalized NDJSON diagnostic events.</summary>
internal sealed class RcloneStructuredLogWriter : IDisposable
{
    private const long MaximumBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly StreamWriter _writer;
    private long _bytesWritten;

    internal RcloneStructuredLogWriter(string path)
    {
        var existingLength = File.Exists(path) ? new FileInfo(path).Length : 0;
        var stream = new FileStream(
            path,
            existingLength >= MaximumBytes ? FileMode.Create : FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _bytesWritten = stream.Length;
        _writer = new StreamWriter(
            stream,
            new System.Text.UTF8Encoding(false),
            bufferSize: 16 * 1024,
            leaveOpen: false);
    }

    internal void Write(RcloneJsonLogEvent value)
    {
        var line = JsonSerializer.Serialize(value, SerializerOptions);
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(line) +
            System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (_bytesWritten + byteCount > MaximumBytes)
        {
            return;
        }

        _writer.WriteLine(line);
        _bytesWritten += byteCount;
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }
}

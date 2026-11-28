using System.Text;

namespace ResoDrive.Windows;

public sealed record DiagnosticLogExcerpt(IReadOnlyList<string> Lines, bool Truncated = false, bool Unavailable = false);

public static partial class DiagnosticReport
{
    private const int MaximumLogBytes = 64 * 1024;
    private const int MaximumLogLines = 250;
    private static readonly Encoding StrictLogEncoding = new UTF8Encoding(false, true);

    public static Task<DiagnosticLogExcerpt> ReadRecentLogsAsync(string baseLogPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadRecentLogs(baseLogPath, cancellationToken), cancellationToken);

    private static DiagnosticLogExcerpt ReadRecentLogs(string baseLogPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var lines = new List<string>();
        var truncated = false;
        var unavailable = false;
        var readableFiles = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(baseLogPath) || !ProcessDiagnosticLog.IsLocalDiagnosticPath(baseLogPath))
            {
                token.ThrowIfCancellationRequested();
                return new([], Unavailable: true);
            }
            baseLogPath = Path.GetFullPath(baseLogPath);
            foreach (var path in new[] { baseLogPath + ".1", baseLogPath })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    RejectRedirectedLogRead(path, token);
                    var excerpt = ReadLogTail(path, token);
                    lines.AddRange(excerpt.Lines);
                    truncated |= excerpt.Truncated;
                    unavailable |= excerpt.Unavailable;
                    readableFiles++;
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // The previous file is optional. A missing current file means
                    // the available excerpt does not include all expected sources.
                    unavailable |= path == baseLogPath;
                }
                catch (Exception exception) when (IsLogReadFailure(exception))
                {
                    unavailable = true;
                }
            }
        }
        catch (Exception exception) when (IsLogReadFailure(exception))
        {
            unavailable = true;
        }
        token.ThrowIfCancellationRequested();
        return new(lines.ToArray(), truncated, unavailable || readableFiles == 0);
    }

    private static void RejectRedirectedLogRead(string path, CancellationToken token)
    {
        // Check the root and every parent before touching its descendants. Testing
        // only the log file could first follow a junction to an offline share.
        var ancestors = new Stack<string>();
        for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            ancestors.Push(ancestor);
        while (ancestors.TryPop(out var ancestor))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(ancestor);
            var directoryExpected = ancestors.Count > 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directoryExpected)
                throw new IOException("Diagnostic logs must use regular local files and directories.");
        }
    }

    private static DiagnosticLogExcerpt ReadLogTail(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        var length = stream.Length;
        var offset = Math.Max(0, length - MaximumLogBytes);
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(length, MaximumLogBytes)];
        var read = 0;
        while (read < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) break;
            read += count;
        }
        var truncated = offset > 0 || read < buffer.Length;
        if (read == 0) return new([], truncated);

        // A byte tail can begin inside a UTF-8 character or record. Drop that first
        // partial record rather than treating its suffix as a diagnostic event.
        var start = offset > 0 ? Array.IndexOf(buffer, (byte)'\n', 0, read) + 1 : 0;
        if (offset > 0 && start == 0) return new([], Truncated: true);
        if (offset == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            start = 3;
        if (start == read) return new([], truncated);
        var end = buffer[read - 1] == '\n' ? read - 1 : read;
        var lines = new List<string>(MaximumLogLines);
        var unavailable = false;
        var inspectedLines = 0;
        while (end >= start)
        {
            token.ThrowIfCancellationRequested();
            if (inspectedLines++ == MaximumLogLines) { truncated = true; break; }
            var separator = end - 1;
            while (separator >= start && buffer[separator] != '\n') separator--;
            var lineStart = separator + 1;
            var lineEnd = end > lineStart && buffer[end - 1] == '\r' ? end - 1 : end;
            try { lines.Add(StrictLogEncoding.GetString(buffer, lineStart, lineEnd - lineStart)); }
            catch (DecoderFallbackException) { unavailable = true; }
            if (separator < start) break;
            end = separator;
        }
        lines.Reverse();
        return new(lines.ToArray(), truncated, unavailable);
    }

    private static bool IsLogReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException;
}

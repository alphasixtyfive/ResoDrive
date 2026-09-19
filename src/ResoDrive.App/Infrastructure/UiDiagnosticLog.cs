using System.Globalization;
using System.IO;
using System.Text;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed class UiDiagnosticLog
{
    private const long DefaultMaximumBytes = 512 * 1024;
    private static readonly Lazy<UiDiagnosticLog> Instance = new(CreateCurrent);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly AccountDataGuard? _accountData;

    internal UiDiagnosticLog(string path, long maximumBytes = DefaultMaximumBytes, AccountDataGuard? accountData = null)
    {
        _path = Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        _accountData = accountData;
    }

    internal static UiDiagnosticLog Current => Instance.Value;

    private static UiDiagnosticLog CreateCurrent()
    {
        var paths = new ApplicationPaths();
        try { return new(paths.UiLogFile, accountData: new AccountDataGuard(paths)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable security marker must block the app, not its startup-error reporting.
            return new(paths.UiLogFile);
        }
    }

    internal void Information(string eventName, string? detail = null) =>
        Write("INFO", eventName, detail, errorId: null);

    internal string Exception(string eventName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var errorId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant();
        Write(
            "ERROR",
            eventName,
            exception.ToString(),
            errorId);
        return errorId;
    }

    private void Write(string level, string eventName, string? detail, string? errorId)
    {
        try
        {
            if (_accountData?.IsBlocked == true) return;
            var safeEvent = OneLine(eventName);
            var safeDetail = string.IsNullOrWhiteSpace(detail)
                ? string.Empty
                : $" detail=\"{OneLine(RecoveryToolsService.Sanitize(detail))}\"";
            var id = errorId is null ? string.Empty : $" errorId={errorId}";
            var line = $"{DateTimeOffset.UtcNow:O} level={level} event={safeEvent}{id}{safeDetail}{Environment.NewLine}";
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Diagnostics must never prevent the application from running.
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length + incomingBytes <= _maximumBytes)
            return;

        var previousPath = _path + ".1";
        if (File.Exists(previousPath))
            File.Delete(previousPath);
        File.Move(_path, previousPath);
    }

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();
}

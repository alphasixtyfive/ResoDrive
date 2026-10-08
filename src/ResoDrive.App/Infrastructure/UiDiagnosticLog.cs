using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed class UiDiagnosticLog
{
    private static readonly Lazy<UiDiagnosticLog> Instance = new(CreateCurrent);
    private static readonly UiDiagnosticLog PendingMigrationLog = new();
    private readonly ProcessDiagnosticLog? _log;

    internal UiDiagnosticLog(string path, long maximumBytes = ProcessDiagnosticLog.DefaultMaximumBytes,
        AccountDataGuard? accountData = null)
        : this(new ProcessDiagnosticLog(path, maximumBytes, accountData)) { }

    private UiDiagnosticLog(ProcessDiagnosticLog log) => _log = log;
    private UiDiagnosticLog() { }

    private static UiDiagnosticLog CreateCurrent()
    {
        try { return new(new ProcessDiagnosticLog(new ApplicationPaths(), "ui")); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // An invalid destination must not replace the original startup error.
            // Disable account logging rather than writing through a fallback path.
            return new();
        }
    }

    // Do not bind the process logger to a root that is about to be renamed.
    internal static UiDiagnosticLog Current => DirectoryMigrationStartup.PendingHandoff ? PendingMigrationLog : Instance.Value;
    internal void StartSession() => _log?.StartSession();
    internal void Information(string eventName, string? detail = null) => _log?.Information(eventName, detail);
    internal string Exception(string eventName, Exception exception) => _log?.Exception(eventName, exception) ??
        Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture)[..8].ToUpperInvariant();
}

using ResoDrive.Windows;

namespace ResoDrive.App;

internal sealed class UiDiagnosticLog
{
    private static readonly Lazy<UiDiagnosticLog> Instance = new(CreateCurrent);
    private readonly CrashDiagnosticLog? _log;

    internal UiDiagnosticLog(string path, long maximumBytes = CrashDiagnosticLog.DefaultMaximumBytes,
        AccountDataGuard? accountData = null)
        : this(new CrashDiagnosticLog(path, maximumBytes, accountData)) { }

    private UiDiagnosticLog(CrashDiagnosticLog log) => _log = log;
    private UiDiagnosticLog() { }

    private static UiDiagnosticLog CreateCurrent()
    {
        try { return new(new CrashDiagnosticLog(new ApplicationPaths(), "ui")); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // An invalid destination must not replace the original startup error.
            // Disable account logging rather than writing through a fallback path.
            return new();
        }
    }

    internal static UiDiagnosticLog Current => Instance.Value;
    internal void StartSession() => _log?.StartSession();
    internal void Information(string eventName, string? detail = null) => _log?.Information(eventName, detail);
    internal string Exception(string eventName, Exception exception) => _log?.Exception(eventName, exception) ??
        Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture)[..8].ToUpperInvariant();
}

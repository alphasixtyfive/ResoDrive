using ResoDrive.Windows;

namespace ResoDrive.Host;

/// <summary>Persists host logs without a queue that would lose its final entries on termination.</summary>
internal sealed class CrashDiagnosticLoggerProvider(CrashDiagnosticLog diagnostics) : ILoggerProvider
{
    private readonly CrashDiagnosticLog _diagnostics = diagnostics;
    private Exception? _fatalBackgroundException;
    public Exception? FatalBackgroundException => Volatile.Read(ref _fatalBackgroundException);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private sealed class FileLogger(CrashDiagnosticLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // The generic host catches BackgroundService failures, logs them, and stops
            // with a successful exit. Preserve the fatal failure for RunAsync to rethrow.
            if (category == "Microsoft.Extensions.Hosting.Internal.Host" &&
                eventId.Id == 10 && eventId.Name == "BackgroundServiceStoppingHost" &&
                logLevel == LogLevel.Critical && exception is not null)
                Interlocked.CompareExchange(ref provider._fatalBackgroundException, exception, null);

#pragma warning disable CA1031 // Formatting diagnostic data must not break host execution.
            string message;
            try { message = formatter(state, exception); }
            catch (Exception) { message = "Log message could not be formatted."; }
#pragma warning restore CA1031
            var detail = $"category={category} eventId={eventId.Id} {message}";
            if (exception is not null)
            {
                // Only one sanitized exception formatter and persistence policy for all roles.
                provider._diagnostics.Exception("host.exception", exception);
            }
            provider._diagnostics.Write(logLevel switch
            {
                LogLevel.Critical => "CRITICAL",
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARN",
                _ => "INFO"
            }, "host.log", detail);
        }
    }
}

using ResoDrive.Windows;

namespace ResoDrive.Host;

/// <summary>Persists host logs without a queue that would lose its final entries on termination.</summary>
internal sealed class HostDiagnosticLoggerProvider(ProcessDiagnosticLog diagnostics) : ILoggerProvider
{
    private readonly ProcessDiagnosticLog _diagnostics = diagnostics;
    private Exception? _fatalBackgroundException;
    public Exception? FatalBackgroundException => Volatile.Read(ref _fatalBackgroundException);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private sealed class FileLogger(HostDiagnosticLoggerProvider provider, string category) : ILogger
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
            }, DiagnosticEventName(category, eventId.Id), detail);
        }
    }

    private static string DiagnosticEventName(string category, int eventId)
    {
        // Category text is redacted as free text. Preserve known operation kinds
        // in the structured event field so exports need not inspect raw messages.
        if (category != "ResoDrive.Host.Worker") return "host.log";
        return eventId switch
        {
            1001 => "host.worker.initialization_failed",
            1002 => "host.worker.client_failed",
            1003 => "host.worker.operation_failed",
            1004 => "host.worker.operation_exception",
            1005 => "host.worker.scheduler_io_failed",
            1006 => "host.worker.drain_timeout",
            1007 => "host.worker.wipe_completed",
            1008 => "host.worker.wipe_failed",
            1009 => "host.worker.wipe_registration_failed",
            1010 => "host.worker.wipe_requested",
            1012 => "host.worker.wipe_enrollment_failed",
            1013 => "host.worker.runtime_identity_failed",
            1014 => "host.worker.runtime_identity_recovered",
            _ => "host.log",
        };
    }
}

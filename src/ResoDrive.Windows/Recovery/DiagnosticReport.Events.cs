using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ResoDrive.Windows;

public static partial class DiagnosticReport
{
    private static void AppendEvents(StringBuilder report, string role, DiagnosticLogExcerpt excerpt)
    {
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Recent {role} events (allowlisted metadata; details remain on this PC):");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Sources incomplete or unavailable: {excerpt.Unavailable}; excerpt truncated: {excerpt.Truncated || excerpt.Lines.Count > 1000}");
        var events = new List<(string Summary, bool IsStartup)>();
        foreach (var line in excerpt.Lines.Take(1000))
        {
            if (line is null) continue;
            // Only the record prefix and metadata at the beginning of detail are needed.
            var bounded = line.Length <= 4096 ? line : line[..4096];
            var header = EventHeader().Match(bounded);
            if (!header.Success || !DateTimeOffset.TryParse(header.Groups["time"].Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)) continue;
            var name = header.Groups["event"].Value;
            if (!(role == "UI" ? UiEvents : HostEvents).Contains(name)) continue;
            var detail = bounded[header.Length..];
            // Generic informational host traffic must not crowd out failures. Old
            // redacted host.log records cannot be assigned a Worker category safely.
            if (name == "host.log" && header.Groups["level"].Value == "INFO") continue;
            var summary = new StringBuilder();
            summary.Append(CultureInfo.InvariantCulture,
                $"{timestamp:O} level={header.Groups["level"].Value} event={name}");
            if (header.Groups["id"].Success)
                summary.Append(CultureInfo.InvariantCulture, $" errorId={header.Groups["id"].Value}");
            if (name == "process.started") AppendSessionIdentity(summary, detail);
            else AppendExceptionCategory(summary, detail);
            events.Add((summary.ToString(), name == "process.started"));
        }
        // Retain runtime history even when later warnings fill the excerpt. Preserve
        // source order after reserving a few slots for the latest process starts.
        var startupIndices = Enumerable.Range(0, events.Count)
            .Where(index => events[index].IsStartup).TakeLast(8).ToArray();
        var otherIndices = Enumerable.Range(0, events.Count)
            .Where(index => !events[index].IsStartup).TakeLast(80 - startupIndices.Length);
        var shownIndices = startupIndices.Concat(otherIndices).Order().ToArray();
        var omitted = events.Count - shownIndices.Length;
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Events shown: {shownIndices.Length}; event limit reached: {omitted > 0}; events omitted: {omitted}");
        foreach (var index in shownIndices) report.AppendLine(events[index].Summary);
    }

    private static void AppendSessionIdentity(StringBuilder summary, string detail)
    {
        var identity = SessionIdentity().Match(detail);
        if (!identity.Success) return;
        summary.Append(CultureInfo.InvariantCulture,
            $" version={VersionText(identity.Groups["version"].Value)} commit={Commit(identity.Groups["commit"].Value)} runtime={RuntimeVersion(identity.Groups["runtime"].Value)} processArchitecture={Architecture(identity.Groups["process"].Value)} osArchitecture={Architecture(identity.Groups["os"].Value)} windows={RuntimeVersion(identity.Groups["windows"].Value)}");
    }

    private static void AppendExceptionCategory(StringBuilder summary, string detail)
    {
        var exception = ExceptionMetadata().Match(detail);
        if (!exception.Success) return;
        // CLR type names and method names can be supplied by extensions. Export a
        // fixed category, never the original identifier, message or stack.
        var category = exception.Groups["type"].Value switch
        {
            "System.IO.FileNotFoundException" => "FileNotFound",
            "System.IO.FileLoadException" => "FileLoad",
            "System.TypeInitializationException" => "TypeInitialization",
            "System.BadImageFormatException" => "BadImageFormat",
            "System.OutOfMemoryException" => "OutOfMemory",
            "System.InvalidOperationException" => "InvalidOperation",
            "System.IO.IOException" => "IO",
            "System.UnauthorizedAccessException" => "AccessDenied",
            "System.OperationCanceledException" or "System.Threading.Tasks.TaskCanceledException" => "Cancelled",
            "System.ArgumentException" or "System.ArgumentOutOfRangeException" or
                "System.ArgumentNullException" => "Argument",
            "System.NullReferenceException" => "NullReference",
            "System.AggregateException" => "Aggregate",
            _ => "Other"
        };
        summary.Append(CultureInfo.InvariantCulture,
            $" exception={category} hresult={exception.Groups["hresult"].Value}");
    }

    private static string RuntimeVersion(string value) => RuntimeNumber().IsMatch(value) ? value : "Unknown";
    private static string Commit(string value) => SourceCommit().IsMatch(value) ? value : "Unknown";
    private static string Architecture(string value) => value is "X86" or "X64" or "Arm" or "Arm64" or
        "Wasm" or "S390x" or "LoongArch64" or "Armv6" or "Ppc64le" or "RiscV64" ? value : "Unknown";

    private static readonly HashSet<string> UiEvents = new(StringComparer.Ordinal)
    {
        "process.started", "startup.failed", "startup.fatal", "startup.error_dialog_failed",
        "startup.host_requested", "startup.wpf_ready", "startup.ready", "startup.window_shown", "shutdown",
        "exception.dispatcher", "exception.app_domain", "exception.unobserved_task",
        "ui.action.failed", "tray.action_failed", "tray.error_reporting_failed", "drive-settings.load",
        "activation.secondary_instance", "activation.listener_ready", "activation.request_received",
        "activation.dispatcher_unavailable", "activation.request_completed",
        "installer.prepare_started", "installer.prepare_failed", "installer.prepare_result_failed",
        "migration.handoff_failed", "recovery.settings_exported", "recovery.settings_imported"
    };

    private static readonly HashSet<string> HostEvents = new(StringComparer.Ordinal)
    {
        "process.started", "host.started", "host.stopped", "host.cancelled", "host.fatal", "host.exception", "host.log",
        "host.worker.initialization_failed", "host.worker.client_failed", "host.worker.operation_failed",
        "host.worker.operation_exception", "host.worker.scheduler_io_failed", "host.worker.drain_timeout",
        "host.worker.wipe_completed", "host.worker.wipe_failed", "host.worker.wipe_registration_failed",
        "host.worker.wipe_requested", "host.worker.wipe_enrollment_failed", "host.worker.runtime_identity_failed",
        "host.worker.runtime_identity_recovered"
    };

    [GeneratedRegex("\\A(?<time>\\d{4}-\\d{2}-\\d{2}T[\\d:.+Z-]+) level=(?<level>INFO|WARN|ERROR|CRITICAL) event=(?<event>[a-z][a-z0-9._-]{0,95})(?: errorId=(?<id>[A-F0-9]{8}))?(?: pid=\\d+ session=[a-f0-9]{32})?(?: detail=\"|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EventHeader();
    [GeneratedRegex(@"\Aversion=(?<version>\S{1,160}) commit=(?<commit>\S{1,64}) runtime=(?<runtime>\S{1,64}) processArchitecture=(?<process>\S{1,32}) osArchitecture=(?<os>\S{1,32}) windows=(?<windows>[^\s""']{1,64})(?:[\s""']|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdentity();
    [GeneratedRegex(@"\AexceptionType=(?<type>[A-Za-z0-9_.$+<>`]{1,256}) hresult=(?<hresult>0x[A-F0-9]{8})(?: |""|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionMetadata();
    [GeneratedRegex(@"\A\d{1,5}(?:\.\d{1,10}){1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeNumber();
    [GeneratedRegex(@"\A[A-Fa-f0-9]{7,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SourceCommit();
}
